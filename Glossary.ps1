function Get-GlossaryConfig {
    $glossaryPath = Join-Path $PSScriptRoot 'glossary.json'
    if (Test-Path -LiteralPath $glossaryPath) {
        $mtime = (Get-Item -LiteralPath $glossaryPath).LastWriteTimeUtc
        if (!$script:glossaryConfig -or $script:glossaryStamp -ne $mtime) {
            try {
                $raw = Get-Content -LiteralPath $glossaryPath -Raw -Encoding UTF8
                $brace = $raw.IndexOf('{')
                if ($brace -gt 0) { $raw = $raw.Substring($brace) }
                $script:glossaryConfig = $raw | ConvertFrom-Json
                $script:glossaryStamp = $mtime
                $script:exactTable = @{}
                if ($script:glossaryConfig.exact) {
                    foreach ($p in $script:glossaryConfig.exact.PSObject.Properties) {
                        $script:exactTable[$p.Name.Trim().ToUpperInvariant()] = [string]$p.Value
                    }
                }
            } catch {
                if (!$script:glossaryConfig) {
                    $script:glossaryConfig = [pscustomobject]@{ exact = [pscustomobject]@{}; terms = @(); postReplace = @() }
                    $script:exactTable = @{}
                }
            }
        }
    }
    return $script:glossaryConfig
}

function Save-GlossaryConfig($cfg) {
    $glossaryPath = Join-Path $PSScriptRoot 'glossary.json'
    $tmpPath = $glossaryPath + '.tmp'
    $json = $cfg | ConvertTo-Json -Depth 8
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($tmpPath, $json, $utf8NoBom)
    Move-Item -LiteralPath $tmpPath -Destination $glossaryPath -Force
    $script:glossaryConfig = $null
    $null = Get-GlossaryConfig
}

function Clean-OcrText([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $t = $text.Trim()

    # Normalize broken [Shift] tooltip prompt variations from partial OCR
    if ($t -match '(?i)^(?:Hold\s+|Old\s+|ld\s+)?\[?(?:Shift|Shif|Shi[''\u2019]?|hift|lift|t)\]?\s*for\s*more\s*det[a-zA-Z\u00C0-\u017F]*\.?$' -or
        $t -match '(?i)^Hold\s+\[(?:Shift|Shif|Shi)?.*for\s+m[a-z]*$') {
        return 'Hold [Shift] for more detail.'
    }

    # Fix split numbers inside Stall / Rush parentheses: Stall (1 5) -> Stall (15)
    $t = [regex]::Replace($t, '(?i)\b(Stall|Rush)\s*\(\s*(\d)\s+(\d)\s*\)', '$1 ($2$3)')

    # Fix OCR reading '10' as 'IO' before scaling icons or stats
    $t = [regex]::Replace($t, '\bIO\s*(?=\(?[A-Z]\)|\b(?:Crit|Attack|Defense|Magic|Mana|Shards?|Shields?|HP)\b)', '10 ')

    # Strip in-game stat scaling icon gibberish right after numbers:
    # e.g. "10 (A) Magic" -> "10 Magic", "400 (S * ) Shields" -> "400 Shields", "11 ( -Y) health" -> "11 health"
    $t = [regex]::Replace($t, '(?<=\d)\s*\(\s*[\*\-\+A-Z0-9\s]{0,5}\s*\)(?=\s*[A-Za-z])', '')
    # Unclosed or half-closed icon parenthesis after a number: "43 ( damage" -> "43 damage", "2 A) HP/S" -> "2 HP/s"
    $t = [regex]::Replace($t, '(?<=\d)\s+\(\s+(?=[A-Za-z])', ' ')
    $t = [regex]::Replace($t, '(?<=\d)\s+[A-Z]\)\s+(?=[A-Za-z])', ' ')

    # Common OCR character misreads in game fonts
    $t = [regex]::Replace($t, '(?:^|(?<=\s))\\?bility\b', 'Ability')
    $t = [regex]::Replace($t, '\bqain\b', 'gain')
    $t = [regex]::Replace($t, '\bJain\b', 'Gain')
    $t = [regex]::Replace($t, '\bvoison\b', 'poison')
    $t = [regex]::Replace($t, '\bIVI\s+combat\b', '1v1 combat')
    $t = [regex]::Replace($t, '\b[0-9A-Za-z]*2feat\s+the\s+creature\b', 'Defeat the creature')
    $t = [regex]::Replace($t, '\bgain\s+I\s+(Shard|Mana|Crit)\b', 'gain 1 $1')
    $t = [regex]::Replace($t, '\bwinning\s+I\s+combat\b', 'winning 1 combat')
    $t = [regex]::Replace($t, '\bhits\s+I\s+second\b', 'hits 1 second')
    $t = [regex]::Replace($t, '[\u2022\u00B7]\s*\+', '+')
    $t = [regex]::Replace($t, '\bHP/S\b', 'HP/s')

    # Separate leading Rarity / Ability type headers merged on the same line
    $t = [regex]::Replace($t, '^(Common|Rare|Epic|Legendary|Unique)\s+(Relic|Item)\s+(?=[A-Z0-9\+])', '$1 $2: ')
    $t = [regex]::Replace($t, '^(THE\s+[A-Z\s]+?)\s+(Passive Ability|Active Ability)\b', '$1 ($2)')
    $t = [regex]::Replace($t, '^(Passive Ability|Active Ability)\s+(?=[A-Z])', '$1: ')

    # Separate ALL-CAPS event action prefix from sentence body: "TAKE ITEM Get an item..." -> "[TAKE ITEM] Get an item..."
    $t = [regex]::Replace($t, '^([A-Z]{2,}(?:\s+[A-Z]{2,}){0,2})\s+(?=(?:Choose|Get|Gain|Lose|Defeat|Retrain|Rank|Recharge|Your|Heroes|Until)\b)', '[$1] ')

    # Collapse multiple spaces while preserving newlines
    $t = [regex]::Replace($t, '[ \t]{2,}', ' ')
    return $t.Trim()
}

function Test-ValidOcrBlock([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $false }
    $t = $text.Trim()
    # Filter obvious OCR symbol noise / garbled encodings
    if ($t -match '[\u00E6\u00F6\u00E5\u00C5\u00D8\u00F8\u00DE\u00FE\u00D0\u00F0\u2022@_\\\|~^]{2,}' -or $t -match '^[^a-zA-Z0-9\+\-\[\(]+$') { return $false }
    $letters = ([regex]::Matches($t, '[a-zA-Z]')).Count
    if ($letters -lt 2) { return $false }
    # Filter tiny 2-3 char broken OCR fragments unless they are known UI labels
    if ($t.Length -le 3) {
        $upper = $t.ToUpperInvariant()
        $allowedShort = @('ALL','ON','OFF','HP','MP','XP','PVP','PVE','FPS','MAX','WIN','BUY','NEW','YES','NO')
        if ($upper -notin $allowedShort) { return $false }
    }
    # Filter trailing broken word fragments like "ero.", "ndy.", "ece th", ")URATION"
    if ($t -match '^[a-z]{1,4}\.?$' -or $t -match '^[\)\]\}\.,:;]+[A-Za-z]*$') { return $false }
    return $true
}

function Get-ExactGlossaryTranslation([string]$text) {
    $null = Get-GlossaryConfig
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $norm = ($text -replace '\s+', ' ').Trim().ToUpperInvariant()
    if ($script:exactTable -and $script:exactTable.ContainsKey($norm)) {
        return $script:exactTable[$norm]
    }
    # Direct pattern for "35/90 Mana" or "50/105 HP"
    if ($text.Trim() -match '^(\d+\s*/\s*\d+)\s+Mana$') {
        return ($Matches[1] -replace '\s+', '') + ' 마나'
    }
    if ($text.Trim() -match '^(\d+\s*/\s*\d+)\s+HP$') {
        return ($Matches[1] -replace '\s+', '') + ' 체력'
    }
    return $null
}

function Get-GlossaryPromptHint([string]$text) {
    $cfg = Get-GlossaryConfig
    if (!$cfg -or !$cfg.terms -or [string]::IsNullOrWhiteSpace($text)) { return '' }
    $matched = @()
    foreach ($entry in $cfg.terms) {
        if ($text -match ('(?i)' + $entry.pattern)) {
            $matched += ($entry.term + '=' + $entry.ko)
        }
    }
    if ($matched.Count -eq 0) { return '' }
    return 'RPG Glossary: ' + ($matched -join ', ') + ".`n"
}

function Format-SourceForGoogle([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $text }
    $t = $text
    # Clarify "deal 250" -> "deal 250 damage" so Google doesn't translate 'deal' as business transaction ('거래')
    $t = [regex]::Replace($t, '(?i)\b(deal|deals|dealing)\s+(\d+(?:\.\d+)?[KkMm]?)(?!\s*(?:%|damage|magic|bonus|physical|true))', '$1 $2 damage')
    return $t
}

function Apply-GlossaryPost([string]$source, [string]$translated) {
    if ([string]::IsNullOrWhiteSpace($translated)) { return $translated }
    $out = $translated.Trim()
    # Strip accidental wrapping quotes if source was not quoted
    if ($source -notmatch '^["\u201C]' -and $out -match '^["\u201C](.+)["\u201D]$') {
        $out = $Matches[1].Trim()
    }
    $cfg = Get-GlossaryConfig
    if ($cfg -and $cfg.postReplace) {
        foreach ($rule in $cfg.postReplace) {
            $out = [regex]::Replace($out, $rule.pattern, $rule.replacement)
        }
    }
    # Strict inequality enforcement based on source text
    if ($source -match '(?i)\bbelow\s+\d' -and $source -notmatch '(?i)or\s+below') {
        $out = [regex]::Replace($out, '(\d+(?:\.\d+)?\s*(?:%|초|미터|m|개|점|체력|HP)?)\s*이하', '$1 미만')
    }
    if ($source -match '(?i)\babove\s+\d' -and $source -notmatch '(?i)or\s+above') {
        $out = [regex]::Replace($out, '(\d+(?:\.\d+)?\s*(?:%|초|미터|m|개|점|체력|HP)?)\s*이상', '$1 초과')
    }
    return $out.Trim()
}

function Get-ExtractCandidateTerms([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }
    $cfg = Get-GlossaryConfig
    $candidates = @()
    # Find Title Case multi-word proper nouns (e.g. "Sanctuary of Bloom", "Shield Repriser", "Resolute Strike", "Rift Storm")
    $m1 = [regex]::Matches($text, '\b([A-Z][a-z]{2,}(?:\s+(?:of|the|and|for|in|on)\s+[A-Z][a-z]{2,}|\s+[A-Z][a-z]{2,}){1,3})\b')
    foreach ($m in $m1) { $candidates += $m.Groups[1].Value.Trim() }
    # Find bracketed action / skill keywords (e.g. "[Channel Shards]", "[Take Armor]")
    $m2 = [regex]::Matches($text, '\[([A-Za-z][A-Za-z\s]{2,22})\]')
    foreach ($m in $m2) { $candidates += $m.Groups[1].Value.Trim() }

    $stopWords = @('After Stall','When Yuuna','Whenever Kai','Whenever Yuuna','Each Hero','This Effect','At Start','At Hero Rank','Choose One','Rare Relic','Unique Relic','Epic Relic','Legendary Relic','Common Relic','Unique Item','Passive Ability','Active Ability','Class Upgrade')
    $unknown = @()
    foreach ($c in ($candidates | Select-Object -Unique)) {
        if ($c -in $stopWords) { continue }
        $already = $false
        if ($cfg -and $cfg.terms) {
            foreach ($entry in $cfg.terms) {
                if ($c -match ('(?i)^' + $entry.pattern + '$') -or $entry.term -eq $c) { $already = $true; break }
            }
        }
        if (!$already) { $unknown += $c }
    }
    return $unknown
}

function Invoke-AutoLearnGlossary($blocks, [string]$backend) {
    try {
        $cfg = Get-GlossaryConfig
        if (!$cfg) { return 0 }
        $added = 0
        $llmPairs = @()

        foreach ($b in $blocks) {
            if (!$b.text -or !$b.translated -or $b.translated.StartsWith('[번역 실패]')) { continue }
            $src = ($b.text -replace '\s+', ' ').Trim()
            $trn = ($b.translated -replace '\s+', ' ').Trim()

            # 1. Auto-learn standalone ALL-CAPS UI headers/labels (e.g. "SHIELD BASTION", "BLADE STORM")
            if ($src -cmatch '^[A-Z]{2,}(?:\s+[A-Z''\-]{2,}){0,3}$' -and $src.Length -ge 4 -and $src.Length -le 28) {
                $upper = $src.ToUpperInvariant()
                if (!$script:exactTable.ContainsKey($upper) -and $trn.Length -ge 1 -and $trn.Length -le 20 -and $trn -match '[가-힣]') {
                    $cfg.exact | Add-Member -NotePropertyName $upper -NotePropertyValue $trn -Force
                    $script:exactTable[$upper] = $trn
                    $added++
                }
                continue
            }

            # 2. Collect sentences with unknown multi-word proper nouns for local LLM term extraction
            $unknownTerms = @(Get-ExtractCandidateTerms $src)
            if ($unknownTerms.Count -gt 0 -and $llmPairs.Count -lt 2) {
                $llmPairs += [pscustomobject]@{ source = $src; translated = $trn; candidates = $unknownTerms }
            }
        }

        # If local server is running and we have candidate proper nouns, extract exact Korean equivalents
        if ($llmPairs.Count -gt 0 -and $backend -in @('exaone','hymt','gemma')) {
            $keyFile = Join-Path $PSScriptRoot 'local-model.key'
            $headers = @{}
            if (Test-Path $keyFile) { $headers.Authorization = 'Bearer ' + ([IO.File]::ReadAllText($keyFile)).Trim() }
            foreach ($item in $llmPairs) {
                $candList = $item.candidates -join ', '
                $prompt = "From the English text and its Korean translation below, find the exact Korean noun phrase used for each term in [$candList]. Output ONLY lines in the format TERM=KOREAN (no quotes, no particles like 은/는/이/가/을/를).`nEnglish: $($item.source)`nKorean: $($item.translated)"
                try {
                    $respText = ''
                    if ($backend -eq 'gemma') {
                        $gPrompt = "<start_of_turn>user`n$prompt<end_of_turn>`n<start_of_turn>model`n"
                        $body = @{prompt=$gPrompt;stream=$false;n_predict=80;temperature=0;top_p=1;seed=42} | ConvertTo-Json -Compress
                        $r = Invoke-RestMethod -Uri 'http://127.0.0.1:18766/completion' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 6
                        $respText = $r.content
                    } else {
                        $modelAlias = if ($backend -eq 'exaone') { 'dalvit-exaone-7.8b' } else { 'dalvit-hymt2-1.8b' }
                        $body = @{model=$modelAlias;messages=@(@{role='user';content=$prompt});stream=$false;max_tokens=80;temperature=0;top_p=1;seed=42} | ConvertTo-Json -Depth 5 -Compress
                        $r = Invoke-RestMethod -Uri 'http://127.0.0.1:18766/v1/chat/completions' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 6
                        $respText = $r.choices[0].message.content
                    }
                    foreach ($line in ($respText -split "`r?`n")) {
                        if ($line -match '^\s*([A-Za-z][A-Za-z\s''\-]{2,30}?)\s*=\s*([가-힣A-Za-z0-9\s]{1,18})\s*$') {
                            $eng = $Matches[1].Trim()
                            $kor = $Matches[2].Trim()
                            # Strip trailing Korean particles if any slipped in
                            $kor = [regex]::Replace($kor, '(은|는|이|가|을|를|의|에|로|으로)$', '').Trim()
                            # Safety check: eng must be one of the candidates and kor must appear in the translation
                            if ($eng -in $item.candidates -and $kor.Length -ge 2 -and $kor -match '[가-힣]' -and $item.translated.Contains($kor)) {
                                $pat = '\b' + [regex]::Escape($eng) + '\b'
                                $cfg.terms += [pscustomobject]@{ pattern = $pat; term = $eng; ko = $kor }
                                $added++
                            }
                        }
                    }
                } catch {}
            }
        }

        if ($added -gt 0) {
            Save-GlossaryConfig $cfg
        }
        return $added
    } catch {
        return 0
    }
}

function Learn-GlossaryCorrection {
    param(
        [string]$Source,
        [string]$OldTranslation,
        [string]$NewTranslation,
        [string]$EngTerm = '',
        [string]$KorTerm = ''
    )
    $cfg = Get-GlossaryConfig
    $learnedNotes = @()

    # 1. If user explicitly supplied EngTerm -> KorTerm
    if (![string]::IsNullOrWhiteSpace($EngTerm) -and ![string]::IsNullOrWhiteSpace($KorTerm)) {
        $e = $EngTerm.Trim()
        $k = $KorTerm.Trim()
        if ($e -notmatch '\s' -and $e.Length -le 20 -and $e -cmatch '^[A-Z0-9\s]+$') {
            $cfg.exact | Add-Member -NotePropertyName $e.ToUpperInvariant() -NotePropertyValue $k -Force
        }
        $pat = '\b' + [regex]::Escape($e) + '\b'
        $updated = $false
        foreach ($entry in $cfg.terms) {
            if ($entry.term -ieq $e -or $entry.pattern -eq $pat) {
                $entry.ko = $k
                $updated = $true
                break
            }
        }
        if (!$updated) {
            $cfg.terms = @($cfg.terms) + @([pscustomobject]@{ pattern = $pat; term = $e; ko = $k })
        }
        $learnedNotes += "$e → $k"
    }

    # 2. If user edited the full translation of a block
    if (![string]::IsNullOrWhiteSpace($Source) -and ![string]::IsNullOrWhiteSpace($NewTranslation)) {
        $src = $Source.Trim()
        $old = if ($OldTranslation) { $OldTranslation.Trim() } else { '' }
        $new = $NewTranslation.Trim()

        # Update translation-cache.json across all backends/models for this source sentence
        $cachePath = Join-Path $PSScriptRoot 'translation-cache.json'
        $cache = @{}
        if (Test-Path -LiteralPath $cachePath) {
            try {
                $rawCache = Get-Content -LiteralPath $cachePath -Raw -Encoding UTF8
                $bIdx = $rawCache.IndexOf('{')
                if ($bIdx -gt 0) { $rawCache = $rawCache.Substring($bIdx) }
                ($rawCache | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $cache[$_.Name] = $_.Value }
            } catch {}
        }
        foreach ($pair in @(
            @('exaone','EXAONE-3.5-7.8B-Q4-v2'),
            @('gemma','TranslateGemma-4B-Q4-v2'),
            @('hymt','Hy-MT2-1.8B-Q4-v2'),
            @('google-official','GoogleCloud-v2'),
            @('google-free','GoogleFree-v2')
        )) {
            $cache[$pair[0] + '|' + $pair[1] + '|' + $src] = $new
        }
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($cachePath, ($cache | ConvertTo-Json -Depth 4), $utf8NoBom)

        # If source is a short phrase/header (<= 35 chars, single line), add directly to exact table
        if ($src.Length -le 35 -and $src -notmatch "`n") {
            $upper = ($src -replace '\s+', ' ').ToUpperInvariant()
            $cfg.exact | Add-Member -NotePropertyName $upper -NotePropertyValue $new -Force
            $learnedNotes += "$src → $new"
        }

        # Diff OldTranslation vs NewTranslation to find replaced Korean words/phrases
        if ($old -and $old -ne $new) {
            $oldWords = @($old -split '\s+')
            $newWords = @($new -split '\s+')
            $prefix = 0
            while ($prefix -lt $oldWords.Count -and $prefix -lt $newWords.Count -and $oldWords[$prefix] -eq $newWords[$prefix]) { $prefix++ }
            $oldSuffix = $oldWords.Count - 1
            $newSuffix = $newWords.Count - 1
            while ($oldSuffix -ge $prefix -and $newSuffix -ge $prefix -and $oldWords[$oldSuffix] -eq $newWords[$newSuffix]) {
                $oldSuffix--; $newSuffix--
            }
            if ($oldSuffix -ge $prefix -and $newSuffix -ge $prefix) {
                $diffOld = (($oldWords[$prefix..$oldSuffix]) -join ' ').Trim(' ', '.', ',', '!', '?')
                $diffNew = (($newWords[$prefix..$newSuffix]) -join ' ').Trim(' ', '.', ',', '!', '?')
                # Strip identical trailing Korean particle (e.g. "방패를" -> "보호막을" => "방패" -> "보호막")
                foreach ($josa in @('에서는','에게서','으로','에서','까지','부터','에게','처럼','만큼','보다','은','는','이','가','을','를','의','에','로','와','과','도','만')) {
                    if ($diffOld.EndsWith($josa) -and $diffNew.EndsWith($josa) -and $diffOld.Length -gt $josa.Length -and $diffNew.Length -gt $josa.Length) {
                        $diffOld = $diffOld.Substring(0, $diffOld.Length - $josa.Length)
                        $diffNew = $diffNew.Substring(0, $diffNew.Length - $josa.Length)
                        break
                    }
                }
                if ($diffOld.Length -ge 1 -and $diffOld.Length -le 15 -and $diffNew.Length -ge 1 -and $diffNew.Length -le 15 -and $diffOld -ne $diffNew) {
                    # Update any existing glossary term that used diffOld
                    $matchedTerm = $false
                    foreach ($entry in $cfg.terms) {
                        if ($src -match ('(?i)' + $entry.pattern) -and $entry.ko.Contains($diffOld)) {
                            $entry.ko = $diffNew
                            $matchedTerm = $true
                            $learnedNotes += "$($entry.term) → $diffNew"
                        }
                    }
                    # Add a postReplace rule so future occurrences of diffOld in similar contexts become diffNew
                    $pat = [regex]::Escape($diffOld)
                    $existsPost = $false
                    foreach ($pr in $cfg.postReplace) {
                        if ($pr.pattern -eq $pat) { $pr.replacement = $diffNew; $existsPost = $true; break }
                    }
                    if (!$existsPost) {
                        $cfg.postReplace = @($cfg.postReplace) + @([pscustomobject]@{ pattern = $pat; replacement = $diffNew })
                    }
                    if (!$matchedTerm) {
                        $learnedNotes += "$diffOld → $diffNew"
                    }
                }
            }
        }
    }

    Save-GlossaryConfig $cfg
    $totalCount = @($cfg.exact.PSObject.Properties).Count + @($cfg.terms).Count + @($cfg.postReplace).Count
    return [pscustomobject]@{
        learned = ($learnedNotes | Select-Object -Unique) -join ', '
        totalRules = $totalCount
    }
}
