$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$source=[Drawing.Bitmap]::new("$PSScriptRoot\assets\DalvitLens.png")
try {
    $sizes=@(16,24,32,48,64,128,256)
    $frames=@()
    foreach($size in $sizes){
        $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size))
        $memory=[IO.MemoryStream]::new()
        $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
        $frames+=,[byte[]]$memory.ToArray()
        $graphics.Dispose();$bitmap.Dispose();$memory.Dispose()
    }
    $file=[IO.File]::Create("$PSScriptRoot\assets\DalvitLens.ico")
    $writer=[IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++){
            $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
            $offset+=$frames[$i].Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame)}
    } finally {$writer.Dispose();$file.Dispose()}
    if($source.GetPixel(0,0).A -ne 0 -or $source.GetPixel($source.Width-1,$source.Height-1).A -ne 0){throw 'Icon exterior is not transparent'}
    Write-Output 'Icon verified: transparent exterior; 16/24/32/48/64/128/256px ICO frames.'
} finally {$source.Dispose()}
