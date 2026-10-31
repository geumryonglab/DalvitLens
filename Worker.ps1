param([string]$InputPath, [string]$OutputPath, [switch]$OcrOnly, [switch]$Listen)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\OcrGrouping.ps1"
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}
function Process-Image([string]$InputPath,[string]$OutputPath,[bool]$OcrOnly) {
$jobTimer=[Diagnostics.Stopwatch]::StartNew()
try {
    $config = Get-Content -LiteralPath "$PSScriptRoot\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.apiKey -and $config.apiKey.StartsWith('dpapi:')) {
        Add-Type -AssemblyName System.Security
        $config.apiKey = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($config.apiKey.Substring(6)), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    }
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($InputPath)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    if (!$script:engine) { $script:engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US')) }
    $engine=$script:engine
    if (!$engine) { throw '영어 OCR 언어가 없습니다. Windows 설정 > 시간 및 언어 > 언어 및 지역에서 영어의 기본 입력 기능을 설치하세요.' }
    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines = @()
    foreach ($line in $result.Lines) {
        # Split distant UI columns even when the OCR engine calls them one line.
        $parts = @(); $words = @(); $lastRight = 0; $height = 16
        foreach ($word in $line.Words) {
            $r = $word.BoundingRect
            if ($words.Count -gt 0 -and $r.X - $lastRight -gt [Math]::Max(36, $height * 2.5)) { $parts += ,$words; $words = @() }
            $words += $word; $lastRight = $r.X + $r.Width; $height = $r.Height
        }
        if ($words.Count) { $parts += ,$words }
        foreach ($part in $parts) {
            $x = ($part | ForEach-Object { $_.BoundingRect.X } | Measure-Object -Minimum).Minimum
            $y = ($part | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum
            $right = ($part | ForEach-Object { $_.BoundingRect.X + $_.BoundingRect.Width } | Measure-Object -Maximum).Maximum
            $bottom = ($part | ForEach-Object { $_.BoundingRect.Y + $_.BoundingRect.Height } | Measure-Object -Maximum).Maximum
            $lines += [pscustomobject]@{ text=($part.Text -join ' '); x=$x; y=$y; w=$right-$x; h=$bottom-$y }
        }
    }
    $blocks = @(Merge-OcrLines $lines)
    $cachePath = "$PSScriptRoot\translation-cache.json"
    $cacheMtime = if (Test-Path -LiteralPath $cachePath) { (Get-Item -LiteralPath $cachePath).LastWriteTimeUtc } else { [DateTime]::MinValue }
    if (!$script:cache -or $script:cacheStamp -ne $cacheMtime) {
        $script:cache = @{}
        if (Test-Path -LiteralPath $cachePath) {
            try {
                $rawC = Get-Content -LiteralPath $cachePath -Raw -Encoding UTF8
                $bIdx = $rawC.IndexOf('{')
                if ($bIdx -gt 0) { $rawC = $rawC.Substring($bIdx) }
                ($rawC | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $script:cache[$_.Name]=$_.Value }
            } catch {}
        }
        $script:cacheStamp = $cacheMtime
    }
    $cache=$script:cache
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $errors = @()
    $ocrMs=$jobTimer.Elapsed.TotalMilliseconds
    $cacheHits=0
    $rpgContext = "This text is from a fantasy RPG / auto-battler game UI or tooltip. Use natural, concise Korean RPG terminology. Preserve numbers, percentages, trigger conditions, strict inequalities (below=미만, above=초과), and effect targets. Do not add explanations."
    foreach ($b in $blocks) {
        if ($OcrOnly) { $b.translated = $b.text; continue }
        $exactMatch = Get-ExactGlossaryTranslation $b.text
        if ($exactMatch) {
            $b.translated = $exactMatch
            $cacheHits++
            continue
        }
        $modelKey = switch ($config.backend) {
            'hymt'            { 'Hy-MT2-1.8B-Q4-v2' }
            'gemma'           { 'TranslateGemma-4B-Q4-v2' }
            'exaone'          { 'EXAONE-3.5-7.8B-Q4-v2' }
            'google-official' { 'GoogleCloud-v2' }
            default           { 'GoogleFree-v2' }
        }
        $key = $config.backend + '|' + $modelKey + '|' + $b.text
        if ($cache.ContainsKey($key)) { $b.translated = $cache[$key]; $cacheHits++; continue }
        $glossaryHint = Get-GlossaryPromptHint $b.text
        try {
            switch ($config.backend) {
                'exaone' {
                    $headers=@{}
                    $keyFile=Join-Path $PSScriptRoot 'local-model.key'
                    if(Test-Path $keyFile){$headers.Authorization='Bearer '+([IO.File]::ReadAllText($keyFile)).Trim()}
                    $sysPrompt = $rpgContext + "`nProduce only the final Korean translation without quotes or commentary."
                    $userPrompt = $glossaryHint + "Translate the following game text into Korean:`n`n" + $b.text.Trim()
                    $body=@{model='dalvit-exaone-7.8b';messages=@(@{role='system';content=$sysPrompt},@{role='user';content=$userPrompt});stream=$false;max_tokens=1024;temperature=0;top_p=1;repeat_penalty=1.05;seed=42;cache_prompt=$true} | ConvertTo-Json -Depth 6 -Compress
                    try {
                        $r=Invoke-RestMethod -Uri 'http://127.0.0.1:18766/v1/chat/completions' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
                    } catch { throw 'EXAONE 3.5 엔진 연결에 실패했습니다. 설정의 로컬 엔진 상태를 확인하세요.' }
                    if($r.choices[0].finish_reason -eq 'length'){throw '설명이 너무 길어 번역이 잘렸습니다. F8 영역을 줄여주세요.'}
                    $b.translated=$r.choices[0].message.content.Trim()
                }
                'gemma' {
                    $headers=@{}
                    $keyFile=Join-Path $PSScriptRoot 'local-model.key'
                    if(Test-Path $keyFile){$headers.Authorization='Bearer '+([IO.File]::ReadAllText($keyFile)).Trim()}
                    $prompt="<start_of_turn>user`nYou are a professional English (en) to Korean (ko) translator. Your goal is to accurately convey the meaning and nuances of the original English text while adhering to Korean grammar, vocabulary, and cultural sensitivities.`n"+$rpgContext+"`n"+$glossaryHint+"Produce only the Korean translation, without any additional explanations or commentary. Please translate the following English text into Korean:`n`n`n"+$b.text.Trim()+"<end_of_turn>`n<start_of_turn>model`n"
                    $body=@{prompt=$prompt;stream=$false;n_predict=1024;temperature=0;top_p=1;repeat_penalty=1.05;seed=42;cache_prompt=$true} | ConvertTo-Json -Depth 6 -Compress
                    try{$r=Invoke-RestMethod -Uri 'http://127.0.0.1:18766/completion' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30}
                    catch{throw 'TranslateGemma 엔진 연결에 실패했습니다. GPU 준비 상태를 확인하세요.'}
                    if($r.stopped_limit){throw '설명이 너무 길어 번역이 잘렸습니다. F8 영역을 줄여주세요.'}
                    $b.translated=$r.content.Trim()
                }
                'hymt' {
                    $headers=@{}
                    $keyFile=Join-Path $PSScriptRoot 'local-model.key'
                    if(Test-Path $keyFile){$headers.Authorization='Bearer '+([IO.File]::ReadAllText($keyFile)).Trim()}
                    $prompt=$rpgContext+"`n"+$glossaryHint+"Translate the following text into Korean. Note that you should only output the translated result without any additional explanation. Preserve numbers, percentages and conditions.`n`n"+$b.text.Trim()
                    $body=@{model='dalvit-hymt2-1.8b';messages=@(@{role='user';content=$prompt});stream=$false;max_tokens=1024;temperature=0;top_p=1;repeat_penalty=1.05;seed=42;cache_prompt=$true} | ConvertTo-Json -Depth 6 -Compress
                    try {
                        $r=Invoke-RestMethod -Uri 'http://127.0.0.1:18766/v1/chat/completions' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
                    } catch { throw 'Hy-MT2 엔진이 아직 준비되지 않았거나 연결에 실패했습니다. 설정의 로컬 엔진 상태를 확인하세요.' }
                    if($r.choices[0].finish_reason -eq 'length'){throw '설명이 너무 길어 번역이 잘렸습니다. F8 영역을 줄여주세요.'}
                    $b.translated=$r.choices[0].message.content.Trim()
                }
                'google-official' {
                    if (!$config.apiKey) { throw '설정에서 Google Cloud API 키를 입력하세요.' }
                    $srcText = Format-SourceForGoogle $b.text
                    $body = @{q=$srcText; source='en'; target='ko'; format='text'} | ConvertTo-Json -Compress
                    $r = Invoke-RestMethod -Uri ('https://translation.googleapis.com/language/translate/v2?key='+[Uri]::EscapeDataString($config.apiKey)) -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 15
                    $b.translated = [Net.WebUtility]::HtmlDecode($r.data.translations[0].translatedText)
                }
                default {
                    $srcText = Format-SourceForGoogle $b.text
                    $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=ko&dt=t&q='+[Uri]::EscapeDataString($srcText)
                    $r = Invoke-RestMethod -Uri $uri -TimeoutSec 12
                    $b.translated = ($r[0] | ForEach-Object { $_[0] }) -join ''
                }
            }
            $b.translated = Apply-GlossaryPost $b.text $b.translated
            if ([string]::IsNullOrWhiteSpace($b.translated)) { throw '빈 번역 응답' }
            $cache[$key] = $b.translated
        } catch {
            $b.translated = '[번역 실패] ' + $b.text
            $errors += if($config.backend -in @('hymt','gemma','exaone')){$_.Exception.Message}else{'번역 서버 연결 또는 설정을 확인하세요.'}
            # Avoid one timeout per remaining block when the service is unavailable.
            foreach ($remaining in $blocks) { if (!$remaining.translated) { $remaining.translated = '[번역 실패] ' + $remaining.text } }
            break
        }
    }
    if (!$OcrOnly -and $cacheHits -lt $blocks.Count) {
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($cachePath, ($cache | ConvertTo-Json -Depth 4), $utf8NoBom)
        $script:cacheStamp = (Get-Item -LiteralPath $cachePath).LastWriteTimeUtc
    }
    $jobTimer.Stop()
    $result=@{blocks=@($blocks); error=($errors | Select-Object -Unique) -join ' '; count=$blocks.Count;ocrMs=[Math]::Round($ocrMs,1);totalMs=[Math]::Round($jobTimer.Elapsed.TotalMilliseconds,1);cacheHits=$cacheHits}
    $result | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath ($OutputPath+'.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($OutputPath+'.tmp') -Destination $OutputPath -Force
    if (!$OcrOnly -and $errors.Count -eq 0 -and $cacheHits -lt $blocks.Count) {
        $null = Invoke-AutoLearnGlossary $blocks $config.backend
    }
} catch { @{error=$_.Exception.Message; blocks=@(); count=0} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
finally { if ($bitmap) { $bitmap.Dispose() }; if ($stream) { $stream.Dispose() } }
}
if($Listen){
    $script:engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US'))
    $reader=[IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.Encoding]::UTF8)
    while($null -ne ($jobLine=$reader.ReadLine())){
        $jobLine = $jobLine.Trim([char]0xFEFF, ' ', "`t", "`r", "`n")
        if($jobLine -eq 'quit'){break}
        if([string]::IsNullOrWhiteSpace($jobLine)){continue}
        try{$job=$jobLine | ConvertFrom-Json;Process-Image $job.inputPath $job.outputPath ([bool]$job.ocrOnly)}catch{}
    }
}else{Process-Image $InputPath $OutputPath ([bool]$OcrOnly)}
