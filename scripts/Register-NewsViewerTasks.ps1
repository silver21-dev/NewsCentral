#Requires -RunAsAdministrator
# ============================================================================
#  SCOPE - REFERENCE INSTALLER FOR THE NEWSVIEWER LAUNCH SURFACE
#
#  Registers the two NewsViewer scheduled tasks from deploy/scheduled-tasks,
#  registers the NewsService event source, and removes the legacy Workstation
#  Unlock task. It is the reference implementation of what the installer must
#  do (docs/packaging.md -> "NewsViewer launch surface") and the dev/pilot
#  shortcut for a machine without an installer. It does NOT touch the HKLM Run
#  entry (logon launch, unchanged) and writes no configuration.
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
      \NewsCentral\NewsViewer Launch           unlock / RDP connect / console connect, no arguments
      \NewsCentral\NewsViewer Content Arrived  NewsService event 2001, argument "--trigger content"

    For each task the script loads the XML, substitutes the Command and WorkingDirectory for the real
    install path, rewrites the Content Arrived event subscription to the configured log/source, writes a
    temporary UTF-16 copy, registers it with "schtasks /Create /XML ... /F" and deletes the temporary file.
    It then registers the NewsService event source when it is missing and removes the legacy Workstation
    Unlock task. Running it twice gives the same result (/F replaces the tasks; an existing event source
    and an already-removed legacy task are reported, not errors).

    The event log name and source MUST match NewsService's Logging:EventLog:LogName / SourceName, or the
    Content Arrived task never fires. The defaults here are NewsService's defaults (Application /
    NewsService); a different value triggers a warning so it is a conscious choice.

.PARAMETER Company
    The company name of the installed product (SolutionConstants.Company; defines the install folder and the
    legacy task path). Required unless -ExePath is given. Without it the legacy task cannot be located by
    its default name and is skipped with a warning (pass -LegacyTaskName to remove it anyway).

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
    Removes both new tasks. Does not touch the event source, the legacy task or the HKLM Run entry.

.EXAMPLE
    .\scripts\Register-NewsViewerTasks.ps1 -Company Contoso

.EXAMPLE
    # Dev box: point the tasks at a Debug build
    $exe = Get-ChildItem .\NewsViewer\bin\Debug -Recurse -Filter NewsViewer.exe | Select-Object -First 1 -ExpandProperty FullName
    .\scripts\Register-NewsViewerTasks.ps1 -ExePath $exe

.EXAMPLE
    .\scripts\Register-NewsViewerTasks.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Company,
    [string]$InstallDir,
    [string]$ExePath,
    [string]$EventLogName    = "Application",
    [string]$EventSourceName = "NewsService",
    [string]$LegacyTaskName  = '\{Company}\NewsCentral\NewsViewer',
    [string]$TaskXmlDir,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# ── Constants ────────────────────────────────────────────────────────────────
$LaunchTask  = '\NewsCentral\NewsViewer Launch'
$ContentTask = '\NewsCentral\NewsViewer Content Arrived'
$ContentEventId = 2001
# NewsService defaults (NewsService/appsettings.json Logging:EventLog) - a deviation is warned about.
$DefaultLogName    = "Application"
$DefaultSourceName = "NewsService"

if (-not $TaskXmlDir) { $TaskXmlDir = Join-Path (Split-Path $PSScriptRoot -Parent) "deploy\scheduled-tasks" }

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }

# Runs schtasks and returns its exit code (output is returned through $script:SchtasksOutput).
function Invoke-Schtasks {
    param([string[]]$Arguments)
    $script:SchtasksOutput = & schtasks.exe @Arguments 2>&1 | Out-String
    return $LASTEXITCODE
}

function Test-TaskExists {
    param([string]$Name)
    return ((Invoke-Schtasks @("/Query", "/TN", $Name)) -eq 0)
}

# ── Uninstall ────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Step "Removing NewsViewer scheduled tasks"
    foreach ($name in @($LaunchTask, $ContentTask)) {
        if (Test-TaskExists $name) {
            if ((Invoke-Schtasks @("/Delete", "/TN", $name, "/F")) -ne 0) {
                throw "schtasks /Delete failed for '$name': $($script:SchtasksOutput.Trim())"
            }
            Write-Host "  removed   $name"
        }
        else {
            Write-Host "  not found $name"
        }
    }
    Write-Host ""
    Write-Host "The event source, the legacy task and the HKLM Run entry were not touched." -ForegroundColor DarkGray
    return
}

# ── Parameter validation ─────────────────────────────────────────────────────
if (-not $ExePath -and [string]::IsNullOrWhiteSpace($Company)) {
    throw "-Company is required unless -ExePath is given."
}
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
Write-Step "Legacy Workstation Unlock task"
$legacy = $LegacyTaskName
if ($legacy -match '\{Company\}') {
    if ([string]::IsNullOrWhiteSpace($Company)) {
        Write-Warning "-LegacyTaskName contains {Company} but -Company was not given: the legacy task was NOT looked up. Pass -Company or an explicit -LegacyTaskName."
        $legacy = $null
    }
    else { $legacy = $legacy.Replace("{Company}", $Company) }
}
if ($legacy) {
    if (Test-TaskExists $legacy) {
        if ((Invoke-Schtasks @("/Delete", "/TN", $legacy, "/F")) -ne 0) {
            throw "schtasks /Delete failed for '$legacy': $($script:SchtasksOutput.Trim())"
        }
        Write-Host "  removed   $legacy"
    }
    else {
        Write-Host "  not found $legacy (nothing to remove)"
    }
}

# ── Verification ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Step "Verification"
# TASK_SESSION_STATE_CHANGE_TYPE values, as Get-ScheduledTask reports them.
$stateNames = @{ 1 = "ConsoleConnect"; 2 = "ConsoleDisconnect"; 3 = "RemoteConnect"; 4 = "RemoteDisconnect"; 7 = "SessionLock"; 8 = "SessionUnlock" }
foreach ($name in @($LaunchTask, $ContentTask)) {
    $path = (Split-Path $name -Parent) + "\"
    $leaf = Split-Path $name -Leaf
    $task = Get-ScheduledTask -TaskPath $path -TaskName $leaf -ErrorAction SilentlyContinue
    Write-Host "  $name" -ForegroundColor Yellow
    if (-not $task) { Write-Host "    MISSING" -ForegroundColor Red; continue }
    foreach ($trigger in $task.Triggers) {
        $kind = $trigger.CimClass.CimClassName -replace '^MSFT_Task', '' -replace 'Trigger$', ''
        $detail = if ($trigger.PSObject.Properties["StateChange"]) { "StateChange=$($stateNames[[int]$trigger.StateChange])" }
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
}
$sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventSourceName)
Write-Host "  event source '$EventSourceName': $(if ($sourceExists) { 'present' } else { 'MISSING' })" `
    -ForegroundColor $(if ($sourceExists) { "Green" } else { "Red" })
Write-Host ""
