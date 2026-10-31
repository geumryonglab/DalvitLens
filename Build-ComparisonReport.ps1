$ErrorActionPreference='Stop'
function Esc($text){[Net.WebUtility]::HtmlEncode([string]$text)}
$root=$PSScriptRoot
$cases=Get-Content (Join-Path $root 'comparison-cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$modes=@(@{id='base';title='기본 번역';prefix='comparison'},@{id='context';title='RPG 문맥 추가';prefix='comparison-context'})
$notes=@{
 'base-02-hymt'='below 30%를 30% 이하로 번역해 경계 조건이 달라졌습니다.';
 'base-02-gemma'='ally(아군)가 적으로 바뀌었습니다. below도 이하로 번역됐습니다.';
 'base-04-hymt'='시전당 발동을 공격당 발동으로 바꿨습니다.';
 'base-07-hymt'='above 80%(초과)를 80% 이상으로 번역했습니다.';
 'base-07-gemma'='above 80%(초과)를 80% 이상으로 번역했습니다.';
 'base-08-hymt'='최대 2명이라는 상한과 매 세 번째라는 반복 조건이 명확하지 않습니다.';
 'base-08-gemma'='매 세 번째 공격이라는 조건이 사라졌고, 최대가 적 수 대신 피해량에 붙었습니다.';
 'base-11-hymt'='Rooted를 RPG 상태명인 속박 대신 뿌리를 내린 것으로 직역했습니다.';
 'base-11-gemma'='이동 불가 의미는 전달하지만 상태명이 빠지고 설명이 반복됩니다.';
 'base-12-hymt'='도발·시전자라는 용어를 처리하지 못했습니다.';
 'base-12-gemma'='도발 대상이 시전자라는 정보가 빠졌습니다.';
 'base-15-hymt'='사망할 상황을 대신 막는 효과가 죽은 후 회복하는 효과처럼 바뀌었습니다.';
 'base-18-gemma'='시전자를 대상으로 지정할 수 없다는 뜻을 공격할 수 없다는 뜻으로 바꿨습니다.';
 'base-20-hymt'='2초 미만으로 줄일 수 없다는 뜻을 2초 이하로 줄일 수 없다고 번역했습니다.';
 'context-02-hymt'='RPG 문맥을 줘도 below가 이하로 바뀌었고 보호막 대신 방패를 사용했습니다.';
 'context-02-gemma'='기본 번역의 아군/적 오류는 개선됐지만 below는 여전히 이하입니다.';
 'context-04-hymt'='on-hit(적중 시)을 피격으로 바꿔 공격자와 피해자 관점이 달라졌습니다.';
 'context-07-hymt'='RPG 문맥을 줘도 초과를 이상으로 번역했습니다.';
 'context-07-gemma'='RPG 문맥을 줘도 초과를 이상으로 번역했습니다.';
 'context-08-gemma'='매 세 번째 공격이라는 조건이 여전히 빠졌습니다.';
 'context-10-gemma'='지속시간 갱신을 재사용 대기시간 초기화로 바꿨습니다.';
 'context-11-hymt'='RPG 문맥을 줘도 속박을 뿌리에 뿌리를 내린 적으로 번역했습니다.';
 'context-11-gemma'='RPG 문맥을 줘도 속박을 뿌리째진 적으로 번역했습니다.';
 'context-12-hymt'='caster(시전자)를 마법사로 한정해 대상의 의미가 달라질 수 있습니다.';
 'context-12-gemma'='도발 용어와 시전자를 공격해야 한다는 정보가 모두 불명확합니다.';
 'context-15-hymt'='사망 방지 조건이 사망 후 복구처럼 읽힙니다.';
 'context-20-hymt'='2초 미만이라는 경계 조건이 이하로 바뀌었습니다.'
}
$html=[Text.StringBuilder]::new()
$null=$html.Append(@'
<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>DalvitLens · 번역 모델 비교</title>
<style>:root{color-scheme:dark;--bg:#100e1b;--card:#161426;--purple:#B373FF;--sky:#5ABFFF;--yellow:#FFE600}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:#eee9f7;font:15px/1.75 'Malgun Gothic',sans-serif}main{max-width:1280px;margin:auto;padding:44px 28px}h1{font-size:34px;line-height:1.3;margin:10px 0 16px}h2{font-size:19px;margin:0}.brand{color:var(--purple);font-weight:700;font-size:12px;letter-spacing:2px}.sub,.meta{color:#b3abc4}.verdict{background:#211b32;border:1px solid #56416f;padding:22px 26px;border-radius:20px;margin:26px 0}.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:24px 0}.stat{background:var(--card);padding:20px;border-radius:18px}.stat b{display:block;color:var(--yellow);font-size:25px}.stat small{color:#b3abc4}.tools{display:flex;gap:12px;flex-wrap:wrap;margin:24px 0}button,input{font:inherit;border:1px solid #4c3d60;border-radius:24px;padding:9px 18px;background:#241d35;color:#eee9f7}button{cursor:pointer}button.active{background:var(--purple);color:#160c23;border-color:var(--purple);font-weight:700}input{flex:1;min-width:220px}article{background:var(--card);border:1px solid #302941;border-radius:22px;padding:26px;margin:20px 0}.source{white-space:pre-wrap;color:#d7d1e2;margin:14px 0}.check{font-size:12px;color:var(--sky);margin-bottom:20px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}.output{background:#1d1930;border-radius:16px;padding:20px;min-width:0}.output header{display:flex;justify-content:space-between;gap:10px;color:var(--purple);font-weight:700}.output.gemma header{color:var(--sky)}.translation{white-space:pre-wrap;overflow-wrap:anywhere;margin:14px 0}.note{border-top:1px solid #4a3b57;padding-top:12px;font-size:13px;color:#f2c9ad}.meta{font-size:12px;margin:6px 0}a{color:var(--sky)}[hidden]{display:none!important}footer{color:#b3abc4;font-size:13px;margin-top:32px}@media(max-width:800px){.stats{grid-template-columns:1fr 1fr}.pair{grid-template-columns:1fr}main{padding:24px 16px}h1{font-size:27px}article{padding:18px}}</style>
<main><div class="brand">DALVITLENS / MODEL COMPARISON</div><h1>빠른 번역과 자연스러운 번역,<br>실제로 얼마나 달라질까?</h1>
<p class="sub">RTX 4070 SUPER 12GB · Q4_K_M · 전체 GPU 오프로딩 · 컨텍스트 4096 · greedy decoding (temperature 0)<br>합성 게임 문장 20개 × 모델별 3회. 아래 시간은 번역 엔진만 측정했으며 OCR·화면 캡처·표시는 제외했습니다.</p>
<div class="verdict"><strong>검토 결과: TranslateGemma 4B 단독 교체를 우선 추천하기 어렵습니다.</strong><br>일부 표현은 더 매끄럽지만, 대상·반복 횟수·조건을 바꾸는 오류가 있습니다. 일반적인 RPG 문맥을 추가해도 전반적인 해결책이 되지는 않았습니다. 숫자 보존 검사만으로는 이런 오류를 잡을 수 없습니다.</div>
<div class="tools"><button class="active" data-mode="base">기본 번역</button><button data-mode="context">RPG 문맥 추가</button><input id="search" placeholder="용어·원문·번역 내용 검색" aria-label="비교 문장 검색"></div>
'@)
foreach($mode in $modes){
    $results=Get-Content (Join-Path $root ($mode.prefix+'-results.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    $summary=Get-Content (Join-Path $root ($mode.prefix+'-summary.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    $resource=Get-Content (Join-Path $root ($mode.prefix+'-resources.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    $h=$summary | Where-Object model -eq 'hymt';$g=$summary | Where-Object model -eq 'gemma'
    $hr=$resource | Where-Object model -eq 'hymt';$gr=$resource | Where-Object model -eq 'gemma'
    $hide=if($mode.id -eq 'base'){''}else{' hidden'}
    $null=$html.Append('<section class="view" id="'+$mode.id+'"'+$hide+'><div class="stats">')
    foreach($item in @(@{label='Hy-MT2 중앙값';value=('{0:N3}초' -f ($h.medianMs/1000));detail=('90백분위 '+$h.p90Ms+' ms')},@{label='TranslateGemma 중앙값';value=('{0:N3}초' -f ($g.medianMs/1000));detail=('90백분위 '+$g.p90Ms+' ms')},@{label='Hy-MT2 VRAM';value=('{0:N2} GiB' -f ($hr.vramMB/1024));detail=('프로세스 RAM '+$hr.ramMB+' MiB')},@{label='TranslateGemma VRAM';value=('{0:N2} GiB' -f ($gr.vramMB/1024));detail=('프로세스 RAM '+$gr.ramMB+' MiB')})){$null=$html.Append('<div class="stat">'+(Esc $item.label)+'<b>'+(Esc $item.value)+'</b><small>'+(Esc $item.detail)+'</small></div>')}
    $null=$html.Append('</div><p class="meta">'+(Esc $mode.title)+' · 각 모델 60회 측정. 문장별로 표시한 번역은 1회차 결과이며, 문장별 시간은 3회 측정의 중앙값입니다.</p>')
    foreach($case in $cases){
        $null=$html.Append('<article><h2>'+(Esc ($case.id+' · '+$case.topic))+'</h2><p class="source">'+(Esc $case.source)+'</p><div class="check">의미 확인 기준: '+(Esc $case.check)+'</div><div class="pair">')
        foreach($model in @('hymt','gemma')){
            $rows=@($results | Where-Object {$_.id -eq $case.id -and $_.model -eq $model});$first=$rows | Where-Object round -eq 1 | Select-Object -First 1
            $label=if($model -eq 'hymt'){'Hy-MT2 1.8B'}else{'TranslateGemma 4B'}
            $ms=@($rows.ms | Sort-Object)[1]
            $null=$html.Append('<div class="output '+$model+'"><header><span>'+ $label+'</span><span>'+ $ms+' ms</span></header><div class="translation">'+(Esc $first.translation)+'</div>')
            $note=$notes[$mode.id+'-'+$case.id+'-'+$model]
            if($note){$null=$html.Append('<div class="note">검토: '+(Esc $note)+'</div>')}
            $null=$html.Append('</div>')
        }
        $null=$html.Append('</div></article>')
    }
    $null=$html.Append('</section>')
}
$null=$html.Append(@'
<footer>이 문장들은 비교를 위해 작성한 합성 문장으로 Guildrun의 공식 문장이 아닙니다. 검토 메모는 이 문장들에 대한 수동 의미 검토이며, 일반적인 모델 순위를 뜻하지 않습니다. 번역문은 후처리하지 않았습니다.<br>기본 비교: 각 모델의 번역 지시·템플릿 사용. 추가 비교: 두 모델에 동일한 일반 RPG 문맥과 의미 보존 지시 추가. 용어 사전이나 정답 번역문은 제공하지 않았습니다.<br>TranslateGemma는 Google 원본을 변환한 mradermacher의 Q4_K_M GGUF이며, 이미지 인코더는 사용하지 않았습니다. 기본 번역기 설정은 변경하지 않았고 비교용 프로세스는 종료했습니다.<br><a href="https://huggingface.co/google/translategemma-4b-it">Google 모델 설명</a> · <a href="https://huggingface.co/mradermacher/translategemma-4b-it-GGUF">GGUF 출처</a> · <a href="https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF">Hy-MT2 출처</a></footer></main>
<script>let mode='base';const search=document.querySelector('#search');function update(){document.querySelectorAll('.view').forEach(v=>v.hidden=v.id!==mode);document.querySelectorAll('article').forEach(a=>a.hidden=!a.textContent.toLowerCase().includes(search.value.toLowerCase()));document.querySelectorAll('[data-mode]').forEach(b=>b.classList.toggle('active',b.dataset.mode===mode))}document.querySelectorAll('[data-mode]').forEach(b=>b.onclick=()=>{mode=b.dataset.mode;update()});search.oninput=update;</script></html>
'@)
[IO.File]::WriteAllText((Join-Path $root 'model-comparison.html'),$html.ToString(),[Text.UTF8Encoding]::new($false))
Write-Output 'Saved model-comparison.html'
