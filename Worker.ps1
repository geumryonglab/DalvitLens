param([string]$InputPath, [string]$OutputPath, [switch]$OcrOnly, [switch]$Listen)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\OcrGrouping.ps1"
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing
$null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]

if (-not ('DalvitOcrEnhancer' -as [type])) {
    Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class DalvitOcrEnhancer {
    public static double EnhanceToFile(string srcPath, string dstPath) {
        using (Bitmap src = new Bitmap(srcPath)) {
            int w = src.Width;
            int h = src.Height;
            if (w <= 0 || h <= 0) {
                src.Save(dstPath, ImageFormat.Png);
                return 1.0;
            }
            double scale = 1.0;
            if (h < 220 || w < 360) scale = 2.0;
            else if (w * h <= 920000) scale = 1.5;

            int dstW = (int)Math.Round(w * scale);
            int dstH = (int)Math.Round(h * scale);
            if (dstW > 3800 || dstH > 3800) {
                scale = 1.0;
                dstW = w;
                dstH = h;
            }

            using (Bitmap work = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb)) {
                using (Graphics g = Graphics.FromImage(work)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, dstW, dstH), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                }

                Rectangle rect = new Rectangle(0, 0, dstW, dstH);
                BitmapData bd = work.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                try {
                    int stride = bd.Stride;
                    int totalBytes = Math.Abs(stride) * dstH;
                    byte[] buf = new byte[totalBytes];
                    Marshal.Copy(bd.Scan0, buf, 0, totalBytes);

                    long sumLuma = 0;
                    int pixelCount = dstW * dstH;
                    for (int y = 0; y < dstH; y++) {
                        int row = y * stride;
                        for (int x = 0; x < dstW; x++) {
                            int idx = row + x * 3;
                            int b = buf[idx];
                            int gr = buf[idx + 1];
                            int r = buf[idx + 2];
                            sumLuma += (77 * r + 150 * gr + 29 * b) >> 8;
                        }
                    }
                    int avgLuma = (int)(sumLuma / Math.Max(1, pixelCount));
                    bool isDarkTheme = (avgLuma < 138);

                    int[] hist = new int[256];
                    byte[] gray = new byte[pixelCount];
                    for (int y = 0; y < dstH; y++) {
                        int row = y * stride;
                        int gRow = y * dstW;
                        for (int x = 0; x < dstW; x++) {
                            int idx = row + x * 3;
                            int b = buf[idx];
                            int gr = buf[idx + 1];
                            int r = buf[idx + 2];
                            int luma = (77 * r + 150 * gr + 29 * b) >> 8;
                            int val;
                            if (isDarkTheme) {
                                int cMax = r > gr ? (r > b ? r : b) : (gr > b ? gr : b);
                                val = (luma * 3 + cMax * 5) >> 3;
                            } else {
                                val = luma;
                            }
                            if (val < 0) val = 0;
                            else if (val > 255) val = 255;
                            gray[gRow + x] = (byte)val;
                            hist[val]++;
                        }
                    }

                    int lowTarget = (int)(pixelCount * 0.08);
                    int highTarget = (int)(pixelCount * 0.998);
                    int pLow = 0, pHigh = 255, acc = 0;
                    for (int i = 0; i < 256; i++) {
                        acc += hist[i];
                        if (acc >= lowTarget) { pLow = i; break; }
                    }
                    acc = 0;
                    for (int i = 0; i < 256; i++) {
                        acc += hist[i];
                        if (acc >= highTarget) { pHigh = i; break; }
                    }
                    if (pHigh - pLow < 40) {
                        pLow = Math.Max(0, pLow - 20);
                        pHigh = Math.Min(255, pHigh + 20);
                    }
                    int span = Math.Max(1, pHigh - pLow);

                    byte[] lut = new byte[256];
                    for (int i = 0; i < 256; i++) {
                        double t = (double)(i - pLow) / span;
                        if (t < 0.0) t = 0.0;
                        else if (t > 1.0) t = 1.0;
                        double gamma = Math.Pow(t, 0.88);
                        double blended = (gamma * 0.60) + (t * 0.40);
                        int mapped = (int)Math.Round(blended * 255.0);
                        if (mapped < 0) mapped = 0;
                        else if (mapped > 255) mapped = 255;
                        lut[i] = (byte)mapped;
                    }

                    for (int i = 0; i < pixelCount; i++) {
                        gray[i] = lut[gray[i]];
                    }

                    for (int y = 0; y < dstH; y++) {
                        int row = y * stride;
                        int gRow = y * dstW;
                        bool edgeY = (y == 0 || y == dstH - 1);
                        for (int x = 0; x < dstW; x++) {
                            int outVal;
                            if (edgeY || x == 0 || x == dstW - 1) {
                                outVal = gray[gRow + x];
                            } else {
                                int c = gray[gRow + x];
                                int up = gray[gRow - dstW + x];
                                int dn = gray[gRow + dstW + x];
                                int lf = gray[gRow + x - 1];
                                int rt = gray[gRow + x + 1];
                                int sharp = (c * 5) - (up + dn + lf + rt);
                                if (sharp < 0) sharp = 0;
                                else if (sharp > 255) sharp = 255;
                                outVal = (c * 6 + sharp * 2) >> 3;
                            }
                            int idx = row + x * 3;
                            byte bVal = (byte)outVal;
                            buf[idx] = bVal;
                            buf[idx + 1] = bVal;
                            buf[idx + 2] = bVal;
                        }
                    }

                    Marshal.Copy(buf, 0, bd.Scan0, totalBytes);
                } finally {
                    work.UnlockBits(bd);
                }
                work.Save(dstPath, ImageFormat.Png);
            }
            return scale;
        }
    }
}
"@
}

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}

$script:ocrEngines = @{}

function Get-OcrEngineForLang([string]$langCode) {
    $norm = if ([string]::IsNullOrWhiteSpace($langCode)) { 'en' } else { $langCode.Trim().ToLowerInvariant() }
    if ($norm -notin @('en', 'ja', 'zh')) { $norm = 'en' }

    if ($script:ocrEngines.ContainsKey($norm) -and $null -ne $script:ocrEngines[$norm]) {
        return $script:ocrEngines[$norm]
    }

    $candidates = switch ($norm) {
        'ja' { @('ja', 'ja-JP') }
        'zh' { @('zh-Hans-CN', 'zh-CN', 'zh-Hans', 'zh-Hant-TW', 'zh-TW', 'zh-HK', 'zh') }
        default { @('en-US', 'en-GB', 'en') }
    }

    foreach ($tag in $candidates) {
        try {
            $langObj = [Windows.Globalization.Language]::new($tag)
            if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($langObj)) {
                $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($langObj)
                if ($eng) {
                    $script:ocrEngines[$norm] = $eng
                    return $eng
                }
            }
        } catch {}
    }

    try {
        foreach ($avail in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) {
            if ($avail.LanguageTag.ToLowerInvariant().StartsWith($norm)) {
                $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($avail)
                if ($eng) {
                    $script:ocrEngines[$norm] = $eng
                    return $eng
                }
            }
        }
    } catch {}

    switch ($norm) {
        'ja' { throw '일본어(JA) Windows OCR 언어팩이 없습니다. 설정 창의 [일/중 OCR 언어팩 설치] 버튼을 누르거나 Windows 설정 > 시간 및 언어 > 언어 및 지역에서 일본어를 설치하세요.' }
        'zh' { throw '중국어(ZH) Windows OCR 언어팩이 없습니다. 설정 창의 [일/중 OCR 언어팩 설치] 버튼을 누르거나 Windows 설정 > 시간 및 언어 > 언어 및 지역에서 중국어(간체/번체)를 설치하세요.' }
        default { throw '영어(EN) Windows OCR 언어팩이 없습니다. Windows 설정 > 시간 및 언어 > 언어 및 지역에서 영어(US)의 기본 입력 기능을 설치하세요.' }
    }
}

function Join-OcrWordTokens($words) {
    if (!$words -or $words.Count -eq 0) { return '' }
    $cjkRegex = '[\u3000-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF]'
    $acc = [string]$words[0].Text
    for ($i = 1; $i -lt $words.Count; $i++) {
        $w = [string]$words[$i].Text
        if ([string]::IsNullOrEmpty($w)) { continue }
        $prevLast = if ($acc.Length -gt 0) { [string]$acc[$acc.Length - 1] } else { '' }
        $currFirst = [string]$w[0]
        if (($prevLast -match $cjkRegex) -or ($currFirst -match $cjkRegex)) {
            $acc = $acc + $w
        } else {
            $acc = $acc + ' ' + $w
        }
    }
    return $acc
}

function Extract-OcrLinesFromFile([string]$imagePath, $engine, [double]$scale = 1.0) {
    $file = $null; $stream = $null; $bitmap = $null
    try {
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($imagePath)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $ocrRes = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $extracted = @()
        $invScale = if ($scale -gt 0.01) { 1.0 / $scale } else { 1.0 }
        foreach ($line in $ocrRes.Lines) {
            # Split distant UI columns even when the OCR engine calls them one line.
            $parts = @(); $words = @(); $lastRight = 0; $height = 16 * $scale
            foreach ($word in $line.Words) {
                $r = $word.BoundingRect
                if ($words.Count -gt 0 -and ($r.X - $lastRight) -gt [Math]::Max(36.0 * $scale, $height * 2.5)) {
                    $parts += ,$words
                    $words = @()
                }
                $words += $word
                $lastRight = $r.X + $r.Width
                $height = $r.Height
            }
            if ($words.Count) { $parts += ,$words }
            foreach ($part in $parts) {
                $x = (($part | ForEach-Object { $_.BoundingRect.X } | Measure-Object -Minimum).Minimum) * $invScale
                $y = (($part | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum) * $invScale
                $right = (($part | ForEach-Object { $_.BoundingRect.X + $_.BoundingRect.Width } | Measure-Object -Maximum).Maximum) * $invScale
                $bottom = (($part | ForEach-Object { $_.BoundingRect.Y + $_.BoundingRect.Height } | Measure-Object -Maximum).Maximum) * $invScale
                $joinedText = Join-OcrWordTokens $part
                $extracted += [pscustomobject]@{
                    text = $joinedText
                    x    = [Math]::Round($x, 1)
                    y    = [Math]::Round($y, 1)
                    w    = [Math]::Round(($right - $x), 1)
                    h    = [Math]::Round(($bottom - $y), 1)
                }
            }
        }
        return ,$extracted
    } finally {
        if ($bitmap) { $bitmap.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}

function Process-Image([string]$InputPath,[string]$OutputPath,[bool]$OcrOnly) {
$jobTimer=[Diagnostics.Stopwatch]::StartNew()
$enhancedPath = $InputPath + '.enhanced.png'
try {
    $config = Get-Content -LiteralPath "$PSScriptRoot\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.apiKey -and $config.apiKey.StartsWith('dpapi:')) {
        Add-Type -AssemblyName System.Security
        $config.apiKey = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($config.apiKey.Substring(6)), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    }

    $srcLang = if ($config.PSObject.Properties['sourceLang'] -and ![string]::IsNullOrWhiteSpace([string]$config.sourceLang)) {
        ([string]$config.sourceLang).Trim().ToLowerInvariant()
    } else { 'en' }
    if ($srcLang -notin @('en', 'ja', 'zh')) { $srcLang = 'en' }

    $engine = Get-OcrEngineForLang $srcLang

    # Pass 1: Contrast-Enhanced & Upscaled Preprocessing OCR (DalvitOcrEnhancer)
    $lines = @()
    $usePreprocess = $true
    if ($config.PSObject.Properties['ocrPreprocess'] -and $config.ocrPreprocess -eq $false) {
        $usePreprocess = $false
    }
    if ($usePreprocess) {
        try {
            $scale = [DalvitOcrEnhancer]::EnhanceToFile($InputPath, $enhancedPath)
            $lines = @(Extract-OcrLinesFromFile $enhancedPath $engine $scale)
        } catch {
            $lines = @()
        }
    }
    $blocks = @(Merge-OcrLines $lines)

    # Pass 2 Fallback: If contrast-enhanced pass returned 0 valid blocks, try raw bitmap directly
    if ($blocks.Count -eq 0) {
        $rawLines = @(Extract-OcrLinesFromFile $InputPath $engine 1.0)
        $blocks = @(Merge-OcrLines $rawLines)
    }

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

    $langNameEn = switch ($srcLang) {
        'ja' { 'Japanese' }
        'zh' { 'Chinese' }
        default { 'English' }
    }
    $langCodeGemma = switch ($srcLang) {
        'ja' { 'Japanese (ja)' }
        'zh' { 'Chinese (zh)' }
        default { 'English (en)' }
    }
    $googleSl = switch ($srcLang) {
        'ja' { 'ja' }
        'zh' { 'zh-CN' }
        default { 'en' }
    }

    $rpgContext = "This text is from a fantasy RPG / auto-battler game UI or tooltip ($langNameEn source). Use natural, concise Korean RPG terminology. Preserve numbers, percentages, trigger conditions, strict inequalities (below=미만, above=초과), and effect targets. Do not add explanations."
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
        $keyLang = $config.backend + '|' + $modelKey + '|' + $srcLang + '|' + $b.text
        $keyLegacy = $config.backend + '|' + $modelKey + '|' + $b.text
        if ($cache.ContainsKey($keyLang)) {
            $b.translated = $cache[$keyLang]
            $cacheHits++
            continue
        }
        if ($cache.ContainsKey($keyLegacy)) {
            $b.translated = $cache[$keyLegacy]
            $cacheHits++
            continue
        }
        $glossaryHint = Get-GlossaryPromptHint $b.text
        try {
            switch ($config.backend) {
                'exaone' {
                    $headers=@{}
                    $keyFile=Join-Path $PSScriptRoot 'local-model.key'
                    if(Test-Path $keyFile){$headers.Authorization='Bearer '+([IO.File]::ReadAllText($keyFile)).Trim()}
                    $sysPrompt = $rpgContext + "`nProduce only the final Korean translation without quotes or commentary."
                    $userPrompt = $glossaryHint + "Translate the following $langNameEn game text into Korean:`n`n" + $b.text.Trim()
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
                    $prompt="<start_of_turn>user`nYou are a professional $langCodeGemma to Korean (ko) translator. Your goal is to accurately convey the meaning and nuances of the original $langNameEn text while adhering to Korean grammar, vocabulary, and cultural sensitivities.`n"+$rpgContext+"`n"+$glossaryHint+"Produce only the Korean translation, without any additional explanations or commentary. Please translate the following $langNameEn text into Korean:`n`n`n"+$b.text.Trim()+"<end_of_turn>`n<start_of_turn>model`n"
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
                    $prompt=$rpgContext+"`n"+$glossaryHint+"Translate the following $langNameEn text into Korean. Note that you should only output the translated result without any additional explanation. Preserve numbers, percentages and conditions.`n`n"+$b.text.Trim()
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
                    $body = @{q=$srcText; source=$googleSl; target='ko'; format='text'} | ConvertTo-Json -Compress
                    $r = Invoke-RestMethod -Uri ('https://translation.googleapis.com/language/translate/v2?key='+[Uri]::EscapeDataString($config.apiKey)) -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 15
                    $b.translated = [Net.WebUtility]::HtmlDecode($r.data.translations[0].translatedText)
                }
                default {
                    $srcText = Format-SourceForGoogle $b.text
                    $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=' + $googleSl + '&tl=ko&dt=t&q='+[Uri]::EscapeDataString($srcText)
                    $r = Invoke-RestMethod -Uri $uri -TimeoutSec 12
                    $b.translated = ($r[0] | ForEach-Object { $_[0] }) -join ''
                }
            }
            $b.translated = Apply-GlossaryPost $b.text $b.translated
            if ([string]::IsNullOrWhiteSpace($b.translated)) { throw '빈 번역 응답' }
            $cache[$keyLang] = $b.translated
            if ($srcLang -eq 'en') {
                $cache[$keyLegacy] = $b.translated
            }
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
    $result=@{blocks=@($blocks); error=($errors | Select-Object -Unique) -join ' '; count=$blocks.Count; ocrMs=[Math]::Round($ocrMs,1); totalMs=[Math]::Round($jobTimer.Elapsed.TotalMilliseconds,1); cacheHits=$cacheHits; sourceLang=$srcLang; ocrEngine='DalvitOcrEnhancer+WinOCR'}
    $result | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath ($OutputPath+'.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($OutputPath+'.tmp') -Destination $OutputPath -Force
    if (!$OcrOnly -and $errors.Count -eq 0 -and $cacheHits -lt $blocks.Count) {
        $null = Invoke-AutoLearnGlossary $blocks $config.backend
    }
} catch { @{error=$_.Exception.Message; blocks=@(); count=0} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
finally {
    if (Test-Path -LiteralPath $enhancedPath) {
        Remove-Item -LiteralPath $enhancedPath -Force -ErrorAction SilentlyContinue
    }
}
}
if($Listen){
    try { $null = Get-OcrEngineForLang 'en' } catch {}
    $reader=[IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.Encoding]::UTF8)
    while($null -ne ($jobLine=$reader.ReadLine())){
        $jobLine = $jobLine.Trim([char]0xFEFF, ' ', "`t", "`r", "`n")
        if($jobLine -eq 'quit'){break}
        if([string]::IsNullOrWhiteSpace($jobLine)){continue}
        try{$job=$jobLine | ConvertFrom-Json;Process-Image $job.inputPath $job.outputPath ([bool]$job.ocrOnly)}catch{}
    }
}else{Process-Image $InputPath $OutputPath ([bool]$OcrOnly)}
