$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$downloadDir = Join-Path $PSScriptRoot 'downloads'
$runtimeDir  = Join-Path $PSScriptRoot 'runtime\llama'
foreach ($dir in @($downloadDir, $runtimeDir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$serverExe = Join-Path $runtimeDir 'llama-server.exe'
if (Test-Path -LiteralPath $serverExe) {
    Write-Output "Verified existing CUDA runtime: $serverExe"
    return
}

function Download-Verified([string]$url, [string]$path, [string]$sha) {
    if ((Test-Path -LiteralPath $path) -and $sha -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha) {
        Write-Output ('Verified existing archive: ' + [IO.Path]::GetFileName($path))
        return
    }
    Write-Output ('Downloading runtime package: ' + [IO.Path]::GetFileName($path))
    & curl.exe --location --fail --retry 3 --connect-timeout 20 --progress-bar --output ($path + '.partial') $url
    if ($LASTEXITCODE -ne 0) { throw ('Download failed: ' + $url) }
    if ($sha -and (Get-FileHash -LiteralPath ($path + '.partial') -Algorithm SHA256).Hash -ne $sha) {
        throw ('Checksum failed: ' + $path)
    }
    Move-Item -LiteralPath ($path + '.partial') -Destination $path -Force
}

$headers = @{ 'User-Agent' = 'DalvitLens-Installer/1.0' }
$releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10' -Headers $headers -TimeoutSec 30
$release = $releases | Where-Object {
    @($_.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cuda-12\.4-x64\.zip$' }).Count -gt 0
} | Select-Object -First 1

if (!$release) { throw 'No official Windows CUDA 12.4 package found on ggml-org/llama.cpp' }

$assets = @($release.assets | Where-Object {
    $_.name -match '^llama-.*-bin-win-cuda-12\.4-x64\.zip$|^cudart-llama-bin-win-cuda-12\.4-x64\.zip$'
})

foreach ($asset in $assets) {
    $path = Join-Path $downloadDir $asset.name
    $sha = if ($asset.digest) { $asset.digest -replace '^sha256:', '' } else { '' }
    Download-Verified $asset.browser_download_url $path $sha
    Write-Output ('Extracting: ' + $asset.name)
    Expand-Archive -LiteralPath $path -DestinationPath $runtimeDir -Force
}

if (!(Test-Path -LiteralPath $serverExe)) {
    $nested = Get-ChildItem -LiteralPath $runtimeDir -Recurse -Filter 'llama-server.exe' | Select-Object -First 1
    if ($nested -and $nested.DirectoryName -ne $runtimeDir) {
        Get-ChildItem -LiteralPath $nested.DirectoryName -Force | Move-Item -Destination $runtimeDir -Force
    }
}

if (!(Test-Path -LiteralPath $serverExe)) { throw 'llama-server.exe not found after extraction.' }
Write-Output ("CUDA runtime ready (" + $release.tag_name + "): " + $serverExe)
