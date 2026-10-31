$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$image = [Drawing.Bitmap]::new(1200,700)
$g = [Drawing.Graphics]::FromImage($image)
$g.Clear([Drawing.Color]::FromArgb(30,35,45))
$font = [Drawing.Font]::new('Arial',22)
$g.DrawString("Flame Strike`nDeals 120 damage.`nGain 20% attack speed for 4 seconds.", $font, [Drawing.Brushes]::White, 40,60)
$g.DrawString("Ice Shield`nGain 50 armor.`nLasts for 8 seconds.", $font, [Drawing.Brushes]::White, 700,60)
$image.Save("$PSScriptRoot\verification.png")
$g.Dispose(); $font.Dispose(); $image.Dispose()
& "$PSScriptRoot\Worker.ps1" -InputPath "$PSScriptRoot\verification.png" -OutputPath "$PSScriptRoot\verification-ocr.json" -OcrOnly
$r = Get-Content "$PSScriptRoot\verification-ocr.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if ($r.error) { throw $r.error }
if ($r.blocks.Count -lt 2) { throw 'Separate columns were merged.' }
$text = $r.blocks.text -join ' '
foreach ($expected in @('120','20%','4','50','8')) { if (!$text.Contains($expected)) { throw "OCR missed $expected" } }
Write-Output ('OCR verified: '+$r.blocks.Count+' spatial blocks; numbers preserved.')
