$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

& "$PSScriptRoot\Install-Runtime.ps1"

$modelDir = Join-Path $PSScriptRoot 'models'
New-Item -ItemType Directory -Path $modelDir -Force | Out-Null

$repo = 'tencent/Hy-MT2-1.8B-GGUF'
$meta = Invoke-RestMethod ('https://huggingface.co/api/models/' + $repo + '?blobs=true') -TimeoutSec 30
$filename = 'Hy-MT2-1.8B-Q4_K_M.gguf'
$entry = $meta.siblings | Where-Object { $_.rfilename -eq $filename } | Select-Object -First 1
if (!$entry.lfs.sha256) { throw 'Model SHA256 metadata is missing' }

$dest = Join-Path $modelDir $filename
$modelUrl = 'https://huggingface.co/' + $repo + '/resolve/' + $meta.sha + '/' + $filename + '?download=true'

if (!(Test-Path -LiteralPath $dest) -or (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $entry.lfs.sha256) {
    $gb = [Math]::Round($entry.size / 1GB, 2)
    Write-Output ("Downloading Hy-MT2 1.8B Q4_K_M (" + $gb + " GB): " + $filename)
    & curl.exe --location --fail --retry 3 --connect-timeout 20 --continue-at - --progress-bar --output ($dest + '.partial') $modelUrl
    if ($LASTEXITCODE -ne 0) { throw ('Download failed: ' + $modelUrl) }
    Write-Output 'Verifying SHA-256 checksum...'
    if ((Get-FileHash -LiteralPath ($dest + '.partial') -Algorithm SHA256).Hash -ne $entry.lfs.sha256) { throw ('Checksum failed: ' + $dest) }
    Move-Item -LiteralPath ($dest + '.partial') -Destination $dest -Force
} else {
    Write-Output 'Verified existing Hy-MT2 1.8B model.'
}

Invoke-WebRequest ('https://huggingface.co/' + $repo + '/raw/' + $meta.sha + '/LICENSE.txt') -OutFile (Join-Path $modelDir 'LICENSE-HyMT2.txt') -TimeoutSec 30
@{
    model       = $repo
    revision    = $meta.sha
    file        = $filename
    sha256      = $entry.lfs.sha256
    installedAt = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'local-model-manifest.json') -Encoding UTF8
Write-Output 'Hy-MT2 1.8B installed and SHA256 verified.'
