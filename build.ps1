$ErrorActionPreference = 'Stop'
$fxDir    = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$compiler = Join-Path $fxDir "csc.exe"

# 1. Build DalvitLens.exe (Main App + In-App Model Manager)
& $compiler /nologo /target:winexe /optimize+ /utf8output `
    "/out:$PSScriptRoot\DalvitLens.exe" `
    "/win32icon:$PSScriptRoot\assets\DalvitLens.ico" `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    /reference:System.Management.dll `
    "/reference:$fxDir\System.IO.Compression.dll" `
    "/reference:$fxDir\System.IO.Compression.FileSystem.dll" `
    "$PSScriptRoot\GuildLens.cs" `
    "$PSScriptRoot\ModelInstaller.cs"

if ($LASTEXITCODE -ne 0) { throw 'DalvitLens.exe build failed' }
Copy-Item -LiteralPath "$PSScriptRoot\DalvitLens.exe" -Destination "$PSScriptRoot\GuildLens.exe" -Force

# 2. Pack embedded runtime payload for single-file Setup Wizard
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$payloadZip = Join-Path $PSScriptRoot "payload.zip"
if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }

$payloadEntries = @(
    "DalvitLens.exe",
    "Worker.ps1",
    "OcrGrouping.ps1",
    "Glossary.ps1",
    "glossary.json",
    "Connect-OBS.ps1",
    "Install-Runtime.ps1",
    "Install-Exaone.ps1",
    "Install-TranslateGemma.ps1",
    "Install-HyMT.ps1",
    "LICENSE",
    "README.md",
    "assets/DalvitLens.ico"
)

$fs = [System.IO.File]::Open($payloadZip, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($rel in $payloadEntries) {
            $srcPath = Join-Path $PSScriptRoot ($rel -replace '/', '\')
            if (!(Test-Path -LiteralPath $srcPath)) { throw "Missing payload file: $srcPath" }
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $srcPath, $rel, [System.IO.Compression.CompressionLevel]::Optimal
            )
        }
    } finally {
        $zip.Dispose()
    }
} finally {
    $fs.Dispose()
}

# 3. Build single-file installer dist\DalvitLens-Setup.exe
$distDir  = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$setupExe = Join-Path $distDir "DalvitLens-Setup.exe"

& $compiler /nologo /target:winexe /optimize+ /utf8output `
    "/out:$setupExe" `
    "/win32icon:$PSScriptRoot\assets\DalvitLens.ico" `
    "/resource:$payloadZip,DalvitLens.Payload.zip" `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Management.dll `
    "/reference:$fxDir\System.IO.Compression.dll" `
    "/reference:$fxDir\System.IO.Compression.FileSystem.dll" `
    "$PSScriptRoot\SetupWizard.cs" `
    "$PSScriptRoot\ModelInstaller.cs"

$setupExit = $LASTEXITCODE
Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
if ($setupExit -ne 0) { throw 'DalvitLens-Setup.exe build failed' }

$appInfo   = Get-Item -LiteralPath "$PSScriptRoot\DalvitLens.exe"
$setupInfo = Get-Item -LiteralPath $setupExe
Write-Output ("BUILD OK: DalvitLens.exe ({0:N1} KB) | dist\DalvitLens-Setup.exe ({1:N1} KB)" -f ($appInfo.Length / 1KB), ($setupInfo.Length / 1KB))
