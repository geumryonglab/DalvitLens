$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

& "$PSScriptRoot\Install-Runtime.ps1"

$repo = 'mradermacher/translategemma-4b-it-GGUF'
$meta = Invoke-RestMethod ('https://huggingface.co/api/models/' + $repo + '?blobs=true') -TimeoutSec 30
$file = $meta.siblings | Where-Object { $_.rfilename -match '(?i)\.Q4_K_M\.gguf$' } | Select-Object -First 1
if (!$file.lfs.sha256) { throw 'Q4_K_M model or SHA256 is missing' }

$dest = Join-Path $PSScriptRoot 'models\TranslateGemma-4B-Q4_K_M.gguf'
New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
$url = 'https://huggingface.co/' + $repo + '/resolve/' + $meta.sha + '/' + $file.rfilename + '?download=true'

if (!(Test-Path -LiteralPath $dest) -or (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $file.lfs.sha256) {
    $gb = [Math]::Round($file.size / 1GB, 2)
    Write-Output ("Downloading TranslateGemma 4B Q4_K_M (" + $gb + " GB): " + $file.rfilename)
    & curl.exe --location --fail --retry 3 --connect-timeout 20 --continue-at - --progress-bar --output ($dest + '.partial') $url
    if ($LASTEXITCODE -ne 0) { throw 'Model download failed' }
    Write-Output 'Verifying SHA-256 checksum...'
    if ((Get-FileHash -LiteralPath ($dest + '.partial') -Algorithm SHA256).Hash -ne $file.lfs.sha256) { throw 'Model SHA256 mismatch' }
    Move-Item -LiteralPath ($dest + '.partial') -Destination $dest -Force
} else {
    Write-Output 'Verified existing TranslateGemma 4B model.'
}

$template = 'https://raw.githubusercontent.com/ggml-org/llama.cpp/b11946/models/templates/google-translategemma-4b-it.jinja'
Invoke-WebRequest $template -OutFile (Join-Path $PSScriptRoot 'models\translategemma.jinja') -TimeoutSec 30
@{
    baseModel        = 'google/translategemma-4b-it'
    quantizationRepo = $repo
    revision         = $meta.sha
    sourceFile       = $file.rfilename
    installedFile    = 'TranslateGemma-4B-Q4_K_M.gguf'
    sha256           = $file.lfs.sha256
    license          = 'Gemma Terms of Use'
    licenseUrl       = 'https://ai.google.dev/gemma/terms'
    templateSource   = $template
    installedAt      = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'translategemma-manifest.json') -Encoding UTF8
Write-Output 'TranslateGemma 4B installed and SHA256 verified.'
