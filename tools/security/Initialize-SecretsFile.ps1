<#
.SYNOPSIS
    Creates (if missing) and locks down the secrets file that holds the VirusTotal / Hybrid Analysis API keys.

.DESCRIPTION
    Default location: %ProgramData%\LotRMP\secrets\security-api.json  (outside every git repository).
    - Copies tools\security\templates\security-api.example.json there if the file does not exist yet
      (placeholders only: lotr-security treats them as "no key").
    - Removes inherited permissions and grants access only to SYSTEM, the Administrators group, the account running
      this script and, optionally, the account that runs the publish/scan task (-ReaderAccount).
    - Never prints, reads back or logs a key. You paste the keys yourself with an editor run as that account.
    Run it from an elevated PowerShell on the server (ProgramData ACL changes need administrator rights).

.EXAMPLE
    .\Initialize-SecretsFile.ps1
    .\Initialize-SecretsFile.ps1 -ReaderAccount 'NT SERVICE\lotr-services'
    .\Initialize-SecretsFile.ps1 -Path D:\lotrmp\data\secrets\security-api.json
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Path = (Join-Path $env:ProgramData 'LotRMP\secrets\security-api.json'),
    [string]$ReaderAccount
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$full = [System.IO.Path]::GetFullPath($Path)
$dir = Split-Path -Parent $full

# Refuse a location inside a git working tree: keys must never be committable.
$probe = $dir
while ($probe) {
    if (Test-Path -LiteralPath (Join-Path $probe '.git')) {
        throw "Refusing: $full is inside the git working tree $probe. Use a folder outside any repository (default: $env:ProgramData\LotRMP\secrets)."
    }
    $parent = Split-Path -Parent $probe
    if ($parent -eq $probe) { break }
    $probe = $parent
}

if (-not (Test-Path -LiteralPath $dir)) {
    if ($PSCmdlet.ShouldProcess($dir, 'Create folder')) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

if (-not (Test-Path -LiteralPath $full)) {
    $example = Join-Path $PSScriptRoot 'templates\security-api.example.json'
    if ($PSCmdlet.ShouldProcess($full, 'Create from template')) { Copy-Item -LiteralPath $example -Destination $full }
    Write-Host "Created $full from the template (placeholders only). Edit it and paste your keys."
} else {
    Write-Host "Secrets file already exists: $full (content left untouched)."
}

function Set-PrivateAcl([string]$target, [bool]$isDir) {
    $acl = if ($isDir) { New-Object System.Security.AccessControl.DirectorySecurity } else { New-Object System.Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)   # no inheritance, drop inherited rules
    $inherit = if ($isDir) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    $principals = @(
        @{ Id = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18');     Rights = 'FullControl' },  # SYSTEM
        @{ Id = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'); Rights = 'FullControl' },  # Administrators
        @{ Id = [System.Security.Principal.WindowsIdentity]::GetCurrent().User;            Rights = 'Modify' }
    )
    if ($ReaderAccount) { $principals += @{ Id = (New-Object System.Security.Principal.NTAccount $ReaderAccount); Rights = 'Read' } }
    foreach ($p in $principals) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($p.Id, $p.Rights, $inherit, 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($PSCmdlet.ShouldProcess($target, 'Restrict ACL')) {
        if ($isDir) { [System.IO.Directory]::SetAccessControl($target, $acl) } else { [System.IO.File]::SetAccessControl($target, $acl) }
    }
}

Set-PrivateAcl $dir $true
Set-PrivateAcl $full $false

Write-Host "ACL restricted to SYSTEM, Administrators, $([Environment]::UserDomainName)\$([Environment]::UserName)$(if ($ReaderAccount) { ", $ReaderAccount (read)" })."
Write-Host "Check with:  lotr-security check-config   (keys are shown masked)"
if ($full -ne [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData 'LotRMP\secrets\security-api.json'))) {
    Write-Host "Non-default path: set LOTR_SECURITY_SECRETS=$full for the account that runs lotr-security, or pass --secrets."
}
