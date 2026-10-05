# Builds the NetFluss for Windows release files.
#
#   pwsh windows/Packaging/build-release.ps1 -Version 1.0.0 [-Architectures x64,arm64] [-SkipInstaller]
#
# For each architecture: a self-contained publish (no .NET install needed on the target),
# the helper service in a Helper\ folder beside the app (the folder the in-app installer
# copies into Program Files), a portable .zip, and — when Inno Setup's iscc.exe is
# available — the per-user installer. SHA256SUMS.txt covers every file; the in-app updater
# refuses an installer it cannot match against it.
#
# Output: windows/artifacts/release/

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string[]] $Architectures = @('x64', 'arm64'),
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $windows 'artifacts'
$release = Join-Path $artifacts 'release'

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force $release | Out-Null

function Invoke-Checked([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with $LASTEXITCODE" }
}

# Optional Authenticode signing: set NETFLUSS_SIGN_PFX (path to a .pfx) and
# NETFLUSS_SIGN_PASSWORD. Unsigned builds work, but SmartScreen warns on first run.
$signtool = if ($env:NETFLUSS_SIGN_PFX) {
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Sign([string[]] $Files) {
    if (-not $env:NETFLUSS_SIGN_PFX) { return }
    if (-not $signtool) { throw 'NETFLUSS_SIGN_PFX is set but signtool.exe was not found.' }
    Invoke-Checked $signtool (@('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256',
        '/f', $env:NETFLUSS_SIGN_PFX, '/p', $env:NETFLUSS_SIGN_PASSWORD) + $Files)
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $publish = Join-Path $artifacts "publish\$arch"
    Write-Host "== $rid =="

    $common = @('-c', 'Release', '-r', $rid, '--self-contained', 'true', "-p:Version=$Version", '-p:DebugType=none', '-nologo')
    Invoke-Checked dotnet (@('publish', (Join-Path $windows 'src\NetFluss.Service\NetFluss.Service.csproj'), '-o', (Join-Path $publish 'Helper')) + $common)
    Invoke-Checked dotnet (@('publish', (Join-Path $windows 'src\NetFluss.App\NetFluss.App.csproj'), '-o', $publish) + $common)

    if (-not (Test-Path (Join-Path $publish 'Helper\NetFluss.Service.exe'))) { throw "The helper is missing from the $arch publish." }
    if (-not (Test-Path (Join-Path $publish 'SpeedTest'))) { throw "The speed test assets are missing from the $arch publish." }
    Sign @((Join-Path $publish 'NetFluss.exe'), (Join-Path $publish 'Helper\NetFluss.Service.exe'))

    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $release "NetFluss-$Version-$arch-portable.zip") -CompressionLevel Optimal

    if ($SkipInstaller) { continue }
    if (-not $iscc) { throw 'Inno Setup 6 (iscc.exe) was not found. Install it, or pass -SkipInstaller.' }
    Invoke-Checked $iscc @("/DAppVersion=$Version", "/DArch=$arch", "/DSource=$publish", (Join-Path $PSScriptRoot 'NetFluss.iss'))
    Sign @(Join-Path $release "NetFluss-Setup-$Version-$arch.exe")
}

# "<sha256>  <name>" — the format sha256sum -c and the in-app updater both read.
$sums = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Path (Join-Path $release 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Get-ChildItem $release | Format-Table Name, Length
