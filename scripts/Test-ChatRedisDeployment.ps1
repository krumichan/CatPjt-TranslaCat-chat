param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$runId = [Guid]::NewGuid().ToString('N')
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$output = Join-Path $resultsRoot "V2\RedisAcl\$runId"
New-Item -ItemType Directory -Path $output | Out-Null
$manifestPath = Join-Path $output 'manifest.json'
$passwordPath = Join-Path $output 'redis-password'
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
[IO.File]::WriteAllText($passwordPath, $password, [Text.Encoding]::ASCII)
$password = $null

# 고정 loopback 포트를 선택하여 컨테이너 재시작 때 endpoint가 바뀌지 않게 한다.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$runtime = [ordered]@{
    testMode = $true
    owner = 'chat-redis-acl-validation'
    runId = $runId
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    keyPrefix = "translacat:chat:test:$runId"
    port = $port
    passwordFile = $passwordPath
    container = $null
    status = 'CREATING'
}
$runtime | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
$container = $null
$probeExit = 1

try {
    # 배포용 스크립트/설정을 그대로 읽기 전용 mount한다. 일반 Compose 서비스는 시작하지 않는다.
    $deploy = Join-Path $workspace 'deploy\chat'
    $runArguments = @('run','--detach','--name',"chat-acl-probe-$runId",'--label',"translacat.chat.run=$runId",'--label','translacat.chat.owner=chat-redis-acl-validation',
        '--user','redis','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true','--memory','384m','--pids-limit','80',
        '--publish',"127.0.0.1:${port}:6379",'--tmpfs','/run/chat:rw,noexec,nosuid,size=1m,mode=1777','--tmpfs','/data:rw,noexec,nosuid,size=1m,mode=1777',
        '--env',"CHAT_REDIS_NAMESPACE=$($runtime.keyPrefix)",'--mount',"type=bind,source=$passwordPath,target=/run/secrets/chat_redis_password,readonly",
        '--mount',"type=bind,source=$deploy,target=/chat-config,readonly",'--entrypoint','/bin/sh',
        'redis:7.4.10-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2','/chat-config/start-chat-redis.sh')
    $container = & docker @runArguments
    if ($LASTEXITCODE -ne 0 -or $container -notmatch '^[a-f0-9]{64}$') { throw 'Owned Redis ACL container creation failed.' }
    $runtime.container = $container
    $runtime.status = 'STARTED'
    $runtime | ConvertTo-Json | Set-Content -LiteralPath $manifestPath

    # 익명 명령이 거부되고 같은 secret을 사용하는 healthcheck가 통과하는지 실제 서버에서 확인한다.
    $healthy = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        & docker exec $container /bin/sh /chat-config/check-chat-redis.sh 2>$null
        if ($LASTEXITCODE -eq 0) { $healthy = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $healthy) { throw 'Authenticated Redis healthcheck did not become ready.' }
    $anonymous = & docker exec $container redis-cli --raw PING
    if ($anonymous -notmatch '^NOAUTH') { throw 'Redis accepted an anonymous command.' }

    # 검증 프로젝트 산출물은 기존 CHAT solution과 분리한다. 비밀은 manifest가 가리키는 파일에서만 읽는다.
    $probeProject = Join-Path $deploy 'RedisAclProbe\RedisAclProbe.csproj'
    & 'C:\Program Files\dotnet\dotnet.exe' run --project $probeProject --configuration Release -- $manifestPath
    $probeExit = $LASTEXITCODE
    if ($probeExit -ne 0) { throw 'Actual StackExchange.Redis ACL checks failed.' }
    $runtime.status = 'VERIFIED'
} finally {
    # 이번 실행이 만든 정확한 ID와 두 label이 모두 일치할 때만 일회용 컨테이너를 정리한다.
    if ($container -match '^[a-f0-9]{64}$') {
        $actualRun = & docker inspect --format '{{index .Config.Labels "translacat.chat.run"}}' $container
        $runCheckExit = $LASTEXITCODE
        $actualOwner = & docker inspect --format '{{index .Config.Labels "translacat.chat.owner"}}' $container
        if ($runCheckExit -ne 0 -or $LASTEXITCODE -ne 0 -or $actualRun -ne $runId -or $actualOwner -ne 'chat-redis-acl-validation') {
            throw 'Redis ACL fixture cleanup ownership verification failed.'
        }
        & docker stop --time 2 $container | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned Redis ACL fixture stop failed.' }
        & docker rm $container | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned Redis ACL fixture removal failed.' }
        $runtime.status = if ($probeExit -eq 0) { 'VERIFIED_CLEANED' } else { 'FAILED_CLEANED' }
    }
    $runtime.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $runtime | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
    Write-Output "Redis ACL validation manifest: $manifestPath"
}
exit $probeExit
