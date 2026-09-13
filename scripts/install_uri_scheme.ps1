<#
.SYNOPSIS
    Registers (or removes) the verifysphere:// custom URI scheme in the Windows Registry.

.DESCRIPTION
    Windows equivalent of the Android intent-filter:
        <data android:scheme="verifysphere" />

    On Android, the OS reads AndroidManifest.xml and routes verifysphere:// links
    to the app automatically. On Windows, this must be written to the Registry once.

    Registry path written:
        HKEY_CLASSES_ROOT\verifysphere
            (Default)         = "URL:VerifySphere Protocol"
            URL Protocol      = ""
        HKEY_CLASSES_ROOT\verifysphere\shell\open\command
            (Default)         = "<ExePath>" "%1"

    Run ONCE after installing VerifySphere.exe, with Administrator privileges.

.PARAMETER ExePath
    Full path to VerifySphere.exe.
    Defaults to the EXE in the same folder as this script.

.PARAMETER Uninstall
    Remove the verifysphere:// URI scheme registration from the Registry.

.EXAMPLE
    # Install (auto-detect EXE in same folder)
    .\install_uri_scheme.ps1

    # Install with explicit path
    .\install_uri_scheme.ps1 -ExePath "C:\Program Files\VerifySphere\VerifySphere.exe"

    # Remove URI scheme
    .\install_uri_scheme.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string] $ExePath   = "",
    [switch] $Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -----------------------------------------------------------------------
# Resolve EXE path
# -----------------------------------------------------------------------

if (-not $Uninstall)
{
    if ([string]::IsNullOrWhiteSpace($ExePath))
    {
        # Default: look for VerifySphere.exe in the same folder as this script
        $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
        $Candidates = Get-ChildItem -Path $ScriptDir -Filter "VerifySphere*.exe" -ErrorAction SilentlyContinue
        if ($Candidates.Count -eq 0)
        {
            Write-Error "VerifySphere.exe not found in $ScriptDir`nUse -ExePath to specify the location."
            exit 1
        }
        $ExePath = $Candidates[0].FullName
    }

    if (-not (Test-Path $ExePath))
    {
        Write-Error "File not found: $ExePath"
        exit 1
    }

    $ExePath = (Resolve-Path $ExePath).Path
}

# -----------------------------------------------------------------------
# Check for Administrator privileges
# -----------------------------------------------------------------------

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator
)

if (-not $IsAdmin)
{
    Write-Error "Administrator privileges required.`nRight-click PowerShell and choose 'Run as Administrator', then re-run this script."
    exit 1
}

# -----------------------------------------------------------------------
# Uninstall path
# -----------------------------------------------------------------------

if ($Uninstall)
{
    $RegRoot = "HKCR:\verifysphere"
    if (Test-Path $RegRoot)
    {
        Remove-Item -Path $RegRoot -Recurse -Force
        Write-Host "✓ verifysphere:// URI scheme removed from Registry." -ForegroundColor Green
    }
    else
    {
        Write-Host "verifysphere:// URI scheme not found in Registry (nothing to remove)." -ForegroundColor Yellow
    }
    exit 0
}

# -----------------------------------------------------------------------
# Install path
# -----------------------------------------------------------------------

# Map HKCR: drive if it isn't already mapped (PowerShell doesn't expose it by default)
if (-not (Get-PSDrive -Name HKCR -ErrorAction SilentlyContinue))
{
    New-PSDrive -Name HKCR -PSProvider Registry -Root HKEY_CLASSES_ROOT | Out-Null
}

$SchemeRoot    = "HKCR:\verifysphere"
$ShellOpenCmd  = "HKCR:\verifysphere\shell\open\command"

# Create the key tree
New-Item         -Path $SchemeRoot   -Force | Out-Null
Set-ItemProperty -Path $SchemeRoot   -Name "(Default)"    -Value "URL:VerifySphere Protocol"
New-ItemProperty -Path $SchemeRoot   -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null

New-Item -Path "HKCR:\verifysphere\shell"       -Force | Out-Null
New-Item -Path "HKCR:\verifysphere\shell\open"  -Force | Out-Null
New-Item -Path $ShellOpenCmd                    -Force | Out-Null

# The command Windows runs when a verifysphere:// link is clicked.
# "%1" is the full URI passed as the first argument — mirrors Android's intent.data
$Command = "`"$ExePath`" `"%1`""
Set-ItemProperty -Path $ShellOpenCmd -Name "(Default)" -Value $Command

Write-Host ""
Write-Host "=== VerifySphere URI Scheme Registered ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Scheme:  verifysphere://"
Write-Host "  EXE:     $ExePath"
Write-Host "  Command: $Command"
Write-Host ""
Write-Host "✓ Done. Clicking a verifysphere:// link will now launch VerifySphere." -ForegroundColor Green
Write-Host ""
Write-Host "To uninstall: .\install_uri_scheme.ps1 -Uninstall" -ForegroundColor Gray
Write-Host ""
