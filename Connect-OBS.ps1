param([string]$Scene='게임화면', [switch]$Inspect)
$ErrorActionPreference='Stop'
$configPath=Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-websocket\config.json'
$config=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$config.server_enabled) { throw 'OBS WebSocket 서버가 꺼져 있습니다. OBS 도구 메뉴에서 서버를 켜주세요.' }
$socket=[Net.WebSockets.ClientWebSocket]::new()
$cancel=[Threading.CancellationTokenSource]::new(10000)
function Receive-Message {
    $buffer=[byte[]]::new(65536); $data=[IO.MemoryStream]::new()
    do {
        $seg=[ArraySegment[byte]]::new($buffer)
        $received=$socket.ReceiveAsync($seg,$cancel.Token).GetAwaiter().GetResult()
        if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'OBS 연결이 닫혔습니다.' }
        $data.Write($buffer,0,$received.Count)
    } while (!$received.EndOfMessage)
    $message=[Text.Encoding]::UTF8.GetString($data.ToArray()) | ConvertFrom-Json
    $data.Dispose(); return $message
}
function Send-Message($message) {
    $bytes=[Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Depth 16 -Compress))
    $null=$socket.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,$cancel.Token).GetAwaiter().GetResult()
}
function Hash64([string]$value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))) } finally { $sha.Dispose() }
}
function Request([string]$type,$requestData=@{}) {
    $id=[Guid]::NewGuid().ToString('N')
    Send-Message @{op=6;d=@{requestType=$type;requestId=$id;requestData=$requestData}}
    do {$response=Receive-Message} while ($response.op -ne 7 -or $response.d.requestId -ne $id)
    if (!$response.d.requestStatus.result) { throw ($type+': '+$response.d.requestStatus.comment) }
    return $response.d.responseData
}
try {
    $null=$socket.ConnectAsync([Uri]('ws://127.0.0.1:'+$config.server_port),$cancel.Token).GetAwaiter().GetResult()
    $hello=Receive-Message
    $identify=@{rpcVersion=1;eventSubscriptions=0}
    if ($hello.d.authentication) {
        $secret=Hash64 ($config.server_password+$hello.d.authentication.salt)
        $identify.authentication=Hash64 ($secret+$hello.d.authentication.challenge)
    }
    Send-Message @{op=1;d=$identify}
    $identified=Receive-Message
    if ($identified.op -ne 2) { throw 'OBS 인증에 실패했습니다.' }
    $video=Request 'GetVideoSettings'
    $current=Request 'GetCurrentProgramScene'
    Write-Output ('OBS canvas: '+$video.baseWidth+'x'+$video.baseHeight+'; output: '+$video.outputWidth+'x'+$video.outputHeight+'; current scene: '+$current.currentProgramSceneName)
    $items=Request 'GetSceneItemList' @{sceneName=$Scene}
    $game=$items.sceneItems | Where-Object { $_.sourceName -eq '게임기본_게임' } | Select-Object -First 1
    if ($game) { $t=$game.sceneItemTransform; Write-Output ('Game transform: x='+$t.positionX+', y='+$t.positionY+', scale='+$t.scaleX+','+$t.scaleY+', crop='+$t.cropLeft+','+$t.cropTop+','+$t.cropRight+','+$t.cropBottom) }
    if ($Inspect) { return }
    if ($video.baseWidth -ne 1920 -or $video.baseHeight -ne 1080) { throw '캔버스가 1920x1080이 아닙니다. 자동 소스 추가를 중단합니다.' }
    $name='DalvitLens 번역'
    $inputs=Request 'GetInputList'
    if ($inputs.inputs.inputName -contains 'GuildLens 번역' -and $inputs.inputs.inputName -notcontains $name) {
        $null=Request 'SetInputName' @{inputName='GuildLens 번역';newInputName=$name}
        $inputs=Request 'GetInputList'
        $items=Request 'GetSceneItemList' @{sceneName=$Scene}
    }
    if ($inputs.inputs.inputName -contains $name) {
        $existing=$inputs.inputs | Where-Object { $_.inputName -eq $name } | Select-Object -First 1
        if ($existing.inputKind -ne 'browser_source') { throw '같은 이름의 다른 종류 소스가 있습니다.' }
        $null=Request 'SetInputSettings' @{inputName=$name;inputSettings=@{url='http://127.0.0.1:18765/';width=1920;height=1080;is_local_file=$false;shutdown=$false;restart_when_active=$true};overlay=$true}
        $item=$items.sceneItems | Where-Object { $_.sourceName -eq $name } | Select-Object -First 1
        if ($item) { $itemId=$item.sceneItemId }
        else { $created=Request 'CreateSceneItem' @{sceneName=$Scene;sourceName=$name;sceneItemEnabled=$true};$itemId=$created.sceneItemId }
    } else {
        $created=Request 'CreateInput' @{sceneName=$Scene;inputName=$name;inputKind='browser_source';inputSettings=@{url='http://127.0.0.1:18765/';width=1920;height=1080;is_local_file=$false;shutdown=$false;restart_when_active=$true};sceneItemEnabled=$true}
        $itemId=$created.sceneItemId
    }
    $null=Request 'SetSceneItemTransform' @{sceneName=$Scene;sceneItemId=$itemId;sceneItemTransform=@{positionX=0.0;positionY=0.0;scaleX=1.0;scaleY=1.0;rotation=0.0;alignment=5;boundsType='OBS_BOUNDS_NONE';cropLeft=0;cropRight=0;cropTop=0;cropBottom=0}}
    $null=Request 'SetSceneItemEnabled' @{sceneName=$Scene;sceneItemId=$itemId;sceneItemEnabled=$true}
    # Put directly above game capture, leaving existing camera and alerts above it.
    $items=Request 'GetSceneItemList' @{sceneName=$Scene}
    $game=$items.sceneItems | Where-Object { $_.sourceName -eq '게임기본_게임' } | Select-Object -First 1
    if ($game) {
        $overlayItem=$items.sceneItems | Where-Object { $_.sceneItemId -eq $itemId } | Select-Object -First 1
        $index=if ($overlayItem.sceneItemIndex -lt $game.sceneItemIndex) {$game.sceneItemIndex} else {$game.sceneItemIndex+1}
        $null=Request 'SetSceneItemIndex' @{sceneName=$Scene;sceneItemId=$itemId;sceneItemIndex=$index}
    }
    $null=Request 'SetSceneItemLocked' @{sceneName=$Scene;sceneItemId=$itemId;sceneItemLocked=$true}
    $check=Request 'GetInputSettings' @{inputName=$name}
    Write-Output ('Connected: '+$Scene+' / '+$name+' / '+$check.inputSettings.width+'x'+$check.inputSettings.height+' / '+$check.inputSettings.url)
} finally { $socket.Dispose(); $cancel.Dispose() }
