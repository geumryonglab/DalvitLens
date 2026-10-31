$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot 'runtime\llama'
$exe=Join-Path $runtime 'llama-server.exe'
$model=Join-Path $PSScriptRoot 'models\Hy-MT2-1.8B-Q4_K_M.gguf'
$health='http://127.0.0.1:18766/health'
try {$r=Invoke-RestMethod $health -TimeoutSec 1;if($r.status -eq 'ok'){Write-Output 'Hy-MT2 already ready';return}}catch{}
$timer=[Diagnostics.Stopwatch]::StartNew()
$arguments=@('-m','models\Hy-MT2-1.8B-Q4_K_M.gguf','-ngl','99','-c','4096','-np','1','-b','512','-ub','256','-fa','on','--cache-ram','0','--host','127.0.0.1','--port','18766','--alias','dalvit-hymt2-1.8b','--no-webui')
if(Test-Path (Join-Path $PSScriptRoot 'local-model.key')){$arguments+=@('--api-key-file','local-model.key')}
$process=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $PSScriptRoot 'local-server.stdout.log') -RedirectStandardError (Join-Path $PSScriptRoot 'local-server.stderr.log')
$process.Id | Set-Content (Join-Path $PSScriptRoot 'local-server.pid')
while($timer.Elapsed.TotalSeconds -lt 60){
    if($process.HasExited){Get-Content (Join-Path $PSScriptRoot 'local-server.stderr.log') -Tail 20;throw 'Hy-MT2 server exited'}
    try {$r=Invoke-RestMethod $health -TimeoutSec 1;if($r.status -eq 'ok'){
        $timer.Stop();@{loadMs=[Math]::Round($timer.Elapsed.TotalMilliseconds);pid=$process.Id;context=4096;gpuLayers=99;model='Hy-MT2-1.8B-Q4_K_M'} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'model-startup-timing.json')
        Write-Output ('Hy-MT2 GPU ready in '+[Math]::Round($timer.Elapsed.TotalSeconds,2)+' seconds');return
    }}catch{}
    Start-Sleep -Milliseconds 200
}
throw 'Hy-MT2 warmup timed out; check local-server.stderr.log'
