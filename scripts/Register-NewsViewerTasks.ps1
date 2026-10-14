#Requires -RunAsAdministrator
# ============================================================================
#  SCOPE - REFERENCE INSTALLER FOR THE NEWSVIEWER LAUNCH SURFACE
#
#  Registers the two NewsViewer scheduled tasks from deploy/scheduled-tasks
#  (logon is a trigger of the Launch task), registers the NewsService event
#  source, removes the legacy Workstation Unlock task and removes the HKLM Run
#  value NewsViewer (the Run entry is gone, not kept). It is the dev/pilot path
#  for a machine set up without an installer; NewsService never registers
#  tasks (it only verifies them, docs/newsservice-spec.md). Tasks registered
#  here carry no nc-hash stamp, so NewsService verifies them by field
#  comparison only. The script writes no configuration.
#  NewsService.exe --emit-task-script produces a self-contained equivalent for
#  external use (the packaged, stamped build artifact).
#
#  EXIT CODE: 0 on success, 1 when registration throws or Verification finds
#  a missing task, a mismatch or a missing event source - on every terminal
#  path, never dependent on a leftover $LASTEXITCODE.
#
#  AUTHORITATIVE REFERENCES:
#    deploy/scheduled-tasks/README.md   what each task does and why
#    docs/packaging.md                  installer requirements
#    docs/production-deployment.md      rollout and dev-box setup
# ============================================================================
<#
.SYNOPSIS
    Registers (or, with -Uninstall, removes) the NewsViewer scheduled tasks and the NewsService event source.

.DESCRIPTION
    Tasks (definitions: deploy/scheduled-tasks/*.xml, UTF-16 LE with BOM):
      \<Company>\NewsCentral\NewsViewer Launch           logon (15 s delay) / unlock / RDP connect / console connect, no arguments
      \<Company>\NewsCentral\NewsViewer Content Arrived  NewsService event 2001, argument "--trigger content"

    For each task the script loads the XML, substitutes the Command and WorkingDirectory for the real
    install path, rewrites the Content Arrived event subscription to the configured log/source, writes a
    temporary UTF-16 copy, registers it with "schtasks /Create /XML ... /F" and deletes the temporary file.
    It then registers the NewsService event source when it is missing and removes the legacy Workstation
    Unlock task and any leftover dev-box tasks at the old \NewsCentral\ path, and finally removes the HKLM Run
    value "NewsViewer" (only when its data contains NewsViewer.exe; any other value of that name is kept;
    read through the 64-bit registry view): logon launches through the Launch task's logon trigger now.
    Running it twice gives the same
    result (/F replaces the tasks; an existing event source and already-removed old tasks are reported, not
    errors).

    The event log name and source MUST match NewsService's Logging:EventLog:LogName / SourceName, or the
    Content Arrived task never fires. The defaults here are NewsService's defaults (Application /
    NewsService); a different value triggers a warning so it is a conscious choice.

.PARAMETER Company
    The company name of the installed product (SolutionConstants.Company). Required in every mode (also with
    -ExePath and -Uninstall): it defines the Task Scheduler folder the tasks live in
    (\<Company>\NewsCentral\), the default install folder and the legacy task path.

.PARAMETER InstallDir
    NewsViewer install folder. Default: C:\Program Files\<Company>\NewsCentral\NewsViewer.

.PARAMETER ExePath
    Full path to NewsViewer.exe; overrides -InstallDir. For dev boxes, e.g. a Debug build. The working
    directory becomes the exe's folder. The file must exist.

.PARAMETER EventLogName
    Event log of the ContentChanged event. Default: Application.

.PARAMETER EventSourceName
    Event source (provider) of the ContentChanged event. Default: NewsService.

.PARAMETER LegacyTaskName
    The legacy Workstation Unlock task to remove. Default: \<Company>\NewsCentral\NewsViewer
    (docs/packaging.md). "{Company}" in the value is replaced by -Company.

.PARAMETER TaskXmlDir
    Folder holding the task XML files. Default: deploy\scheduled-tasks next to this script's repository root.

.PARAMETER Uninstall
    Removes both new tasks. Does not touch the event source, the legacy task or the HKLM Run value.
    Uninstalling NewsViewer MUST remove the tasks (this switch, or the installer's equivalent): NewsService
    never creates or removes tasks, so nothing else will.

.EXAMPLE
    .\scripts\Register-NewsViewerTasks.ps1 -Company Contoso

.EXAMPLE
    # Dev box: point the tasks at a Debug build
    $exe = Get-ChildItem .\NewsViewer\bin\Debug -Recurse -Filter NewsViewer.exe | Select-Object -First 1 -ExpandProperty FullName
    .\scripts\Register-NewsViewerTasks.ps1 -Company Contoso -ExePath $exe

.EXAMPLE
    .\scripts\Register-NewsViewerTasks.ps1 -Company Contoso -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Company,
    [string]$InstallDir,
    [string]$ExePath,
    [string]$EventLogName    = "Application",
    [string]$EventSourceName = "NewsService",
    [string]$LegacyTaskName  = '\{Company}\NewsCentral\NewsViewer',
    [string]$TaskXmlDir,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# ---- 64-bit host required -----------------------------------------------------
# The task hash covers Command and WorkingDirectory after environment variables are expanded, and a 32-bit
# host expands %ProgramFiles% to the "Program Files (x86)" folder while the 64-bit NewsService expands it to
# the "Program Files" folder: the script would register and report success, and NewsService would later report
# nc-hash drift with no clue why. A 32-bit host also reads the HKLM Run value through the redirected
# WOW6432Node view. So this is refused up front, before anything is read or written, in every mode
# (-WhatIf and -Uninstall included) rather than compensated for.
if (-not [Environment]::Is64BitProcess) {
    Write-Host 'ERROR: this script must be run from 64-bit PowerShell; this is a 32-bit PowerShell host.' -ForegroundColor Red
    Write-Host '  The task hash depends on how %ProgramFiles% expands (the Program Files (x86) folder in a 32-bit host), and a' -ForegroundColor Red
    Write-Host '  32-bit host reads the redirected registry view. Nothing was read or changed.' -ForegroundColor Red
    Write-Host '  Remedy: run it with %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe or, when the caller is itself' -ForegroundColor Red
    Write-Host '  32-bit (some SCCM/Intune launchers), with %SystemRoot%\sysnative\WindowsPowerShell\v1.0\powershell.exe.' -ForegroundColor Red
    exit 1
}

# ── Constants ────────────────────────────────────────────────────────────────
if ($Company -notmatch '^[^\\/:*?"<>|]+$') {
    throw "-Company must not contain any of \ / : * ? `" < > | (it becomes a Task Scheduler folder name)."
}
$LaunchTask  = "\$Company\NewsCentral\NewsViewer Launch"
$ContentTask = "\$Company\NewsCentral\NewsViewer Content Arrived"
# Earlier dev-box registrations lived directly under \NewsCentral\ (no company folder); install removes them.
$OldDevTasks = @('\NewsCentral\NewsViewer Launch', '\NewsCentral\NewsViewer Content Arrived')
$ContentEventId = 2001
# NewsService defaults (NewsService/appsettings.json Logging:EventLog) - a deviation is warned about.
$DefaultLogName    = "Application"
$DefaultSourceName = "NewsService"

if (-not $TaskXmlDir) { $TaskXmlDir = Join-Path (Split-Path $PSScriptRoot -Parent) "deploy\scheduled-tasks" }

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }

# What Verification must find, filled by Register-Task.
$script:Expected = @{}

# Canonical form of a path for comparing (same steps as NewsService's TaskPathNormalizer): trim, strip
# surrounding quotes, expand environment variables, '/' to '\', drop a trailing '\', lower-case.
function Get-NormalizedPath {
    param([string]$Path)
    $p = $Path.Trim().Trim('"').Trim()
    $p = [System.Environment]::ExpandEnvironmentVariables($p)
    $p = $p.Replace('/', '\').TrimEnd('\')
    return $p.ToLowerInvariant()
}

# One entry per trigger: the element name, plus the state-change kind for session triggers (sorted).
function Get-TriggerSignature {
    param($Doc, $Ns)
    $list = @()
    $triggers = $Doc.SelectSingleNode('//t:Triggers', $Ns)
    if ($triggers) {
        foreach ($node in $triggers.ChildNodes) {
            if ($node.NodeType -ne 'Element') { continue }
            $state = $node.SelectSingleNode('t:StateChange', $Ns)
            $list += $(if ($state) { $node.LocalName + ':' + $state.InnerText.Trim() } else { $node.LocalName })
        }
    }
    return @($list | Sort-Object)
}

# Compares the registered definition with what Register-Task just wrote. Returns the problems found
# (empty = it matches). Paths are compared in their normalised form.
function Compare-RegisteredTask {
    param([string]$Name)
    $expected = $script:Expected[$Name]
    if (-not $expected) { return @("no expectation was recorded for this task") }
    $path = (Split-Path $Name -Parent) + '\'
    $leaf = Split-Path $Name -Leaf
    try { $xmlText = Export-ScheduledTask -TaskName $leaf -TaskPath $path -ErrorAction Stop }
    catch { return @("cannot read the registered definition: $($_.Exception.Message)") }

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.LoadXml([string]$xmlText)
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $text = { param($xpath) $n = $doc.SelectSingleNode($xpath, $ns); if ($n) { $n.InnerText.Trim() } else { '' } }

    $problems = @()
    $command = & $text '//t:Exec/t:Command'
    if ((Get-NormalizedPath $command) -ne (Get-NormalizedPath $expected.Command)) {
        $problems += "wrong command - expected '$($expected.Command)', found '$command'"
    }
    $workDir = & $text '//t:Exec/t:WorkingDirectory'
    if ((Get-NormalizedPath $workDir) -ne (Get-NormalizedPath $expected.WorkingDir)) {
        $problems += "wrong working directory - expected '$($expected.WorkingDir)', found '$workDir'"
    }
    if ($null -ne $expected.Subscription) {
        $subscription = & $text '//t:EventTrigger/t:Subscription'
        if ($subscription -ne $expected.Subscription.Trim()) {
            $problems += "wrong event subscription - expected '$($expected.Subscription)', found '$subscription'"
        }
    }
    $found = @(Get-TriggerSignature $doc $ns)
    if (($found -join ',') -ne (@($expected.Triggers) -join ',')) {
        $problems += "wrong trigger set - expected [$(@($expected.Triggers) -join ', ')], found [$($found -join ', ')]"
    }
    return $problems
}

# Runs schtasks and returns its exit code (output is returned through $script:SchtasksOutput).
function Invoke-Schtasks {
    param([string[]]$Arguments)
    # Windows PowerShell 5.1 turns native stderr redirected with 2>&1 into ErrorRecords, and under
    # $ErrorActionPreference = "Stop" the first one is a terminating NativeCommandError - but a missing task
    # (exit 1 + stderr) is an expected outcome here. So the call runs with 'Continue' (restored in finally),
    # every output item is converted to text, and success is decided by $LASTEXITCODE alone.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $script:SchtasksOutput = (& schtasks.exe @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    return $code
}

function Test-TaskExists {
    param([string]$Name)
    return ((Invoke-Schtasks @("/Query", "/TN", $Name)) -eq 0)
}

# Deletes a task if it exists. Returns $true when it was removed, $false when there was nothing to remove (a
# missing task is an expected outcome, never an error). Any other failure throws with schtasks' own output.
function Remove-TaskIfPresent {
    param([string]$Name)
    if (-not (Test-TaskExists $Name)) { return $false }
    if ((Invoke-Schtasks @("/Delete", "/TN", $Name, "/F")) -ne 0) {
        $output = $script:SchtasksOutput.Trim()
        if (-not (Test-TaskExists $Name)) { return $false }   # vanished between the query and the delete
        throw "schtasks /Delete failed for '$Name': $output"
    }
    return $true
}

# ── Uninstall ────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Step "Removing NewsViewer scheduled tasks"
    foreach ($name in @($LaunchTask, $ContentTask)) {
        if (Remove-TaskIfPresent $name) { Write-Host "  removed   $name" }
        else { Write-Host "  not found $name" }
    }
    Write-Host ""
    Write-Host "The event source, the legacy task and the HKLM Run value were not touched." -ForegroundColor DarkGray
    Write-Host "Uninstalling NewsViewer must remove these tasks (this switch, or the installer's equivalent): NewsService never removes its own tasks." -ForegroundColor DarkGray
    exit 0
}

# ── Parameter validation ─────────────────────────────────────────────────────
foreach ($pair in @(@("EventLogName", $EventLogName), @("EventSourceName", $EventSourceName))) {
    # The values are embedded in an XPath subscription query inside XML: keep them free of quotes and markup.
    if ([string]::IsNullOrWhiteSpace($pair[1]) -or $pair[1] -notmatch '^[^''"<>&]+$') {
        throw "-$($pair[0]) must be non-empty and must not contain quotes, <, > or &."
    }
}

if ($ExePath) {
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "-ExePath '$ExePath' does not exist." }
    $exe = (Resolve-Path -LiteralPath $ExePath).Path
}
else {
    if (-not $InstallDir) { $InstallDir = "C:\Program Files\$Company\NewsCentral\NewsViewer" }
    $exe = Join-Path $InstallDir "NewsViewer.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Write-Warning "$exe does not exist yet. The tasks are registered anyway; install NewsViewer there before they fire."
    }
}
$workingDir = Split-Path $exe -Parent

if ($EventLogName -ne $DefaultLogName -or $EventSourceName -ne $DefaultSourceName) {
    Write-Warning ("Event log/source '$EventLogName' / '$EventSourceName' differ from the NewsService defaults " +
        "('$DefaultLogName' / '$DefaultSourceName'). They MUST equal NewsService's Logging:EventLog:LogName / " +
        "SourceName, or the Content Arrived task never fires.")
}

foreach ($file in @("NewsViewer-Launch.xml", "NewsViewer-ContentArrived.xml")) {
    if (-not (Test-Path -LiteralPath (Join-Path $TaskXmlDir $file))) { throw "Task definition not found: $(Join-Path $TaskXmlDir $file)" }
}

# ── Task registration ────────────────────────────────────────────────────────
# Loads the task XML, substitutes the install path (and, for Content Arrived, the subscription), writes a
# temporary UTF-16 LE (BOM) copy - the encoding schtasks accepts reliably - registers it and deletes it.
function Register-Task {
    param([string]$XmlFile, [string]$TaskName, [switch]$RewriteSubscription)

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.Load((Join-Path $TaskXmlDir $XmlFile))

    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")

    $command = $doc.SelectSingleNode("//t:Exec/t:Command", $ns)
    $workDir = $doc.SelectSingleNode("//t:Exec/t:WorkingDirectory", $ns)
    if (-not $command -or -not $workDir) { throw "$XmlFile has no Exec/Command or Exec/WorkingDirectory." }
    # The task's own path comes from RegistrationInfo/URI ("{Company}" placeholder in the shipped file).
    $uri = $doc.SelectSingleNode("//t:RegistrationInfo/t:URI", $ns)
    if (-not $uri) { throw "$XmlFile has no RegistrationInfo/URI." }
    $uri.InnerText = $TaskName
    $command.InnerText = '"' + $exe + '"'
    $workDir.InnerText = $workingDir

    if ($RewriteSubscription) {
        $subscription = $doc.SelectSingleNode("//t:EventTrigger/t:Subscription", $ns)
        if (-not $subscription) { throw "$XmlFile has no EventTrigger/Subscription." }
        $subscription.InnerText =
            "<QueryList><Query Id=""0"" Path=""$EventLogName""><Select Path=""$EventLogName"">" +
            "*[System[Provider[@Name='$EventSourceName'] and EventID=$ContentEventId]]" +
            "</Select></Query></QueryList>"
    }
    # Record what Verification must find afterwards.
    $script:Expected[$TaskName] = @{
        Command      = $command.InnerText
        WorkingDir   = $workDir.InnerText
        Subscription = $(if ($RewriteSubscription) { $subscription.InnerText } else { $null })
        Triggers     = @(Get-TriggerSignature $doc $ns)
    }


    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("NewsViewerTask-" + [guid]::NewGuid().ToString("N") + ".xml")
    try {
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Encoding = New-Object System.Text.UnicodeEncoding($false, $true)   # UTF-16 LE, BOM
        $settings.Indent = $false
        $writer = [System.Xml.XmlWriter]::Create($tmp, $settings)
        try { $doc.Save($writer) } finally { $writer.Dispose() }

        if ((Invoke-Schtasks @("/Create", "/TN", $TaskName, "/XML", $tmp, "/F")) -ne 0) {
            throw "schtasks /Create failed for '$TaskName': $($script:SchtasksOutput.Trim())"
        }
        Write-Host "  registered $TaskName"
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

Write-Step "Registering NewsViewer scheduled tasks (exe: $exe)"
Register-Task -XmlFile "NewsViewer-Launch.xml"         -TaskName $LaunchTask
Register-Task -XmlFile "NewsViewer-ContentArrived.xml" -TaskName $ContentTask -RewriteSubscription

# ── Event source ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Step "NewsService event source"
if ([System.Diagnostics.EventLog]::SourceExists($EventSourceName)) {
    $existingLog = [System.Diagnostics.EventLog]::LogNameFromSourceName($EventSourceName, ".")
    if ($existingLog -ne $EventLogName) {
        Write-Warning "Event source '$EventSourceName' already exists under log '$existingLog', not '$EventLogName'. The task subscribes to '$EventLogName'."
    }
    Write-Host "  exists    $EventSourceName (log: $existingLog)"
}
else {
    [System.Diagnostics.EventLog]::CreateEventSource($EventSourceName, $EventLogName)
    Write-Host "  created   $EventSourceName (log: $EventLogName)"
}

# ── Legacy Workstation Unlock task ───────────────────────────────────────────
Write-Host ""
Write-Step "Legacy Workstation Unlock task and old dev-box tasks"
$legacy = $LegacyTaskName.Replace("{Company}", $Company)
foreach ($old in @($legacy) + $OldDevTasks) {
    $label = if ($old -eq $legacy) { "legacy " } else { "old dev " }
    if (Remove-TaskIfPresent $old) { Write-Host "  removed   $label$old" }
    else { Write-Host "  not found $label$old (nothing to remove)" }
}

# ── Legacy HKLM Run value ────────────────────────────────────────────────────
# Same rule as NewsService (ViewerTaskProvisioner): value name exactly "NewsViewer", removed only when its data
# contains NewsViewer.exe, read through the 64-bit registry view; nothing else under Run is touched. The tasks
# are in place at this point (a failed registration threw above), so the machine keeps a logon launch.
Write-Host ""
Write-Step "Legacy HKLM Run value"
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try {
    $run = $hklm.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', $true)
    $runData = if ($run) { $run.GetValue('NewsViewer') } else { $null }
    if ($null -eq $runData) {
        Write-Host "  not found HKLM Run value NewsViewer (nothing to remove)"
    }
    elseif (("$runData") -like '*NewsViewer.exe*') {
        $run.DeleteValue('NewsViewer')
        Write-Host "  removed   HKLM Run value NewsViewer ($runData)"
    }
    else {
        Write-Host "  kept      HKLM Run value NewsViewer does not point at NewsViewer.exe ($runData)"
    }
}
finally {
    if ($run) { $run.Dispose() }
    $hklm.Dispose()
}

# ── Verification ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Step "Verification"
# TASK_SESSION_STATE_CHANGE_TYPE values, as Get-ScheduledTask reports them.
$stateNames = @{ 1 = "ConsoleConnect"; 2 = "ConsoleDisconnect"; 3 = "RemoteConnect"; 4 = "RemoteDisconnect"; 7 = "SessionLock"; 8 = "SessionUnlock" }
$failures = 0
foreach ($name in @($LaunchTask, $ContentTask)) {
    $path = (Split-Path $name -Parent) + "\"
    $leaf = Split-Path $name -Leaf
    $task = Get-ScheduledTask -TaskPath $path -TaskName $leaf -ErrorAction SilentlyContinue
    Write-Host "  $name" -ForegroundColor Yellow
    if (-not $task) { Write-Host "    MISSING" -ForegroundColor Red; $failures++; continue }
    foreach ($trigger in $task.Triggers) {
        $kind = $trigger.CimClass.CimClassName -replace '^MSFT_Task', '' -replace 'Trigger$', ''
        $detail = if ($trigger.PSObject.Properties["StateChange"]) { "StateChange=$($stateNames[[int]$trigger.StateChange])" }
                  elseif ($kind -eq 'Logon') { "Delay=$(if ($trigger.Delay) { $trigger.Delay } else { '(none)' })  UserId=$(if ($trigger.UserId) { $trigger.UserId } else { '(any user)' })" }
                  elseif ($trigger.PSObject.Properties["Subscription"]) { "Subscription=$($trigger.Subscription)" }
                  else { "" }
        Write-Host "    trigger   $kind  $detail"
    }
    foreach ($action in $task.Actions) {
        Write-Host "    command   $($action.Execute)"
        Write-Host "    arguments $(if ($action.Arguments) { $action.Arguments } else { '(none)' })"
        Write-Host "    workdir   $($action.WorkingDirectory)"
    }
    Write-Host "    principal $($task.Principal.GroupId)  run level $($task.Principal.RunLevel)"
    foreach ($problem in @(Compare-RegisteredTask $name)) {
        Write-Host "    MISMATCH  $problem" -ForegroundColor Red
        $failures++
    }
}
$sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventSourceName)
Write-Host "  event source '$EventSourceName': $(if ($sourceExists) { 'present' } else { 'MISSING' })" `
    -ForegroundColor $(if ($sourceExists) { "Green" } else { "Red" })
if (-not $sourceExists) { $failures++ }
Write-Host ""

# Explicit exit code on every terminal path: never fall off the end, where the exit code would be whatever a
# native probe (the schtasks query for a legacy task that rightly does not exist) left in $LASTEXITCODE.
if ($failures -gt 0) {
    Write-Host "VERIFICATION FAILED: $failures problem(s) above. The launch surface is NOT established." -ForegroundColor Red
    exit 1
}
Write-Host "Verification passed: both tasks are registered as intended." -ForegroundColor Green
exit 0
