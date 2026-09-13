param([string] $OutputRoot = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
$meta = Get-Content (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
if ($meta.version -notmatch '^\d+\.\d+\.\d+-fork\.\d+$' -or $meta.tag -cne "v$($meta.version)" -or $meta.fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid fork release version.' }
$sourceSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify source commit.' }
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $output) { throw "Output directory already exists: $output" }
$publish = Join-Path $output 'app'
$assets = Join-Path $output 'assets'
New-Item $publish, $assets -ItemType Directory -Force | Out-Null

& dotnet fsi --exec tests/RegressionTests.fsx
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
& dotnet publish 'DLSS 5 MANAGER.fsproj' --configuration Release --runtime win-x64 --self-contained true --output $publish -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
foreach ($name in @('DLSS 5 MANAGER.exe','DLSS 5 MANAGER.dll','DLSS 5 MANAGER.deps.json','DLSS 5 MANAGER.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','Avalonia.Controls.dll','libSkiaSharp.dll','languages\languages.JSON')) {
    if (-not (Test-Path (Join-Path $publish $name) -PathType Leaf)) { throw "Published dependency missing: $name" }
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'DLSS 5 MANAGER.exe')).ProductVersion
if (-not $version.StartsWith($meta.version, [StringComparison]::Ordinal)) { throw "Unexpected application version: $version" }
Copy-Item 'Copyright.txt', 'release/README-Windows.txt' $publish
New-Item (Join-Path $publish 'docs') -ItemType Directory | Out-Null
Copy-Item 'docs/runtime-packages.md', 'docs/health-check-and-restore.md' (Join-Path $publish 'docs')
$info = [ordered]@{ repository='mutsuki14/DLSS-5-MANAGER'; commit=$sourceSha; tag=$meta.tag; version=$meta.version; runtime='win-x64'; selfContained=$true; builtAtUtc=[DateTime]::UtcNow.ToString('o') }
$info | ConvertTo-Json | Set-Content (Join-Path $publish 'build-info.json') -Encoding utf8
$inventory = @(Get-ChildItem $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/'); sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$_.Length }
})
ConvertTo-Json -InputObject $inventory -Depth 4 | Set-Content (Join-Path $publish 'payload-sha256.json') -Encoding utf8

$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $compiler)) { throw 'Inno Setup 6 is required to build the installer.' }
& $compiler "/DSourceDir=$publish" "/DOutputDir=$assets" "/DAppVersion=$($meta.version)" "/DFileVersion=$($meta.fileVersion)" 'release/installer.iss'
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$zip = Join-Path $assets "DLSS-5-MANAGER-$($meta.version)-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
& (Join-Path $PSScriptRoot 'verify-windows.ps1') -PublishDirectory $publish -AssetsDirectory $assets -Version $meta.version
if (-not $?) { throw 'Windows package verification failed.' }
Copy-Item (Join-Path $publish 'build-info.json') $assets
$checksums = Get-ChildItem $assets -File | Sort-Object Name | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
$checksums | Set-Content (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Verified release assets: $assets"
