$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

& "$PSScriptRoot\Install-Runtime.ps1"

$repo = 'LGAI-EXAONE/EXAONE-3.5-7.8B-Instruct-GGUF'
$meta = Invoke-RestMethod ('https://huggingface.co/api/models/' + $repo + '?blobs=true') -TimeoutSec 30
$file = $meta.siblings | Where-Object { $_.rfilename -eq 'EXAONE-3.5-7.8B-Instruct-Q4_K_M.gguf' } | Select-Object -First 1
if (!$file.lfs.sha256) { throw 'Q4_K_M model or SHA256 is missing' }

$dest = Join-Path $PSScriptRoot 'models\EXAONE-3.5-7.8B-Q4_K_M.gguf'
New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
$url = 'https://huggingface.co/' + $repo + '/resolve/' + $meta.sha + '/' + $file.rfilename + '?download=true'

if (!(Test-Path -LiteralPath $dest) -or (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $file.lfs.sha256) {
    $gb = [Math]::Round($file.size / 1GB, 2)
    Write-Output ("Downloading EXAONE 3.5 7.8B Q4_K_M (" + $gb + " GB): " + $file.rfilename)
    & curl.exe --location --fail --retry 3 --connect-timeout 20 --continue-at - --progress-bar --output ($dest + '.partial') $url
    if ($LASTEXITCODE -ne 0) { throw 'Model download failed' }
    Write-Output 'Verifying SHA-256 checksum...'
    if ((Get-FileHash -LiteralPath ($dest + '.partial') -Algorithm SHA256).Hash -ne $file.lfs.sha256) { throw 'Model SHA256 mismatch' }
    Move-Item -LiteralPath ($dest + '.partial') -Destination $dest -Force
} else {
    Write-Output 'Verified existing EXAONE 3.5 7.8B model.'
}

Invoke-WebRequest ('https://huggingface.co/' + $repo + '/raw/' + $meta.sha + '/LICENSE') -OutFile (Join-Path $PSScriptRoot 'models\LICENSE-EXAONE.txt') -TimeoutSec 30
@{
    baseModel        = 'LGAI-EXAONE/EXAONE-3.5-7.8B-Instruct'
    quantizationRepo = $repo
    revision         = $meta.sha
    sourceFile       = $file.rfilename
    installedFile    = 'EXAONE-3.5-7.8B-Q4_K_M.gguf'
    sha256           = $file.lfs.sha256
    installedAt      = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'exaone-manifest.json') -Encoding UTF8
Write-Output 'EXAONE 3.5 7.8B installed and SHA256 verified.'
