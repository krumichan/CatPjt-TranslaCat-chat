param(
    [Parameter(Mandatory)][ValidatePattern('^translacat-chat:[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$')][string]$Image,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$ProbeHost = 'chat.invalid'
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$deploy = Join-Path $workspace 'deploy\chat'
$runId = [Guid]::NewGuid().ToString('N')
$project = "chat-startup-$runId"
$owner = 'chat-deployment-startup-validation'
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$output = Join-Path $resultsRoot "V2\DeploymentSmoke\$runId"
New-Item -ItemType Directory -Path $output | Out-Null
$manifestPath = Join-Path $output 'manifest.json'
$environmentPath = Join-Path $output 'compose.env'
$overlayPath = Join-Path $output 'test-overlay.json'

# 이미 root가 최종 소스로 빌드한 로컬 image만 사용한다. 이 스크립트는 build/pull/운영 배포를 하지 않는다.
$imageId = & docker image inspect --format '{{.Id}}' $Image
if ($LASTEXITCODE -ne 0 -or $imageId -notmatch '^sha256:[a-f0-9]{64}$') { throw 'The requested local CHAT image is not available.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()

# 비밀은 이번 ignored 합성 fixture 파일에만 쓰며 명령행/manifest/응답에는 값이 들어가지 않는다.
$redisSecretPath = Join-Path $output 'redis-password'
$jwtSecretPath = Join-Path $output 'jwt-signing-key'
$databaseSecretPath = Join-Path $output 'database-connection'
[IO.File]::WriteAllText($redisSecretPath,
    [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant(), [Text.Encoding]::ASCII)
Copy-Item -LiteralPath $redisSecretPath -Destination "${redisSecretPath}_api"
[IO.File]::WriteAllText($jwtSecretPath,
    [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)), [Text.Encoding]::ASCII)
# 실제 DB adapter를 유지하되 API container 자체의 닫힌 loopback port만 가리킨다. BE/LL/MySQL 서버는 대상이 아니다.
[IO.File]::WriteAllText($databaseSecretPath,
    'Server=127.0.0.1;Port=1;Database=translacat_chat;User ID=synthetic_unavailable;Password=synthetic_only;Connection Timeout=1;Default Command Timeout=1;Pooling=False;SslMode=Disabled', [Text.Encoding]::ASCII)
$environment = @(
    'DOTNET_ENVIRONMENT=Production',
    'ASPNETCORE_ENVIRONMENT=Production',
    "CHAT_API_PORT=$port",
    "CHAT_ALLOWED_HOSTS=$ProbeHost",
    "CHAT_REDIS_NAMESPACE=translacat:chat:test:$runId",
    'CHAT_SOURCE_TIME_ZONE=Etc/UTC',
    "CHAT_REDIS_PASSWORD_FILE=$($redisSecretPath.Replace('\','/'))",
    "CHAT_DATABASE_CONNECTION_FILE=$($databaseSecretPath.Replace('\','/'))",
    "CHAT_JWT_SIGNING_KEY_FILE=$($jwtSecretPath.Replace('\','/'))"
)
[IO.File]::WriteAllLines($environmentPath, $environment, [Text.Encoding]::ASCII)
$labels = @{ 'translacat.chat.run' = $runId; 'translacat.chat.owner' = $owner }
@{
    services = @{
        'chat-api' = @{ image = $Image; labels = $labels }
        'chat-redis' = @{ labels = $labels }
    }
    networks = @{
        'chat-private' = @{ labels = $labels }
        'chat-egress' = @{ labels = $labels }
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $overlayPath -Encoding utf8
$runtime = [ordered]@{
    testMode = $true; owner = $owner; runId = $runId; project = $project
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o'); image = $Image; imageId = $imageId
    port = $port; databaseMode = 'OWN_CONTAINER_LOOPBACK_PORT_1_UNAVAILABLE'
    scope = 'PRODUCTION_COMPOSE_STARTUP_FAIL_CLOSED_ONLY'; containers = @(); networks = @(); status = 'PREPARED'
}

function Save-Manifest {
    $runtime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

function Find-OwnedResources {
    # prefix 이름을 삭제 근거로 삼지 않고 이번 run/owner/project의 정확한 label 교집합만 기록한다.
    $containers = @(& docker ps --all --no-trunc --filter "label=translacat.chat.run=$runId" --filter "label=translacat.chat.owner=$owner" --filter "label=com.docker.compose.project=$project" --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0) { throw 'Owned container inventory failed.' }
    $networks = @(& docker network ls --no-trunc --filter "label=translacat.chat.run=$runId" --filter "label=translacat.chat.owner=$owner" --filter "label=com.docker.compose.project=$project" --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0) { throw 'Owned network inventory failed.' }
    $runtime.containers = @($containers | Where-Object { $_ })
    $runtime.networks = @($networks | Where-Object { $_ })
    Save-Manifest
}

function Assert-OwnedResource([string]$Kind, [string]$Id) {
    if ($Id -notmatch '^[a-f0-9]{64}$') { throw 'An exact Docker resource ID is required.' }
    $template = if ($Kind -eq 'container') {
        '{{.Id}} {{index .Config.Labels "translacat.chat.run"}} {{index .Config.Labels "translacat.chat.owner"}} {{index .Config.Labels "com.docker.compose.project"}}'
    } else {
        '{{.Id}} {{index .Labels "translacat.chat.run"}} {{index .Labels "translacat.chat.owner"}} {{index .Labels "com.docker.compose.project"}}'
    }
    $identity = & docker $Kind inspect --format $template $Id
    if ($LASTEXITCODE -ne 0 -or $identity -ne "$Id $runId $owner $project") { throw 'Owned startup resource identity verification failed.' }
}

Save-Manifest
$verified = $false
$compose = @('compose','--project-name',$project,'--project-directory',$deploy,'--env-file',$environmentPath,
    '-f',(Join-Path $deploy 'compose.yaml'),'-f',$overlayPath)
try {
    & docker @compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Production Compose plus test overlay configuration is invalid.' }
    & docker @compose up --detach --no-build --pull never
    if ($LASTEXITCODE -ne 0) { throw 'Owned production-configuration startup failed.' }
    Find-OwnedResources
    if ($runtime.containers.Count -ne 2 -or $runtime.networks.Count -ne 2) { throw 'The startup fixture must contain exactly two services and two networks.' }
    $runtime.status = 'STARTED'
    Save-Manifest

    # Kestrel/실제 ACL Redis/HTTP middleware를 통과한다. readiness 실패를 성공인 것처럼 바꾸지 않는다.
    $address = "http://127.0.0.1:$port"
    $probeHeaders = @{ Host = $ProbeHost }
    $ready = $null
    $health = $null
    $stopAt = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while ([DateTimeOffset]::UtcNow -lt $stopAt) {
        try {
            $health = Invoke-WebRequest "$address/api/health" -Headers $probeHeaders -TimeoutSec 4 -SkipHttpErrorCheck
            $ready = Invoke-WebRequest "$address/api/ready" -Headers $probeHeaders -TimeoutSec 4 -SkipHttpErrorCheck
            $body = $ready.Content | ConvertFrom-Json
            if ($health.StatusCode -eq 200 -and $body.redis -eq 'READY' -and $body.realtime -eq 'READY') { break }
        } catch [System.Net.Http.HttpRequestException] { }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $health -or $null -eq $ready) { throw 'Actual CHAT HTTP endpoints did not become reachable.' }
    $wrongHost = Invoke-WebRequest "$address/api/health" -TimeoutSec 4 -SkipHttpErrorCheck
    if ($wrongHost.StatusCode -ne 400) { throw 'A loopback Host without the allowed Production host was not rejected.' }
    $healthBody = $health.Content | ConvertFrom-Json
    $readyBody = $ready.Content | ConvertFrom-Json
    # 실패 응답도 안전한 진단 자료로 먼저 남긴다. assertion 실패를 PASS로 바꾸지 않는다.
    @{ healthStatus = $health.StatusCode; health = $healthBody; readinessStatus = $ready.StatusCode; readiness = $readyBody;
        actualDatabaseIntegration = $false; actualProviderCalls = 0 } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'http-observations.json') -Encoding utf8
    if ($health.StatusCode -ne 200 -or $healthBody.status -ne 'UP' -or $healthBody.service -ne 'TranslaCat.Chat') { throw 'Health liveness contract did not pass.' }
    $expectedReadiness = $ready.StatusCode -eq 503 -and $readyBody.status -eq 'NOT_READY' -and $readyBody.database -eq 'NOT_READY' -and
        $readyBody.authentication -eq 'NOT_CONFIGURED' -and $readyBody.identity -eq 'NOT_CONFIGURED' -and
        $readyBody.redis -eq 'READY' -and $readyBody.realtime -eq 'READY' -and $readyBody.presence -eq 'NOT_READY' -and
        $readyBody.presenceProfiles -eq 'NOT_CONFIGURED' -and $readyBody.imageUploads -eq 'NOT_CONFIGURED' -and
        $readyBody.sourceTimeZone -eq 'CONFIGURED_NOT_VERIFIED'
    if (-not $expectedReadiness) { throw 'Readiness did not preserve the explicit unavailable DB/account boundary with live dedicated Redis.' }
    $denied = Invoke-WebRequest "$address/api/v1/chat/rooms" -Headers $probeHeaders -TimeoutSec 4 -SkipHttpErrorCheck
    if ($denied.StatusCode -ne 401) { throw 'The production HTTP pipeline did not reject an unauthenticated business request.' }

    @{ healthStatus = $health.StatusCode; health = $healthBody; readinessStatus = $ready.StatusCode; readiness = $readyBody;
        wrongHostStatus = $wrongHost.StatusCode; probeHost = $ProbeHost;
        unauthenticatedBusinessStatus = $denied.StatusCode; actualDatabaseIntegration = $false; actualProviderCalls = 0 } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'http-observations.json') -Encoding utf8
    $verified = $true
    $runtime.status = 'VERIFIED_FAIL_CLOSED'
} finally {
    # 실패 중 부분 생성된 자원도 label로 찾되 모두 ID/label을 재검증한 후 정확한 이번 대상만 정리한다.
    Find-OwnedResources
    foreach ($id in $runtime.containers) { Assert-OwnedResource 'container' $id }
    foreach ($id in $runtime.networks) { Assert-OwnedResource 'network' $id }
    foreach ($id in $runtime.containers) {
        & docker stop --time 10 $id | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned startup container stop failed; manifest is retained.' }
        & docker rm $id | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned startup container removal failed; manifest is retained.' }
    }
    foreach ($id in $runtime.networks) {
        & docker network rm $id | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned startup network removal failed; manifest is retained.' }
    }
    $runtime.status = if ($verified) { 'VERIFIED_FAIL_CLOSED_CLEANED' } else { 'FAILED_CLEANED' }
    $runtime.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Manifest
    Write-Output "CHAT deployment startup smoke manifest: $manifestPath"
}
