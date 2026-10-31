param([switch]$GameContext)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$cases=Get-Content (Join-Path $root 'comparison-cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$keyPath=Join-Path $root 'local-model.key'
if(!(Test-Path $keyPath)){throw 'Local communication key not found'}
$headers=@{Authorization='Bearer '+([IO.File]::ReadAllText($keyPath)).Trim()}
$endpoint='http://127.0.0.1:18768'
$all=@();$resources=@()
$prefix=if($GameContext){'comparison-context'}else{'comparison'}
$context=if($GameContext){"This text is from a fantasy role-playing game. Use natural, concise Korean game UI and tooltip wording with standard RPG terminology. Preserve the effect recipient, trigger conditions, strict inequalities, counts and exclusions. Do not add information.`n"}else{''}
function Call-Translation($case,[string]$model,[int]$round){
    if($model -eq 'hymt'){
        $body=@{model='comparison-hymt';messages=@(@{role='user';content=$context+"Translate the following text into Korean. Note that you should only output the translated result without any additional explanation. Preserve numbers, percentages and conditions.`n`n"+$case.source});stream=$false;max_tokens=512;temperature=0;top_p=1;repeat_penalty=1.05;seed=42;cache_prompt=$true}
        $route='/v1/chat/completions'
    }else{
        # Exact English->Korean text prompt emitted by the publisher's chat template.
        $prompt="<start_of_turn>user`nYou are a professional English (en) to Korean (ko) translator. Your goal is to accurately convey the meaning and nuances of the original English text while adhering to Korean grammar, vocabulary, and cultural sensitivities.`nProduce only the Korean translation, without any additional explanations or commentary. Please translate the following English text into Korean:`n`n`n"+$case.source.Trim()+"<end_of_turn>`n<start_of_turn>model`n"
        if($GameContext){$prompt=$prompt.Replace("`nProduce only",("`n"+$context+'Produce only'))}
        $body=@{prompt=$prompt;stream=$false;n_predict=512;temperature=0;top_p=1;repeat_penalty=1.05;seed=42;cache_prompt=$true}
        $route='/completion'
    }
    $json=$body | ConvertTo-Json -Depth 8 -Compress
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $r=Invoke-RestMethod ($endpoint+$route) -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 60
    $timer.Stop()
    $text=if($model -eq 'hymt'){$r.choices[0].message.content}else{$r.content}
    $finished=if($model -eq 'hymt'){$r.choices[0].finish_reason}else{if($r.stopped_limit){'length'}else{'stop'}}
    $missing=@([regex]::Matches($case.source,'\d+(?:\.\d+)?') | ForEach-Object {$_.Value} | Where-Object {$text -notmatch ('(?<!\d)'+[regex]::Escape($_)+'(?!\d)')})
    [pscustomobject]@{model=$model;round=$round;id=$case.id;topic=$case.topic;source=$case.source;check=$case.check;translation=$text.Trim();ms=[math]::Round($timer.Elapsed.TotalMilliseconds,1);finishReason=$finished;missingNumbers=$missing;tokens=$r.tokens_predicted;timings=$r.timings;usage=$r.usage}
}
foreach($model in @('hymt','gemma')){
    $proc=$null
    try{
        $filename=if($model -eq 'hymt'){'Hy-MT2-1.8B-Q4_K_M.gguf'}else{'TranslateGemma-4B-Q4_K_M.gguf'}
        $alias=if($model -eq 'hymt'){'comparison-hymt'}else{'comparison-gemma'}
        $args=@('-m',('models\'+$filename),'-ngl','99','-c','4096','-np','1','-b','512','-ub','256','-fa','on','--cache-ram','0','--host','127.0.0.1','--port','18768','--alias',$alias,'--no-webui','--api-key-file','local-model.key')
        $load=[Diagnostics.Stopwatch]::StartNew()
        Write-Output ('Starting comparison model: '+$model)
        $proc=Start-Process -FilePath (Join-Path $root 'runtime\llama\llama-server.exe') -ArgumentList $args -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root ('compare-'+$model+'.stdout.log')) -RedirectStandardError (Join-Path $root ('compare-'+$model+'.stderr.log'))
        $ready=$false
        while($load.Elapsed.TotalSeconds -lt 60){
            if($proc.HasExited){Get-Content (Join-Path $root ('compare-'+$model+'.stderr.log')) -Tail 18;throw ('Model exited: '+$model)}
            try{$health=Invoke-RestMethod ($endpoint+'/health') -TimeoutSec 1;if($health.status -eq 'ok'){$ready=$true;break}}catch{}
            Start-Sleep -Milliseconds 100
        }
        $load.Stop();if(!$ready){throw 'Model load timed out'}
        $warmup=Call-Translation $cases[0] $model 0
        for($round=1;$round -le 3;$round++){
            foreach($case in $cases){$all+=Call-Translation $case $model $round}
            $all | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $root ($prefix+'-results.json')) -Encoding UTF8
            Write-Output ('Finished '+$model+' round '+$round+' / 3')
        }
        $proc.Refresh()
        $gpu=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory | Where-Object {$_.Name -like ('pid_'+$proc.Id+'_*')}
        $resources+=[pscustomobject]@{model=$model;loadMs=[math]::Round($load.Elapsed.TotalMilliseconds,1);firstRequestMs=$warmup.ms;ramMB=[math]::Round($proc.WorkingSet64/1MB);privateMB=[math]::Round($proc.PrivateMemorySize64/1MB);vramMB=[math]::Round(($gpu | Measure-Object DedicatedUsage -Sum).Sum/1MB);sharedGpuMB=[math]::Round(($gpu | Measure-Object SharedUsage -Sum).Sum/1MB)}
        $resources | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root ($prefix+'-resources.json')) -Encoding UTF8
    }finally{
        if($proc){try{if(!$proc.HasExited){$proc.Kill();$null=$proc.WaitForExit(3000)}}catch{}$proc.Dispose()}
    }
}
$summary=foreach($group in ($all | Group-Object model)){
    $times=@($group.Group.ms | Sort-Object)
    [pscustomobject]@{model=$group.Name;requests=$times.Count;medianMs=($times[29]+$times[30])/2;p90Ms=$times[[math]::Ceiling($times.Count*.9)-1];minMs=$times[0];maxMs=$times[-1];missingNumberRequests=@($group.Group | Where-Object {$_.missingNumbers.Count -gt 0}).Count;truncatedRequests=@($group.Group | Where-Object {$_.finishReason -eq 'length'}).Count}
}
$summary | ConvertTo-Json | Set-Content (Join-Path $root ($prefix+'-summary.json')) -Encoding UTF8
$summary | Format-Table -AutoSize
$resources | Format-Table -AutoSize
Write-Output 'COMPARISON COMPLETE; temporary comparison models stopped.'
