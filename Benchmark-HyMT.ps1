$ErrorActionPreference='Stop'
$samples=@(
    @{name='short';text='Gain 20% attack speed for 4 seconds.'},
    @{name='tooltip';text='Flame Strike. Deal 120 magic damage to all enemies within 3 meters. Burning enemies take 25% additional damage for 6 seconds. This effect stacks up to 3 times.'},
    @{name='conditional';text='When an ally falls below 30% health, grant them a shield equal to 15% of your maximum health for 8 seconds. Cooldown: 12 seconds. Does not stack.'}
)
$rows=@()
$headers=@{}
$keyPath=Join-Path $PSScriptRoot 'local-model.key'
if(Test-Path $keyPath){$headers.Authorization='Bearer '+([IO.File]::ReadAllText($keyPath)).Trim()}
function Translate-One($sample,[string]$phase){
    $body=@{model='dalvit-hymt2-1.8b';messages=@(@{role='user';content="Translate the following text into Korean. Note that you should only output the translated result without any additional explanation. Preserve numbers, percentages and conditions.`n`n"+$sample.text});stream=$false;max_tokens=512;temperature=0.7;top_p=0.6;top_k=20;repeat_penalty=1.05;seed=42;cache_prompt=$true} | ConvertTo-Json -Depth 6 -Compress
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $r=Invoke-RestMethod -Uri 'http://127.0.0.1:18766/v1/chat/completions' -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
    $timer.Stop()
    $translation=$r.choices[0].message.content
    $numbers=@([regex]::Matches($sample.text,'\d+(?:\.\d+)?') | ForEach-Object {$_.Value})
    $missing=@($numbers | Where-Object {$translation -notmatch ('(?<!\d)'+[regex]::Escape($_)+'(?!\d)')})
    [pscustomobject]@{phase=$phase;sample=$sample.name;ms=[Math]::Round($timer.Elapsed.TotalMilliseconds,1);outputTokens=$r.usage.completion_tokens;finishReason=$r.choices[0].finish_reason;missingNumbers=$missing;source=$sample.text;translation=$translation;serverTimings=$r.timings}
}
$rows+=Translate-One $samples[0] 'first-request'
for($run=1;$run -le 3;$run++){foreach($sample in $samples){$rows+=Translate-One $sample ('warm-'+$run)}}
$rows | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $PSScriptRoot 'benchmark-hymt-results.json') -Encoding UTF8
$rows | Select-Object phase,sample,ms,outputTokens,finishReason,@{n='missingNumbers';e={$_.missingNumbers -join ','}} | Format-Table -AutoSize
$rows | Where-Object {$_.phase -eq 'warm-1'} | Select-Object sample,translation | Format-List
