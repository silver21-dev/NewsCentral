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
#  source when missing and removes the legacy Workstation Unlock task. With
#  -Uninstall it removes both tasks (NewsService never modifies Task Scheduler,
#  so the installer owns their creation and removal). It writes no
#  configuration and contains no secret.
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
    Removes both NewsViewer tasks. The event source and the legacy task are not touched.
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

# Deletes a task if it exists. $true = removed, $false = nothing to remove. Any other failure throws.
function Remove-TaskIfPresent {
    param([string]$Name)
    if (-not (Test-TaskExists $Name)) { return $false }
    if (-not $PSCmdlet.ShouldProcess($Name, 'Remove scheduled task')) { return $false }
    if ((Invoke-Schtasks @('/Delete', '/TN', $Name, '/F')) -ne 0) {
        $output = $script:SchtasksOutput.Trim()
        if (-not (Test-TaskExists $Name)) { return $false }
        throw "schtasks /Delete failed for '$Name': $output"
    }
    return $true
}

# ---- Uninstall ---------------------------------------------------------------
if ($Uninstall) {
    Write-Step 'Removing NewsViewer scheduled tasks'
    foreach ($name in @($LaunchTask, $ContentTask)) {
        if (Remove-TaskIfPresent $name) { Write-Host "  removed   $name" }
        else { Write-Host "  nothing removed (absent, or -WhatIf): $name" }
    }
    return
}

# Decodes the embedded definition to a temp file, substitutes the install path (and, for Content Arrived,
# the event subscription), writes a UTF-16 LE (BOM) copy - the encoding schtasks accepts reliably -
# registers it and deletes both temp files.
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
        # an unindented XmlWriter over a StringWriter, hashed as UTF-8.
        $sw = New-Object System.IO.StringWriter
        $stampSettings = New-Object System.Xml.XmlWriterSettings
        $stampSettings.Indent = $false
        $stampWriter = [System.Xml.XmlWriter]::Create($sw, $stampSettings)
        try { $doc.Save($stampWriter) } finally { $stampWriter.Dispose() }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sw.ToString())) } finally { $sha.Dispose() }
        $hash = (($hashBytes | ForEach-Object { $_.ToString('x2') }) -join '')
        $description = $doc.SelectSingleNode('//t:RegistrationInfo/t:Description', $ns)
        if (-not $description) { throw "The embedded definition for '$TaskName' has no description." }
        $description.InnerText = $description.InnerText + ' [nc-hash:' + $hash + ']'
        Write-Verbose "nc-hash $TaskName $hash"

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

# ---- Tasks -------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Warning "$ExePath does not exist yet. The tasks are registered anyway; install NewsViewer there before they fire."
}
Write-Step "Registering NewsViewer scheduled tasks for '$Company' (exe: $ExePath)"
Register-Task -XmlBase64 $LaunchXmlBase64  -TaskName $LaunchTask
Register-Task -XmlBase64 $ContentXmlBase64 -TaskName $ContentTask -RewriteSubscription

# ---- Event source ------------------------------------------------------------
Write-Host ''
Write-Step 'NewsService event source'
if ([System.Diagnostics.EventLog]::SourceExists($EventSourceName)) {
    $existingLog = [System.Diagnostics.EventLog]::LogNameFromSourceName($EventSourceName, '.')
    if ($existingLog -ne $EventLogName) {
        Write-Warning "Event source '$EventSourceName' already exists under log '$existingLog', not '$EventLogName'. The Content Arrived task subscribes to '$EventLogName'."
    }
    Write-Host "  exists    $EventSourceName (log: $existingLog)"
}
elseif ($PSCmdlet.ShouldProcess($EventSourceName, "Create event source in log '$EventLogName'")) {
    [System.Diagnostics.EventLog]::CreateEventSource($EventSourceName, $EventLogName)
    Write-Host "  created   $EventSourceName (log: $EventLogName)"
}

# ---- Legacy tasks ------------------------------------------------------------
Write-Host ''
Write-Step 'Legacy Workstation Unlock task and old dev-box tasks'
foreach ($old in $LegacyTasks) {
    if (Remove-TaskIfPresent $old) { Write-Host "  removed   $old" }
    else { Write-Host "  nothing removed (absent, or -WhatIf): $old" }
}
Write-Host ''
""";
}
