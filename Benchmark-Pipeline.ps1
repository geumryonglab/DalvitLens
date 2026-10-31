$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$benchDir=Join-Path $PSScriptRoot 'benchmark'
New-Item -ItemType Directory -Path $benchDir -Force | Out-Null
$samples=@(
    @{name='short';text='Gain 23% attack speed for 7 seconds.'},
    @{name='tooltip';text="Frost Nova`nDeal 137 magic damage to all enemies within 4 meters.`nFrozen enemies take 27% additional damage for 9 seconds.`nThis effect stacks up to 3 times."},
    @{name='conditional';text="When an ally falls below 31% health, grant them a shield`nequal to 17% of your maximum health for 8 seconds.`nCooldown: 14 seconds. Does not stack."}
)
foreach($sample in $samples){
    $bitmap=[Drawing.Bitmap]::new(1050,330)
    $bitmap.SetResolution(96,96)
    $g=[Drawing.Graphics]::FromImage($bitmap)
    $g.Clear([Drawing.Color]::FromArgb(22,20,38))
    $font=[Drawing.Font]::new('Arial',20)
    $g.DrawString($sample.text,$font,[Drawing.Brushes]::White,[Drawing.RectangleF]::new(30,40,990,270))
    $bitmap.Save((Join-Path $benchDir ($sample.name+'.png')))
    $font.Dispose();$g.Dispose();$bitmap.Dispose()
}
$psi=[Diagnostics.ProcessStartInfo]::new('powershell.exe',('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSScriptRoot+'\Worker.ps1" -Listen'))
$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardInput=$true
$worker=[Diagnostics.Process]::Start($psi)
$rows=@()
try {
    for($run=1;$run -le 3;$run++){foreach($sample in $samples){
        $output=Join-Path $benchDir ([Guid]::NewGuid().ToString('N')+'.json')
        $job=@{inputPath=(Join-Path $benchDir ($sample.name+'.png'));outputPath=$output;ocrOnly=$false} | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $worker.StandardInput.WriteLine($job);$worker.StandardInput.Flush()
        while(!(Test-Path -LiteralPath $output)){
            if($worker.HasExited){throw 'Resident OCR worker exited'}
            if($timer.Elapsed.TotalSeconds -gt 30){throw 'OCR/translation pipeline timed out'}
            Start-Sleep -Milliseconds 10
        }
        $timer.Stop();$r=Get-Content -LiteralPath $output -Raw -Encoding UTF8 | ConvertFrom-Json
        if($r.error){throw $r.error}
        $translation=$r.blocks.translated -join "`n"
        $missing=@([regex]::Matches($sample.text,'\d+') | ForEach-Object {$_.Value} | Where-Object {$translation -notmatch ('(?<!\d)'+[regex]::Escape($_)+'(?!\d)')})
        $rows+=[pscustomobject]@{run=$run;sample=$sample.name;wallMs=[Math]::Round($timer.Elapsed.TotalMilliseconds,1);workerMs=$r.totalMs;ocrMs=$r.ocrMs;blocks=$r.count;cacheHits=$r.cacheHits;missingNumbers=$missing;translation=$translation}
    }}
}finally{try{$worker.StandardInput.WriteLine('quit');$worker.StandardInput.Flush();if(!$worker.WaitForExit(2000)){$worker.Kill()}}catch{}$worker.Dispose()}
$rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $PSScriptRoot 'benchmark-pipeline-results.json') -Encoding UTF8
$rows | Select-Object run,sample,wallMs,workerMs,ocrMs,blocks,cacheHits,@{n='missingNumbers';e={$_.missingNumbers -join ','}} | Format-Table -AutoSize
