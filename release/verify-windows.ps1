param(
    [Parameter(Mandatory=$true)][string] $PublishDirectory,
    [Parameter(Mandatory=$true)][string] $AssetsDirectory,
    [Parameter(Mandatory=$true)][string] $Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$temp = Join-Path ([IO.Path]::GetTempPath()) ('dlss5-release-' + [Guid]::NewGuid().ToString('N'))
New-Item $temp -ItemType Directory | Out-Null
$installed = Join-Path $temp 'installed'
$portable = Join-Path $temp 'portable'

function Assert-Payload([string] $Directory) {
    $entries = Get-Content (Join-Path $PublishDirectory 'payload-sha256.json') -Raw | ConvertFrom-Json
    foreach ($entry in $entries) {
        $path = Join-Path $Directory $entry.path
        if (-not (Test-Path $path -PathType Leaf)) { throw "Payload file missing: $($entry.path)" }
        if ((Get-FileHash $path -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Payload checksum mismatch: $($entry.path)" }
    }
}
function Assert-Window([string] $Directory, [string] $Label) {
    $stderr = Join-Path $temp "$Label-stderr.log"
    $stdout = Join-Path $temp "$Label-stdout.log"
    $process = Start-Process -FilePath (Join-Path $Directory 'DLSS 5 MANAGER.exe') -WorkingDirectory $Directory -PassThru -RedirectStandardError $stderr -RedirectStandardOutput $stdout
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $visible = $false
        do {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
            if ($process.HasExited) { throw "Application exited during $Label startup ($($process.ExitCode)): $(Get-Content $stderr -Raw)" }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $visible = $true }
        } while (-not $visible -and [DateTime]::UtcNow -lt $deadline)
        if (-not $visible) { throw "No main window appeared for $Label." }
        Start-Sleep -Seconds 3
        $process.Refresh()
        if ($process.HasExited) { throw "Application crashed after opening the $Label window." }
        Write-Host "PASS ${Label}: main window opened and remained running."
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { throw "Application did not close normally: $Label" }
        if ($process.ExitCode -ne 0) { throw "Application exit code $($process.ExitCode): $Label" }
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}

try {
    Expand-Archive -Path (Join-Path $AssetsDirectory "DLSS-5-MANAGER-$Version-win-x64-portable.zip") -DestinationPath $portable
    Assert-Payload $portable
    Assert-Window $portable 'portable'
    $setup = Join-Path $AssetsDirectory "DLSS-5-MANAGER-$Version-win-x64-setup.exe"
    $setupLog = Join-Path $temp 'setup.log'
    $args = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',"/DIR=`"$installed`"", "/LOG=`"$setupLog`"")
    $setupProcess = Start-Process $setup -ArgumentList $args -Wait -PassThru
    if ($setupProcess.ExitCode -ne 0) { throw "Installer failed ($($setupProcess.ExitCode)): $(Get-Content $setupLog -Raw)" }
    Assert-Payload $installed
    Assert-Window $installed 'installed'
    $dataRoot = Join-Path $env:LOCALAPPDATA 'DLSS5Manager\Backups'
    New-Item $dataRoot -ItemType Directory -Force | Out-Null
    $marker = Join-Path $dataRoot ('release-smoke-' + [Guid]::NewGuid().ToString('N') + '.txt')
    Set-Content $marker 'retain user backups' -Encoding ascii
    try {
        $uninstall = Start-Process (Join-Path $installed 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $($uninstall.ExitCode)" }
        if (Test-Path (Join-Path $installed 'DLSS 5 MANAGER.exe')) { throw 'Uninstall left the application executable.' }
        if (-not (Test-Path $marker)) { throw 'Uninstall removed user backups.' }
        Write-Host 'PASS installer: install, payload verification, startup, uninstall, and backup retention.'
    } finally {
        if (Test-Path $marker) { Remove-Item $marker -Force }
    }
} finally {
    # This temporary directory contains only this verification run's files.
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
