# Builds the vmangos server image with World of Skatecraft's patch (setup/vmangos/skatecraft.patch:
# the Skateboarding profession) and points the vmangos-deploy checkout's compose.yaml at it.
#
#   powershell -ExecutionPolicy Bypass -File setup\build-server.ps1             # server\ beside this
#   powershell -ExecutionPolicy Bypass -File setup\build-server.ps1 -Deploy DIR
#
# setup\setup.ps1 runs this for server\. It is safe to run again: an image that already exists is
# reused, and compose.yaml is re-pointed either way. The first build compiles vmangos (a few
# minutes); SKATECRAFT_BUILD_JOBS sets its parallelism (default 4, so the PC stays usable).
param(
    [string]$Deploy = "",
    [int]$Jobs = 0
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if (-not $Deploy) { $Deploy = Join-Path $Root 'server' }
$Deploy = (Resolve-Path $Deploy).Path
$Patch = Join-Path $Root 'setup\vmangos\skatecraft.patch'
# The vmangos commit the patch is made against.
$VmangosRev = '0e3ff01e76d4758e8a7c3108b2717cc785ed56fa'
if ($Jobs -le 0) { $Jobs = if ($env:SKATECRAFT_BUILD_JOBS) { [int]$env:SKATECRAFT_BUILD_JOBS } else { 4 } }

function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Stop-Build($message, $fix = '') {
    Write-Host ''
    Write-Host "BUILD STOPPED: $message" -ForegroundColor Red
    if ($fix) { Write-Host "Fix: $fix" }
    exit 1
}
function Invoke-Native($what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Stop-Build "$what failed (see the lines above)" }
}
function Test-Native([scriptblock]$command) {
    try { & $command *> $null; return $LASTEXITCODE -eq 0 } catch { return $false }
}
# Byte-exact (Latin-1) text round trip, so no encoding or BOM is ever added or lost.
$Latin1 = [Text.Encoding]::GetEncoding(28591)
function ConvertTo-Lf($path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes -contains 0) { return }
    $text = $Latin1.GetString($bytes)
    $fixed = $text.Replace("`r`n", "`n")
    if ($fixed -ne $text) { [IO.File]::WriteAllBytes($path, $Latin1.GetBytes($fixed)) }
}

$ComposeFile = Join-Path $Deploy 'compose.yaml'
$Dockerfile = Join-Path $Deploy 'docker\server\Dockerfile'
if (-not (Test-Path $ComposeFile)) { Stop-Build "no compose.yaml in $Deploy" 'run setup.ps1 first: it fetches vmangos-deploy' }
if (-not (Test-Path $Dockerfile)) { Stop-Build "no docker\server\Dockerfile in $Deploy" 'delete server\ and run setup again' }

# Tagged by the patch's and the Dockerfile's contents together, so a changed patch or a
# vmangos-deploy update builds a new image instead of reusing the old one. The digest is over the
# two files' bytes in that order, which is what the Linux script and Setup.exe compute too.
$Bytes = [IO.File]::ReadAllBytes($Patch) + [IO.File]::ReadAllBytes($Dockerfile)
$Sha = [Security.Cryptography.SHA256]::Create().ComputeHash($Bytes)
$Image = "world-of-skatecraft/vmangos-server:5875-$((($Sha | ForEach-Object { $_.ToString('x2') }) -join '').Substring(0, 12))"

# The deploy project's server Dockerfile uses BuildKit-only instructions (COPY --chmod, --exclude).
$env:DOCKER_BUILDKIT = '1'

if (-not (Test-Native { docker image inspect $Image })) {
    Step "Building $Image (vmangos $VmangosRev with the Skatecraft patch, $Jobs jobs)"
    $Context = Join-Path ([IO.Path]::GetTempPath()) "skatecraft-build-$([guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Force -Path $Context | Out-Null
        Copy-Item -Recurse (Join-Path $Deploy 'docker') (Join-Path $Context 'docker')
        New-Item -ItemType Directory -Force -Path (Join-Path $Context 'docker\patches') | Out-Null
        Copy-Item $Patch (Join-Path $Context 'docker\patches\zz-skatecraft.patch')
        # Shell scripts, the Dockerfile and the patch run in a Linux container; CRLF breaks them.
        Get-ChildItem -Recurse -File (Join-Path $Context 'docker') | ForEach-Object { ConvertTo-Lf $_.FullName }
        $BuildDockerfile = Join-Path $Context 'Dockerfile.skatecraft'
        $text = [IO.File]::ReadAllText((Join-Path $Context 'docker\server\Dockerfile'), [Text.Encoding]::UTF8)
        $text = $text.Replace('make -j"$(nproc)"', "make -j$Jobs")
        [IO.File]::WriteAllText($BuildDockerfile, $text, (New-Object Text.UTF8Encoding($false)))
        Invoke-Native 'the server build' {
            docker build -t $Image -f $BuildDockerfile `
                --build-arg "VMANGOS_REVISION=$VmangosRev" `
                --build-arg VMANGOS_CLIENT_VERSION=5875 `
                --build-arg VMANGOS_FAIL_ON_PATCH_ERROR=1 `
                $Context
        }
    }
    finally {
        Remove-Item -Recurse -Force $Context -ErrorAction SilentlyContinue
    }
}

# Targeted replace on the raw bytes: line endings and everything else in the file stay as they are.
$compose = $Latin1.GetString([IO.File]::ReadAllBytes($ComposeFile))
$pattern = 'image: (&mangosd-image )?(ghcr\.io/mserajnik/vmangos-server[:@]\S+|world-of-skatecraft/vmangos-server:\S+)'
if ($compose -notmatch $pattern) { Stop-Build "no vmangos-server image: line found in $ComposeFile" }
$updated = [regex]::Replace($compose, $pattern, "image: `$1$Image")
if ($updated -ne $compose) { [IO.File]::WriteAllBytes($ComposeFile, $Latin1.GetBytes($updated)) }
Write-Host "$ComposeFile now runs $Image"
exit 0
