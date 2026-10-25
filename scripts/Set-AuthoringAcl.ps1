#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Applies the NTFS permissions that protect NewsCentral's team signing keys, users.json and audit log.

.DESCRIPTION
    NewsCentral never sets ACLs itself: the app runs as each author's own Windows account, so ACLs can only keep
    data away from people who do not need it. This script, run once by the environment administrator, does that:

      SecurePath (default <DataPath>\secure)
        inheritance disabled; SYSTEM, Administrators and AdminsGroup: Full control. Nothing else.
        keys\<team>   TeamSigners[team]: Modify (on top of the administrators it inherits). Nothing else.
        Folders under keys\ for teams NOT in -TeamSigners lose any explicit ACE (administrators only).

      <DataPath>\config\users.json
        inheritance disabled on the file; SYSTEM, Administrators and AdminsGroup: Full control;
        AuthorsGroup: Read.

      AuditPath (default <DataPath>\audit)
        inheritance disabled; SYSTEM, Administrators and AdminsGroup: Full control.
        AuthorsGroup, folders (this folder and subfolders): ListDirectory, CreateFiles, CreateDirectories,
                      ReadAttributes, Synchronize.
        AuthorsGroup, files (inherit only): AppendData, ReadAttributes, Synchronize.
        OWNER RIGHTS, files (inherit only): ReadData, AppendData, ReadAttributes, ReadPermissions, Synchronize -
                      so the author who created an audit file cannot rewrite, delete or re-ACL it.
        OWNER RIGHTS, folders (this folder and subfolders): ListDirectory, ReadAttributes, ReadPermissions,
                      Synchronize - the same reason, for the month folders an author creates (see NOTES).
        No CREATOR OWNER ACE.

    The script REFUSES (exit 1, listing the files) while any <DataPath>\<team>\team-signing.json still exists:
    an administrator must log in to NewsCentral first, which migrates every team key into the secure store.
    After provisioning, rotate every team key (the old files were readable by every author).

    Run it from 64-bit PowerShell, elevated. -WhatIf shows what would change and still performs the read-only
    checks (the team-signing.json guard, group name resolution).

    Exit code: 0 = applied (or previewed), 1 = refused or failed. Nothing is changed before the checks pass.

.PARAMETER DataPath
    The NewsCentral authoring data root (the environment's DataPath).

.PARAMETER SecurePath
    Root of the secure key store (environment.json securePath). Default: <DataPath>\secure.

.PARAMETER AuditPath
    Root of the audit log (environment.json auditPath). Default: <DataPath>\audit.

.PARAMETER AdminsGroup
    Group (DOMAIN\Name, MACHINE\Name or a plain local group name) of the environment's administrators.

.PARAMETER AuthorsGroup
    Group of everyone who authors content (and so signs in to NewsCentral without being an administrator).

.PARAMETER TeamSigners
    Hashtable of team folder name -> group allowed to sign for that team, e.g. @{ 'cz-its' = 'NC-cz-its-Signers' }.

.NOTES
    config\ folder: this script protects users.json itself. An author who can delete files in the config folder
    (Modify or DeleteSubdirectoriesAndFiles on it) can still delete users.json and create a new one. The script
    warns when AuthorsGroup holds such a right on the config folder; removing it is left to you because the folder
    may hold other files authors legitimately write.

    The OWNER RIGHTS entry for folders goes beyond the minimum: the owner of a folder holds WRITE_DAC implicitly,
    so an author who creates a month folder could otherwise re-ACL it and delete the audit files inside.

.EXAMPLE
    .\Set-AuthoringAcl.ps1 -DataPath D:\NewsCentral -AdminsGroup CONTOSO\NC-Admins -AuthorsGroup CONTOSO\NC-Authors `
        -TeamSigners @{ 'cz-its' = 'CONTOSO\NC-cz-its-Signers' } -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$DataPath,
    [string]$SecurePath,
    [string]$AuditPath,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$AdminsGroup,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$AuthorsGroup,
    [hashtable]$TeamSigners = @{}
)

$ErrorActionPreference = "Stop"

# ---- 64-bit host required -----------------------------------------------------
# Same rule as the other provisioning scripts: a 32-bit host sees redirected folders and registry views, so it is
# refused up front, before anything is read or written.
if (-not [Environment]::Is64BitProcess) {
    Write-Host 'ERROR: this script must be run from 64-bit PowerShell; this is a 32-bit PowerShell host.' -ForegroundColor Red
    Write-Host '  Nothing was read or changed.' -ForegroundColor Red
    Write-Host '  Remedy: run it with %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe or, when the caller is itself' -ForegroundColor Red
    Write-Host '  32-bit, with %SystemRoot%\sysnative\WindowsPowerShell\v1.0\powershell.exe.' -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName System.Security | Out-Null

$FS   = [System.Security.AccessControl.FileSystemRights]
$Allow = [System.Security.AccessControl.AccessControlType]::Allow
$Inh   = [System.Security.AccessControl.InheritanceFlags]
$Prop  = [System.Security.AccessControl.PropagationFlags]

function Resolve-Sid {
    param([string]$Name, [string]$What)
    try {
        return (New-Object System.Security.Principal.NTAccount($Name)).Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "$What '$Name' could not be resolved to an account or group: $($_.Exception.Message)"
    }
}

function New-Rule {
    param($Sid, $Rights, $Inherit, $Propagation)
    return New-Object System.Security.AccessControl.FileSystemAccessRule($Sid, $Rights, $Inherit, $Propagation, $Allow)
}

# Removes every explicit rule, optionally disabling inheritance (and discarding the inherited rules).
function Clear-Acl {
    param($Acl, [bool]$Protect)
    if ($Protect) { $Acl.SetAccessRuleProtection($true, $false) }
    foreach ($rule in @($Acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
        [void]$Acl.RemoveAccessRuleSpecific($rule)
    }
}

$exitCode = 0
try {
    if (-not [System.IO.Path]::IsPathRooted($DataPath)) { throw "-DataPath must be an absolute path: '$DataPath'" }
    $DataPath = $DataPath.TrimEnd('\')
    if (-not $SecurePath) { $SecurePath = Join-Path $DataPath 'secure' }
    if (-not $AuditPath)  { $AuditPath  = Join-Path $DataPath 'audit' }
    foreach ($p in @($SecurePath, $AuditPath)) {
        if (-not [System.IO.Path]::IsPathRooted($p)) { throw "Paths must be absolute: '$p'" }
    }
    if (-not (Test-Path -LiteralPath $DataPath -PathType Container)) { throw "DataPath '$DataPath' does not exist." }

    # ---- Guard: keys must be migrated first -----------------------------------
    $legacy = @(Get-ChildItem -LiteralPath $DataPath -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'team-signing.json' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($legacy.Count -gt 0) {
        Write-Host 'REFUSED: team signing keys still exist under the data path. Nothing was changed.' -ForegroundColor Red
        Write-Host '  Sign in to NewsCentral as a system administrator first (this migrates every team key into the secure store),' -ForegroundColor Red
        Write-Host '  then run this script again. Files:' -ForegroundColor Red
        $legacy | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        exit 1
    }

    $usersFile = Join-Path (Join-Path $DataPath 'config') 'users.json'
    if (-not (Test-Path -LiteralPath $usersFile -PathType Leaf)) {
        throw "'$usersFile' does not exist. Initialize the environment in NewsCentral first."
    }

    # ---- Resolve every account before touching anything -------------------------
    $system  = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $builtinAdmins = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $ownerRights = New-Object System.Security.Principal.SecurityIdentifier('S-1-3-4')
    $admins  = Resolve-Sid -Name $AdminsGroup  -What '-AdminsGroup'
    $authors = Resolve-Sid -Name $AuthorsGroup -What '-AuthorsGroup'
    $signers = @{}
    foreach ($team in $TeamSigners.Keys) {
        if ($team -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "-TeamSigners key '$team' is not a valid team folder name." }
        $signers[[string]$team] = Resolve-Sid -Name ([string]$TeamSigners[$team]) -What "-TeamSigners['$team']"
    }

    $fullControlSids = @($system, $builtinAdmins, $admins)
    $containerFlags = $Inh::ContainerInherit -bor $Inh::ObjectInherit

    # ---- SecurePath -------------------------------------------------------------
    if ($PSCmdlet.ShouldProcess($SecurePath, 'Create (if missing) and restrict to SYSTEM, Administrators and AdminsGroup')) {
        if (-not (Test-Path -LiteralPath $SecurePath)) { New-Item -ItemType Directory -Path $SecurePath -Force | Out-Null }
        $acl = Get-Acl -LiteralPath $SecurePath
        Clear-Acl -Acl $acl -Protect $true
        foreach ($sid in $fullControlSids) {
            $acl.AddAccessRule((New-Rule $sid $FS::FullControl $containerFlags $Prop::None))
        }
        Set-Acl -LiteralPath $SecurePath -AclObject $acl
    }

    $keysRoot = Join-Path $SecurePath 'keys'
    if ($PSCmdlet.ShouldProcess($keysRoot, 'Create (if missing)')) {
        if (-not (Test-Path -LiteralPath $keysRoot)) { New-Item -ItemType Directory -Path $keysRoot -Force | Out-Null }
    }

    foreach ($team in $signers.Keys) {
        $teamDir = Join-Path $keysRoot $team
        if ($PSCmdlet.ShouldProcess($teamDir, "Grant Modify to $($TeamSigners[$team])")) {
            if (-not (Test-Path -LiteralPath $teamDir)) { New-Item -ItemType Directory -Path $teamDir -Force | Out-Null }
            $acl = Get-Acl -LiteralPath $teamDir
            Clear-Acl -Acl $acl -Protect $false
            $acl.SetAccessRuleProtection($false, $false)
            $acl.AddAccessRule((New-Rule $signers[$team] $FS::Modify $containerFlags $Prop::None))
            Set-Acl -LiteralPath $teamDir -AclObject $acl
        }
    }

    if (Test-Path -LiteralPath $keysRoot) {
        foreach ($dir in @(Get-ChildItem -LiteralPath $keysRoot -Directory -ErrorAction SilentlyContinue)) {
            if (-not $signers.ContainsKey($dir.Name)) {
                if ($PSCmdlet.ShouldProcess($dir.FullName, 'Remove explicit ACEs (team is not in -TeamSigners)')) {
                    $acl = Get-Acl -LiteralPath $dir.FullName
                    Clear-Acl -Acl $acl -Protect $false
                    $acl.SetAccessRuleProtection($false, $false)
                    Set-Acl -LiteralPath $dir.FullName -AclObject $acl
                }
            }
        }
    }

    # ---- users.json -------------------------------------------------------------
    if ($PSCmdlet.ShouldProcess($usersFile, 'Restrict writes to administrators; AuthorsGroup read-only')) {
        $acl = Get-Acl -LiteralPath $usersFile
        Clear-Acl -Acl $acl -Protect $true
        foreach ($sid in $fullControlSids) {
            $acl.AddAccessRule((New-Rule $sid $FS::FullControl $Inh::None $Prop::None))
        }
        $acl.AddAccessRule((New-Rule $authors $FS::Read $Inh::None $Prop::None))
        Set-Acl -LiteralPath $usersFile -AclObject $acl
    }

    $configDir = Split-Path -Parent $usersFile
    $configAcl = Get-Acl -LiteralPath $configDir
    $deleteChild = [int]$FS::DeleteSubdirectoriesAndFiles
    $risky = @($configAcl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference -eq $authors -and (([int]$_.FileSystemRights -band $deleteChild) -ne 0) })
    if ($risky.Count -gt 0) {
        Write-Host "WARNING: $AuthorsGroup can delete files in '$configDir', so an author can still delete users.json and create a new one." -ForegroundColor Yellow
        Write-Host '  users.json itself is protected, but remove DeleteSubdirectoriesAndFiles / Modify for that group on the config folder to close this.' -ForegroundColor Yellow
    }

    # ---- AuditPath --------------------------------------------------------------
    if ($PSCmdlet.ShouldProcess($AuditPath, 'Create (if missing) and apply the append-only layout')) {
        if (-not (Test-Path -LiteralPath $AuditPath)) { New-Item -ItemType Directory -Path $AuditPath -Force | Out-Null }
        $acl = Get-Acl -LiteralPath $AuditPath
        Clear-Acl -Acl $acl -Protect $true
        foreach ($sid in $fullControlSids) {
            $acl.AddAccessRule((New-Rule $sid $FS::FullControl $containerFlags $Prop::None))
        }

        # Authors, folders: this folder and subfolders (no file rights here).
        $folderRights = $FS::ListDirectory -bor $FS::CreateFiles -bor $FS::CreateDirectories -bor $FS::ReadAttributes -bor $FS::Synchronize
        $acl.AddAccessRule((New-Rule $authors $folderRights $Inh::ContainerInherit $Prop::None))

        # Authors, files: inherit only - append, never write/delete.
        $fileRights = $FS::AppendData -bor $FS::ReadAttributes -bor $FS::Synchronize
        $acl.AddAccessRule((New-Rule $authors $fileRights $Inh::ObjectInherit $Prop::InheritOnly))

        # OWNER RIGHTS, files: the creator keeps read/append but loses the implicit WRITE_DAC / DELETE.
        $ownerFileRights = $FS::ReadData -bor $FS::AppendData -bor $FS::ReadAttributes -bor $FS::ReadPermissions -bor $FS::Synchronize
        $acl.AddAccessRule((New-Rule $ownerRights $ownerFileRights $Inh::ObjectInherit $Prop::InheritOnly))

        # OWNER RIGHTS, folders: the same for the month folders authors create (beyond the minimum, see NOTES).
        $ownerFolderRights = $FS::ListDirectory -bor $FS::ReadAttributes -bor $FS::ReadPermissions -bor $FS::Synchronize
        $acl.AddAccessRule((New-Rule $ownerRights $ownerFolderRights $Inh::ContainerInherit $Prop::None))

        Set-Acl -LiteralPath $AuditPath -AclObject $acl
    }

    # ---- Result -----------------------------------------------------------------
    if (-not $WhatIfPreference) {
        Write-Host ''
        Write-Host 'Resulting ACLs:' -ForegroundColor Cyan
        foreach ($target in @($SecurePath, (Join-Path $SecurePath 'keys'), $usersFile, $AuditPath)) {
            if (Test-Path -LiteralPath $target) {
                Write-Host ''
                Write-Host "icacls `"$target`"" -ForegroundColor Cyan
                & icacls.exe $target | Out-Host
            }
        }
        foreach ($team in $signers.Keys) {
            $teamDir = Join-Path (Join-Path $SecurePath 'keys') $team
            Write-Host ''
            Write-Host "icacls `"$teamDir`"" -ForegroundColor Cyan
            & icacls.exe $teamDir | Out-Host
        }
        Write-Host ''
        Write-Host 'Done. Now rotate every team key in NewsCentral (Key Management): the old key files were readable by every author.' -ForegroundColor Green
    }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}

# Explicit exit code on every terminal path: never fall off the end, where the exit code would be whatever a native
# command (icacls) last left in $LASTEXITCODE.
exit $exitCode
