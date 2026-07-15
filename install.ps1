#Requires -Version 5.1
# Installs the latest axiom-cli release for Windows.
# Usage: irm https://raw.githubusercontent.com/YoMosa2009/Axiom-CLI/main/install.ps1 | iex
$ErrorActionPreference = "Stop"

$Repo = "YoMosa2009/Axiom-CLI"
$InstallDir = if ($env:AXIOM_INSTALL_DIR) { $env:AXIOM_INSTALL_DIR } else { "$env:LOCALAPPDATA\axiom-cli\bin" }

$arch = if ([System.Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
} else {
    Write-Error "32-bit Windows is not supported."
}

if ($arch -eq "arm64") {
    Write-Error "win-arm64 is not yet published. Build from source: https://github.com/$Repo"
}

$asset = "axiom-cli-win-$arch.zip"
$apiUrl = "https://api.github.com/repos/$Repo/releases/latest"

Write-Host "Detecting latest release..."
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "axiom-cli-installer" }
$downloadUrl = ($release.assets | Where-Object { $_.name -eq $asset }).browser_download_url

if (-not $downloadUrl) {
    Write-Error "Could not find a release asset named '$asset'. See https://github.com/$Repo/releases"
}

$tempDir = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $zipPath = Join-Path $tempDir $asset
    Write-Host "Downloading $asset..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

    Write-Host "Extracting..."
    Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $tempDir "axiom.exe") -Destination $InstallDir -Force
    Get-ChildItem -Path $tempDir -Filter "*.dll" | Copy-Item -Destination $InstallDir -Force

    Write-Host "Installed axiom to $InstallDir\axiom.exe"

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*") {
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
        Write-Host ""
        Write-Host "Added $InstallDir to your user PATH. Restart your terminal for it to take effect."
    }

    Write-Host ""
    Write-Host "Run 'axiom config' to set your OpenRouter API key, then 'axiom chat' or 'axiom code `"<task>`"'."
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
