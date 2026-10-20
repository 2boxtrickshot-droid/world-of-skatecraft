# World of Skatecraft one-click Windows installer. Double-click install.bat.
# Installs any missing prerequisites with winget, then runs setup\setup.ps1.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Step($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host 'winget was not found. Install "App Installer" from the Microsoft Store (or update Windows), then run install.bat again.' -ForegroundColor Red
    exit 1
}

# name, command that proves it is installed, winget id, extra winget args
$tools = @(
    @{ Name = 'Git';            Cmd = 'git';    Id = 'Git.Git';                 Extra = @() },
    @{ Name = 'Rust';           Cmd = 'cargo';  Id = 'Rustlang.Rustup';         Extra = @() },
    @{ Name = 'Python 3';       Cmd = 'python'; Id = 'Python.Python.3.12';       Extra = @() },
    @{ Name = 'Docker Desktop'; Cmd = 'docker'; Id = 'Docker.DockerDesktop';    Extra = @() }
)
$needRestart = $false
foreach ($t in $tools) {
    if (Get-Command $t.Cmd -ErrorAction SilentlyContinue) { Write-Host "  ok       $($t.Name)"; continue }
    Step "Installing $($t.Name)"
    winget install -e --id $t.Id --accept-package-agreements --accept-source-agreements @($t.Extra)
    Refresh-Path
    if ($t.Name -eq 'Docker Desktop') { $needRestart = $true }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msvc = (Test-Path $vswhere) -and
    [bool](& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($msvc) { Write-Host '  ok       Visual Studio C++ build tools' }
else {
    Step 'Installing Visual Studio C++ build tools (large download)'
    winget install -e --id Microsoft.VisualStudio.2022.BuildTools --accept-package-agreements --accept-source-agreements `
        --override '--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
}

if ($needRestart) {
    Write-Host ''
    Write-Host 'Docker Desktop was just installed. Restart Windows, start Docker Desktop once and wait until it says it is running, then double-click install.bat again.' -ForegroundColor Yellow
    exit 0
}

Step 'Running World of Skatecraft setup'
& (Join-Path $PSScriptRoot 'setup.ps1') @args
exit $LASTEXITCODE
