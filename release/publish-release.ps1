param(
    [Parameter(Mandatory=$true)][string] $AssetsDirectory,
    [string] $SourceCommit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repository = 'mutsuki14/DLSS-5-MANAGER'
if ($env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_REF -cne 'refs/heads/codex/health-check-safe-restore') { throw 'Release publishing is restricted to the fork development branch.' }
if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid source commit.' }
$meta = Get-Content (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
$info = Get-Content (Join-Path $AssetsDirectory 'build-info.json') -Raw | ConvertFrom-Json
if ($info.commit -cne $SourceCommit -or $info.tag -cne $meta.tag -or $info.repository -cne $repository) { throw 'Artifact provenance does not match this workflow.' }
$prefix = "DLSS-5-MANAGER-$($meta.version)-win-x64"
$expectedNames = @("$prefix-setup.exe", "$prefix-portable.zip", 'SHA256SUMS.txt', 'build-info.json')
$files = @(Get-ChildItem $AssetsDirectory -File)
if (@(Compare-Object ($expectedNames | Sort-Object) ($files.Name | Sort-Object)).Count -ne 0) { throw 'Unexpected or missing release assets.' }
foreach ($line in Get-Content (Join-Path $AssetsDirectory 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Malformed checksum line.' }
    $actual = (Get-FileHash (Join-Path $AssetsDirectory $Matches[2]) -Algorithm SHA256).Hash
    if ($actual -ine $Matches[1]) { throw "Artifact checksum mismatch: $($Matches[2])" }
}
$existingJson = & gh release view $meta.tag --repo $repository --json isDraft,targetCommitish 2>$null
if ($LASTEXITCODE -eq 0) {
    $existing = $existingJson | ConvertFrom-Json
    if (-not $existing.isDraft) { throw 'This release is already published; published assets are immutable in this workflow.' }
    if ($existing.targetCommitish -cne $SourceCommit) { throw 'An existing draft belongs to a different commit.' }
} else {
    & gh release create $meta.tag --repo $repository --target $SourceCommit --title $meta.name --notes-file (Join-Path $PSScriptRoot 'notes.md') --draft --prerelease
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the draft release.' }
}
# The REST tag endpoint does not expose unpublished drafts. Resolve the draft ID first.
$releaseId = & gh release view $meta.tag --repo $repository --json databaseId --jq .databaseId
if ($LASTEXITCODE -ne 0 -or $releaseId -notmatch '^\d+$') { throw 'Cannot resolve the release ID.' }
function Get-RemoteRelease {
    $json = & gh api "repos/$repository/releases/$releaseId"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify uploaded assets.' }
    return ($json | ConvertFrom-Json)
}
$uploaded = Get-RemoteRelease
$needsUpload = $false
foreach ($file in $files) {
    $asset = @($uploaded.assets | Where-Object name -CEQ $file.Name)
    $digest = 'sha256:' + (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or $asset[0].digest -cne $digest) { $needsUpload = $true }
}
if ($needsUpload) {
    & gh release upload $meta.tag @($files.FullName) --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; release remains a draft.' }
    $uploaded = Get-RemoteRelease
}
# Verify server-side digests and byte lengths before exposing the release.
if (@($uploaded.assets).Count -ne $expectedNames.Count) { throw 'Uploaded asset count differs.' }
foreach ($file in $files) {
    $asset = @($uploaded.assets | Where-Object name -CEQ $file.Name)
    if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or $asset[0].state -ne 'uploaded') { throw "Uploaded asset is incomplete: $($file.Name)" }
    $digest = 'sha256:' + (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset[0].digest -cne $digest) { throw "Server checksum mismatch: $($file.Name)" }
}
& gh release edit $meta.tag --repo $repository --draft=false --prerelease --latest=false
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the verified release.' }
Write-Host "Published https://github.com/$repository/releases/tag/$($meta.tag)"
