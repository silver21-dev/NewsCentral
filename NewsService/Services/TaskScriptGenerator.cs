using System.Text;
using NewsCentral.Configuration;

namespace NewsService.Services;

/// <summary>Volatile, informational header values; <see cref="TaskScriptEmitter"/> ignores the timestamp when comparing.</summary>
internal sealed record TaskScriptHeader(string Version, DateTimeOffset GeneratedUtc, string Machine);

/// <summary>
/// Generates the self-contained registration script (ASCII, PowerShell 5.1). Pure: identical inputs
/// give byte-identical output. Task parameters come from <see cref="TaskDefinitionInputs"/>, task
/// names and legacy names from <see cref="TaskDefinitionBuilder"/>, and the task XML is the embedded
/// template, base64 of its exact bytes - the same resource the service registers from. The script
/// carries no secret, only registration parameters.
/// </summary>
internal static class TaskScriptGenerator
{
    internal const string GeneratedMarker = "# Generated:";

    public static string Generate(
        TaskDefinitionInputs inputs, TaskScriptHeader header, Func<ViewerTaskKind, byte[]> loadTemplateBytes)
    {
        if (string.IsNullOrWhiteSpace(inputs.Company)) throw new ArgumentException("Company is empty.", nameof(inputs));
        if (inputs.Company.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("Company must not contain any of \\ / : * ? \" < > |.", nameof(inputs));
        if (string.IsNullOrWhiteSpace(inputs.ExePath)) throw new ArgumentException("ExePath is empty.", nameof(inputs));
        if (!TaskDefinitionBuilder.IsValidEventName(inputs.EventLogName) || !TaskDefinitionBuilder.IsValidEventName(inputs.EventSourceName))
            throw new ArgumentException(
                "Event log name and source must be non-empty and must not contain quotes, <, > or &.", nameof(inputs));

        var workingDir = Path.GetDirectoryName(inputs.ExePath) ?? string.Empty;
        var legacy = string.Join(",\n    ", TaskDefinitionBuilder.LegacyTaskNames(inputs.Company).Select(Literal));

        var script = Template
            .Replace("@@VERSION@@", Comment(header.Version))
            .Replace("@@TIMESTAMP@@", header.GeneratedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
            .Replace("@@MACHINE@@", Comment(header.Machine))
            .Replace("@@COMPANY@@", Literal(inputs.Company))
            .Replace("@@LAUNCHTASK@@", Literal(TaskDefinitionBuilder.TaskName(ViewerTaskKind.Launch, inputs.Company)))
            .Replace("@@CONTENTTASK@@", Literal(TaskDefinitionBuilder.TaskName(ViewerTaskKind.ContentArrived, inputs.Company)))
            .Replace("@@EXEPATH@@", Literal(inputs.ExePath))
            .Replace("@@WORKDIR@@", Literal(workingDir))
            .Replace("@@LOGNAME@@", Literal(inputs.EventLogName))
            .Replace("@@SOURCE@@", Literal(inputs.EventSourceName))
            .Replace("@@EVENTID@@", ServiceEventIds.ContentChanged.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@LEGACYTASKS@@", legacy)
            .Replace("@@LAUNCHXML@@", Base64Lines(loadTemplateBytes(ViewerTaskKind.Launch)))
            .Replace("@@CONTENTXML@@", Base64Lines(loadTemplateBytes(ViewerTaskKind.ContentArrived)));

        // One line-ending convention, whatever the source file uses.
        return script.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>A PowerShell single-quoted literal; non-ASCII characters become [char] escapes so the script stays ASCII.</summary>
    internal static string Literal(string value)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException("Value contains a control character.", nameof(value));
        var sb = new StringBuilder();
        var open = false;
        var parts = 0;
        void Close() { if (open) { sb.Append('\''); open = false; } }
        foreach (var ch in value)
        {
            if (ch < 0x80)
            {
                if (!open)
                {
                    if (parts++ > 0) sb.Append(" + ");
                    sb.Append('\'');
                    open = true;
                }
                sb.Append(ch);
                if (ch == '\'') sb.Append('\'');
            }
            else
            {
                Close();
                if (parts++ > 0) sb.Append(" + ");
                sb.Append($"[char]0x{(int)ch:X4}");
            }
        }
        Close();
        if (parts == 0) return "''";
        return parts == 1 ? sb.ToString() : "(" + sb + ")";
    }

    private static string Comment(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value) sb.Append(ch is >= ' ' and < (char)0x7F ? ch : '?');
        return sb.ToString();
    }

    private static string Base64Lines(byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        var lines = new List<string>();
        for (var i = 0; i < b64.Length; i += 72)
            lines.Add("    '" + b64.Substring(i, Math.Min(72, b64.Length - i)) + "'");
        return string.Join(",\n", lines);
    }

    private const string Template = """
#Requires -RunAsAdministrator
# ============================================================================
#  SCOPE - GENERATED NEWSVIEWER LAUNCH-SURFACE REGISTRATION SCRIPT
#
#  This file was GENERATED at build time by NewsService.exe --emit-task-script
#  and ships in the package; no running service writes it. Do NOT
#  hand-edit it: regenerate it instead. It is self-contained and needs no repo
#  checkout, no NewsService install and no knowledge of the Company value.
#
#  Registers the two NewsViewer scheduled tasks, creates the NewsService event
#  source when missing, removes the legacy Workstation Unlock task and the
#  legacy HKLM Run value "NewsViewer", then prints a Verification readout.
#  With -Uninstall it removes both tasks and the Run value (NewsService never
#  modifies Task Scheduler, so the installer owns their creation and removal).
#  It writes no configuration and contains no secret.
#
#  Reference implementation: scripts/Register-NewsViewerTasks.ps1
#  Details: docs/packaging.md (launch surface)
# ============================================================================
# Generator: NewsService @@VERSION@@
# Generated: @@TIMESTAMP@@
# Machine:   @@MACHINE@@
<#
.SYNOPSIS
    Registers (or, with -Uninstall, removes) the NewsViewer scheduled tasks and the NewsService event source.

.DESCRIPTION
    Idempotent: running it twice gives the same result. Supports -WhatIf.

.PARAMETER Uninstall
    Removes both NewsViewer tasks and the legacy HKLM Run value. The event source and the legacy tasks are not touched.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# ---- Baked in at generation time ---------------------------------------------
$Company         = @@COMPANY@@
$LaunchTask      = @@LAUNCHTASK@@
$ContentTask     = @@CONTENTTASK@@
$ExePath         = @@EXEPATH@@
$WorkingDir      = @@WORKDIR@@
$EventLogName    = @@LOGNAME@@
$EventSourceName = @@SOURCE@@
$ContentEventId  = @@EVENTID@@
$script:Expected  = @{}   # what Verification must find, filled by Register-Task
$LegacyTasks     = @(
    @@LEGACYTASKS@@
)

# ---- Task definitions: base64 of the exact file bytes (UTF-16 LE with BOM) ---
$LaunchXmlBase64 = @(
@@LAUNCHXML@@
) -join ''
$ContentXmlBase64 = @(
@@CONTENTXML@@
) -join ''

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }

# Runs schtasks and returns its exit code (output is returned through $script:SchtasksOutput).
function Invoke-Schtasks {
    param([string[]]$Arguments)
    # A missing task (exit 1 + stderr) is an expected outcome: run with 'Continue' and decide on the exit code alone.
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
    return ((Invoke-Schtasks @('/Query', '/TN', $Name)) -eq 0)
}

# Canonical form of a path for hashing and comparing (same steps as NewsService's TaskPathNormalizer):
# trim, strip surrounding quotes, expand environment variables, '/' to '\', drop a trailing '\', lower-case.
function Get-NormalizedPath {
    param([string]$Path)
    $p = $Path.Trim().Trim('"').Trim()
    $p = [System.Environment]::ExpandEnvironmentVariables($p)
    $p = $p.Replace('/', '\').TrimEnd('\')
    return $p.ToLowerInvariant()
}

function Test-EventSourceExists { param([string]$Name) return [System.Diagnostics.EventLog]::SourceExists($Name) }
function Get-EventSourceLogName { param([string]$Name) return [System.Diagnostics.EventLog]::LogNameFromSourceName($Name, '.') }
function New-EventSourceNow { param([string]$Name, [string]$Log) [System.Diagnostics.EventLog]::CreateEventSource($Name, $Log) }

# Deletes a task if it exists. Returns 'removed', 'absent' (nothing to remove) or 'whatif' (present, but -WhatIf).
# Any other failure throws.
function Remove-TaskIfPresent {
    param([string]$Name)
    if (-not (Test-TaskExists $Name)) { return 'absent' }
    if (-not $PSCmdlet.ShouldProcess($Name, 'Remove scheduled task')) { return 'whatif' }
    if ((Invoke-Schtasks @('/Delete', '/TN', $Name, '/F')) -ne 0) {
        $output = $script:SchtasksOutput.Trim()
        if (-not (Test-TaskExists $Name)) { return 'absent' }
        throw "schtasks /Delete failed for '$Name': $output"
    }
    return 'removed'
}

# Same wording as scripts/Register-NewsViewerTasks.ps1; -WhatIf gets its own line.
function Write-RemoveResult {
    param([string]$Result, [string]$Name, [string]$Label = '')
    switch ($Result) {
        'removed' { Write-Host "  removed   $Label$Name" }
        'whatif'  { Write-Host "  would remove $Label$Name (-WhatIf: nothing was changed)" }
        default   { Write-Host "  not found $Label$Name (nothing to remove)" }
    }
}

# Legacy HKLM Run value: value name exactly "NewsViewer", removed only when its data contains NewsViewer.exe,
# read through the 64-bit registry view; nothing else under Run is touched (same rule as
# scripts/Register-NewsViewerTasks.ps1). The Launch task has a logon trigger, so a Run entry is a duplicate
# launch, and a stale one pointing at an old install fails at every logon.
function Remove-LegacyRunValue {
    Write-Host ''
    Write-Step 'Legacy HKLM Run value'
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $run = $null
    try {
        # Read-only first: -WhatIf (and a value that is absent or not ours) never needs write access.
        $run = $hklm.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', $false)
        $runData = if ($run) { $run.GetValue('NewsViewer') } else { $null }
        if ($null -eq $runData) {
            Write-Host '  not found HKLM Run value NewsViewer (nothing to remove)'
        }
        elseif (("$runData") -like '*NewsViewer.exe*') {
            if ($PSCmdlet.ShouldProcess('HKLM Run value NewsViewer', "Remove ($runData)")) {
                $run.Dispose()
                $run = $hklm.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', $true)
                $run.DeleteValue('NewsViewer')
                Write-Host "  removed   HKLM Run value NewsViewer ($runData)"
            }
        }
        else {
            Write-Host "  kept      HKLM Run value NewsViewer does not point at NewsViewer.exe ($runData)"
        }
    }
    finally {
        if ($run) { $run.Dispose() }
        $hklm.Dispose()
    }
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

# Decodes the embedded definition to a temp file, substitutes the install path (and, for Content Arrived,
# the event subscription), writes a UTF-16 LE (BOM) copy - the encoding schtasks accepts reliably -
# registers it and deletes both temp files. Records what Verification must find afterwards.
function Register-Task {
    param([string]$XmlBase64, [string]$TaskName, [switch]$RewriteSubscription)

    $src = Join-Path ([System.IO.Path]::GetTempPath()) ('NewsViewerTaskSrc-' + [guid]::NewGuid().ToString('N') + '.xml')
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('NewsViewerTask-' + [guid]::NewGuid().ToString('N') + '.xml')
    try {
        [System.IO.File]::WriteAllBytes($src, [System.Convert]::FromBase64String($XmlBase64))

        $doc = New-Object System.Xml.XmlDocument
        $doc.PreserveWhitespace = $true
        $doc.Load($src)

        $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
        $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')

        $uri     = $doc.SelectSingleNode('//t:RegistrationInfo/t:URI', $ns)
        $command = $doc.SelectSingleNode('//t:Exec/t:Command', $ns)
        $workDir = $doc.SelectSingleNode('//t:Exec/t:WorkingDirectory', $ns)
        if (-not $uri -or -not $command -or -not $workDir) { throw "The embedded definition for '$TaskName' is incomplete." }
        $uri.InnerText     = $TaskName
        $command.InnerText = '"' + $ExePath + '"'
        $workDir.InnerText = $WorkingDir

        $subscription = $null
        if ($RewriteSubscription) {
            $subscription = $doc.SelectSingleNode('//t:EventTrigger/t:Subscription', $ns)
            if (-not $subscription) { throw "The embedded definition for '$TaskName' has no event subscription." }
            $subscription.InnerText =
                "<QueryList><Query Id=""0"" Path=""$EventLogName""><Select Path=""$EventLogName"">" +
                "*[System[Provider[@Name='$EventSourceName'] and EventID=$ContentEventId]]" +
                "</Select></Query></QueryList>"
        }

        # Stamp: SHA-256 of the final definition (without the stamp), appended to the Description as
        # " [nc-hash:<hex>]". NewsService (ViewerTasks:Mode = VerifyOnly) compares it to detect drift in
        # settings a field comparison cannot see (principal, time limit, ...). It builds the same text:
        # an unindented XmlWriter over a StringWriter, hashed as UTF-8. Command and WorkingDirectory enter
        # the hash in their NORMALISED form (Get-NormalizedPath), so a %ProgramFiles% token and the literal
        # path it expands to hash equally; the registered XML keeps the text as written.
        $writtenCommand = $command.InnerText
        $writtenWorkDir = $workDir.InnerText
        $command.InnerText = Get-NormalizedPath $writtenCommand
        $workDir.InnerText = Get-NormalizedPath $writtenWorkDir
        $sw = New-Object System.IO.StringWriter
        $stampSettings = New-Object System.Xml.XmlWriterSettings
        $stampSettings.Indent = $false
        $stampWriter = [System.Xml.XmlWriter]::Create($sw, $stampSettings)
        try { $doc.Save($stampWriter) } finally { $stampWriter.Dispose() }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sw.ToString())) } finally { $sha.Dispose() }
        $hash = (($hashBytes | ForEach-Object { $_.ToString('x2') }) -join '')
        $command.InnerText = $writtenCommand
        $workDir.InnerText = $writtenWorkDir
        $description = $doc.SelectSingleNode('//t:RegistrationInfo/t:Description', $ns)
        if (-not $description) { throw "The embedded definition for '$TaskName' has no description." }
        $description.InnerText = $description.InnerText + ' [nc-hash:' + $hash + ']'
        Write-Verbose "nc-hash $TaskName $hash"

        $script:Expected[$TaskName] = @{
            Command      = $command.InnerText
            WorkingDir   = $workDir.InnerText
            Subscription = $(if ($subscription) { $subscription.InnerText } else { $null })
            Triggers     = @(Get-TriggerSignature $doc $ns)
            Hash         = $hash
            Xml          = $doc.OuterXml
        }

        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Encoding = New-Object System.Text.UnicodeEncoding($false, $true)   # UTF-16 LE, BOM
        $settings.Indent = $false
        $writer = [System.Xml.XmlWriter]::Create($tmp, $settings)
        try { $doc.Save($writer) } finally { $writer.Dispose() }

        if ($PSCmdlet.ShouldProcess($TaskName, 'Register scheduled task')) {
            if ((Invoke-Schtasks @('/Create', '/TN', $TaskName, '/XML', $tmp, '/F')) -ne 0) {
                throw "schtasks /Create failed for '$TaskName': $($script:SchtasksOutput.Trim())"
            }
            Write-Host "  registered $TaskName"
        }
    }
    finally {
        Remove-Item -LiteralPath $src -Force -WhatIf:$false -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tmp -Force -WhatIf:$false -ErrorAction SilentlyContinue
    }
}

# Compares the registered definition with what Register-Task just wrote. Returns the problems found
# (empty = it matches). Environment variables are expanded on both sides before comparing paths.
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
    $expand = { param($s) Get-NormalizedPath $s }
    $text = { param($xpath) $n = $doc.SelectSingleNode($xpath, $ns); if ($n) { $n.InnerText.Trim() } else { '' } }

    $problems = @()
    $command = & $text '//t:Exec/t:Command'
    if ((& $expand $command) -ne (& $expand $expected.Command)) {
        $problems += "wrong command - expected '$($expected.Command)', found '$command'"
    }
    $workDir = & $text '//t:Exec/t:WorkingDirectory'
    if ((& $expand $workDir) -ne (& $expand $expected.WorkingDir)) {
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
    $stamp = [regex]::Match((& $text '//t:RegistrationInfo/t:Description'), '\[nc-hash:([0-9a-fA-F]{64})\]')
    if (-not $stamp.Success -or $stamp.Groups[1].Value -ne $expected.Hash) {
        $problems += 'the nc-hash stamp is missing or differs from the one written'
    }
    return $problems
}

# Prints the readout (same shape and wording as scripts/Register-NewsViewerTasks.ps1) and returns the
# number of problems: a missing task, a task that does not match what was just registered, a missing
# event source.
function Test-LaunchSurface {
    $failures = 0
    # TASK_SESSION_STATE_CHANGE_TYPE values, as Get-ScheduledTask reports them.
    $stateNames = @{ 1 = 'ConsoleConnect'; 2 = 'ConsoleDisconnect'; 3 = 'RemoteConnect'; 4 = 'RemoteDisconnect'; 7 = 'SessionLock'; 8 = 'SessionUnlock' }
    foreach ($name in @($LaunchTask, $ContentTask)) {
        $path = (Split-Path $name -Parent) + '\'
        $leaf = Split-Path $name -Leaf
        $task = Get-ScheduledTask -TaskPath $path -TaskName $leaf -ErrorAction SilentlyContinue
        Write-Host "  $name" -ForegroundColor Yellow
        if (-not $task) { Write-Host '    MISSING' -ForegroundColor Red; $failures++; continue }
        foreach ($trigger in $task.Triggers) {
            $kind = $trigger.CimClass.CimClassName -replace '^MSFT_Task', '' -replace 'Trigger$', ''
            $detail = if ($trigger.PSObject.Properties['StateChange']) { "StateChange=$($stateNames[[int]$trigger.StateChange])" }
                      elseif ($kind -eq 'Logon') { "Delay=$(if ($trigger.Delay) { $trigger.Delay } else { '(none)' })  UserId=$(if ($trigger.UserId) { $trigger.UserId } else { '(any user)' })" }
                      elseif ($trigger.PSObject.Properties['Subscription']) { "Subscription=$($trigger.Subscription)" }
                      else { '' }
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
    $sourceExists = (Test-EventSourceExists $EventSourceName)
    Write-Host "  event source '$EventSourceName': $(if ($sourceExists) { 'present' } else { 'MISSING' })" `
        -ForegroundColor $(if ($sourceExists) { 'Green' } else { 'Red' })
    if (-not $sourceExists) { $failures++ }
    return $failures
}

# ---- Main --------------------------------------------------------------------
try {
# ---- Uninstall ---------------------------------------------------------------
if ($Uninstall) {
    Write-Step 'Removing NewsViewer scheduled tasks'
    foreach ($name in @($LaunchTask, $ContentTask)) {
        Write-RemoveResult (Remove-TaskIfPresent $name) $name
    }
    # A machine upgraded from an older package may still carry the Run value.
    Remove-LegacyRunValue
    exit 0
}

# ---- Tasks -------------------------------------------------------------------
if (-not (Test-Path -LiteralPath ([System.Environment]::ExpandEnvironmentVariables($ExePath)) -PathType Leaf)) {
    Write-Warning "$ExePath does not exist yet. The tasks are registered anyway; install NewsViewer there before they fire."
}
Write-Step "Registering NewsViewer scheduled tasks for '$Company' (exe: $ExePath)"
Register-Task -XmlBase64 $LaunchXmlBase64  -TaskName $LaunchTask
Register-Task -XmlBase64 $ContentXmlBase64 -TaskName $ContentTask -RewriteSubscription

# ---- Event source ------------------------------------------------------------
Write-Host ''
Write-Step 'NewsService event source'
if ((Test-EventSourceExists $EventSourceName)) {
    $existingLog = (Get-EventSourceLogName $EventSourceName)
    if ($existingLog -ne $EventLogName) {
        Write-Warning "Event source '$EventSourceName' already exists under log '$existingLog', not '$EventLogName'. The Content Arrived task subscribes to '$EventLogName'."
    }
    Write-Host "  exists    $EventSourceName (log: $existingLog)"
}
elseif ($PSCmdlet.ShouldProcess($EventSourceName, "Create event source in log '$EventLogName'")) {
    New-EventSourceNow $EventSourceName $EventLogName
    Write-Host "  created   $EventSourceName (log: $EventLogName)"
}

# ---- Legacy tasks ------------------------------------------------------------
Write-Host ''
Write-Step 'Legacy Workstation Unlock task and old dev-box tasks'
for ($i = 0; $i -lt $LegacyTasks.Count; $i++) {
    Write-RemoveResult (Remove-TaskIfPresent $LegacyTasks[$i]) $LegacyTasks[$i] $(if ($i -eq 0) { 'legacy ' } else { 'old dev ' })
}

# ---- Legacy Run value (the tasks are in place: a failed registration threw above) ----
Remove-LegacyRunValue

# ---- Verification ------------------------------------------------------------
Write-Host ''
Write-Step 'Verification'
if ($WhatIfPreference) {
    Write-Host '  skipped (-WhatIf: nothing was registered, so there is nothing to verify)'
}
else {
    $failures = Test-LaunchSurface
    Write-Host ''
    if ($failures -gt 0) {
        Write-Host "VERIFICATION FAILED: $failures problem(s) above. The launch surface is NOT established." -ForegroundColor Red
        exit 1
    }
    Write-Host 'Verification passed: both tasks are registered as intended.' -ForegroundColor Green
}
Write-Host ''
# Explicit success code: never fall off the end, where the exit code would be whatever a native probe
# (the schtasks query for a legacy task that rightly does not exist) left in $LASTEXITCODE.
exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
""";
}
