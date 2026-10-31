. "$PSScriptRoot\Glossary.ps1"

function Merge-OcrLines($Lines) {
    # OCR can also split one visual row into fragments. Rejoin only when a
    # nearby preceding line spans both fragments (a wrapped paragraph).
    $rows=@()
    foreach($line in ($Lines | Sort-Object y,x)) {
        $rowMatch=$null
        foreach($row in $rows) {
            $height=[Math]::Max($row.h,$line.h)
            $sameRow=[Math]::Abs(($row.y+$row.h/2)-($line.y+$line.h/2)) -le $height*.35
            $gap=[Math]::Max($row.x,$line.x)-[Math]::Min($row.x+$row.w,$line.x+$line.w)
            if(!$sameRow -or $gap -lt 0 -or $gap -gt $height*1.5){continue}
            $left=[Math]::Min($row.x,$line.x);$right=[Math]::Max($row.x+$row.w,$line.x+$line.w)
            foreach($above in $rows) {
                $verticalGap=[Math]::Min($row.y,$line.y)-($above.y+$above.h)
                if($above -ne $row -and $verticalGap -ge -3 -and $verticalGap -lt [Math]::Max(12,$height*.85) -and $above.x -le $left+$height*.5 -and $above.x+$above.w -ge $right-$height*.5){$rowMatch=$row;break}
            }
            if($rowMatch){break}
        }
        if($rowMatch){
            $rowMatch.text=if($line.x -lt $rowMatch.x){$line.text+' '+$rowMatch.text}else{$rowMatch.text+' '+$line.text}
            $bottom=[Math]::Max($rowMatch.y+$rowMatch.h,$line.y+$line.h)
            $rowMatch.x=$left;$rowMatch.w=$right-$left;$rowMatch.y=[Math]::Min($rowMatch.y,$line.y);$rowMatch.h=$bottom-$rowMatch.y
        }else{$rows += [pscustomobject]@{text=$line.text;x=$line.x;y=$line.y;w=$line.w;h=$line.h}}
    }
    $blocks = @()
    foreach ($line in ($rows | Sort-Object y,x)) {
        if ($line.text -notmatch '[a-zA-Z]{2}') { continue }
        $match = $null; $bestGap = [double]::MaxValue
        foreach ($b in $blocks) {
            # Compare with the last line, not the growing paragraph rectangle.
            $gap = $line.y - ($b.lastY + $b.lastH)
            $height = [Math]::Max($line.h, $b.lastH)
            $sizeRatio = [Math]::Min($line.h,$b.lastH) / [Math]::Max(1,$height)
            $overlap = [Math]::Min($line.x+$line.w,$b.lastX+$b.lastW) - [Math]::Max($line.x,$b.lastX)
            $leftAligned = [Math]::Abs($line.x-$b.lastX) -le [Math]::Max(12,$height*.85)
            $centerAligned = [Math]::Abs(($line.x+$line.w/2)-($b.lastX+$b.lastW/2)) -le [Math]::Max(12,$height*.8)
            $sharedWidth = $overlap -ge [Math]::Min($line.w,$b.lastW)*.65
            if ($gap -ge -3 -and $gap -lt [Math]::Max(12,$height*.85) -and $sizeRatio -ge .72 -and $sharedWidth -and ($leftAligned -or $centerAligned) -and $gap -lt $bestGap) {
                $match=$b; $bestGap=$gap
            }
        }
        if ($match) {
            # A visual wrap is a space, not a translation boundary.
            $match.text += ' ' + $line.text.Trim()
            $right=[Math]::Max($match.x+$match.w,$line.x+$line.w)
            $match.x=[Math]::Min($match.x,$line.x); $match.w=$right-$match.x
            $match.h=[Math]::Max($match.y+$match.h,$line.y+$line.h)-$match.y
            $match.lastX=$line.x; $match.lastY=$line.y; $match.lastW=$line.w; $match.lastH=$line.h
        } else {
            $blocks += [pscustomobject]@{text=$line.text.Trim();x=$line.x;y=$line.y;w=$line.w;h=$line.h;translated='';lastX=$line.x;lastY=$line.y;lastW=$line.w;lastH=$line.h}
        }
    }
    $cleanedBlocks = @()
    foreach ($b in $blocks) {
        $b.text = Clean-OcrText $b.text
        if (Test-ValidOcrBlock $b.text) {
            $cleanedBlocks += $b
        }
    }
    # Keep internal line geometry out of the worker protocol.
    $cleanedBlocks | Select-Object text,x,y,w,h,translated
}
