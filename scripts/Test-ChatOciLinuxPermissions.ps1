param([Parameter(Mandatory)][string]$Image)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$runId = [Guid]::NewGuid().ToString('N')
$volume = "chat-oci-permissions-$runId"
$project = "chat-permissions-$runId"
$oldSha = '1111111111111111111111111111111111111111'
$newSha = '2222222222222222222222222222222222222222'
$sdk = 'mcr.microsoft.com/dotnet/sdk:10.0.401-noble'
$cli = 'docker:29-cli'
$owner = 'chat-oci-linux-permissions-test'
$evidence = [IO.Path]::GetFullPath((Join-Path $root "../.codex-workspace/verification/chat/TestResults/OciLinuxPermissions/$runId"))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Assert-DockerExit([string]$Action) {
    if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit $LASTEXITCODE." }
}

function Encode-Synthetic([string]$Value) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

# 운영 비밀을 읽지 않고 실제 32개 이름으로 합성 Production 입력을 구성한다.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$replacements = @{
    CHAT_API_PORT = "$port"; CHAT_ALLOWED_HOSTS = 'chat.invalid'
    CHAT_BROWSER_ORIGIN = 'https://chat.invalid'; CHAT_SOURCE_TIME_ZONE = 'Asia/Tokyo'
    CHAT_IDENTITY_BASE_URL = 'https://be.invalid/'; CHAT_CORE_BASE_URL = 'https://be.invalid/'
    CHAT_AI_BASE_URL = 'https://ai.invalid/'; CHAT_REDIS_NAMESPACE = "translacat:chat:permission-test:$runId"
}
$lines = foreach ($line in Get-Content -LiteralPath (Join-Path $root 'deploy/chat/.env.example')) {
    if ($line -match '^([A-Z][A-Z0-9_]*)=' -and $replacements.ContainsKey($Matches[1])) {
        "$($Matches[1])=$($replacements[$Matches[1]])"
    } else { $line }
}
$syntheticEnv = Encode-Synthetic (($lines -join [char]10) + [char]10)
$syntheticDb = 'Server=127.0.0.1;Port=1;Database=translacat_chat;User ID=synthetic;Password=synthetic_only;Connection Timeout=1;Pooling=False'
$syntheticJwt = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$syntheticIngress = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$syntheticIdentity = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$syntheticAi = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$oldRedis = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$newRedis = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

& docker image inspect $Image $sdk $cli | Out-Null
Assert-DockerExit 'Local Linux image lookup'
$created = $false
$composeReady = $false
$mountPoint = $null
$results = [ordered]@{
    runId = $runId; owner = $owner; image = $Image; volume = $volume; project = $project
    status = 'PREPARED'; actualLinuxFilesystem = $true; actualProductionSecrets = 0
    actualDatabaseIntegration = $false
}

try {
    # 실행별 named volume을 Linux daemon 파일시스템으로 사용한다.
    & docker volume create --label "translacat.chat.owner=$owner" --label "translacat.chat.run=$runId" $volume | Out-Null
    Assert-DockerExit 'Owned Linux volume creation'
    $created = $true
    $mountPoint = & docker volume inspect --format '{{.Mountpoint}}' $volume
    Assert-DockerExit 'Owned Linux mountpoint'
    if ($mountPoint -notmatch "^/var/lib/docker/volumes/$volume/_data$") { throw 'Unexpected Linux mountpoint.' }

    $setup = @'
set -eu
mkdir -p "$WORKSPACE/deploy/chat" "$WORKSPACE/scripts" "$WORKSPACE/bin"
cp /source/deploy/chat/.env.example /source/deploy/chat/compose.yaml /source/deploy/chat/compose.internal-auth.yaml /source/deploy/chat/compose.production.yaml /source/deploy/chat/redis.conf /source/deploy/chat/start-chat-redis.sh /source/deploy/chat/check-chat-redis.sh "$WORKSPACE/deploy/chat/"
cp /source/scripts/provision-chat-oci.sh "$WORKSPACE/scripts/"
printf '#!/bin/sh\necho "$FAKE_SHA"\n' > "$WORKSPACE/bin/git"
chmod 755 "$WORKSPACE/bin/git"
chown -R 20000:20000 "$WORKSPACE"
'@
    & docker run --rm --mount "type=bind,source=$root,target=/source,readonly" --mount "type=volume,source=$volume,target=$mountPoint" --env "WORKSPACE=$mountPoint" --entrypoint /bin/bash $sdk -c $setup
    Assert-DockerExit 'Linux fixture copy'

    # 배포 UID 20000이 두 전용 그룹으로 old/new 릴리스를 실제 생성하고 같은 SHA를 재실행한다.
    foreach ($release in @(@{ Sha = $oldSha; Redis = $oldRedis }, @{ Sha = $newSha; Redis = $newRedis })) {
        $env:CHAT_PRODUCTION_ENV_B64 = $syntheticEnv
        $env:CHAT_REDIS_PASSWORD_B64 = Encode-Synthetic $release.Redis
        $env:CHAT_DATABASE_CONNECTION_B64 = Encode-Synthetic $syntheticDb
        $env:CHAT_JWT_SIGNING_KEY_B64 = Encode-Synthetic $syntheticJwt
        $env:CHAT_SERVICE_INGRESS_KEY_B64 = Encode-Synthetic $syntheticIngress
        $env:CHAT_IDENTITY_KEY_B64 = Encode-Synthetic $syntheticIdentity
        $env:CHAT_AI_API_KEY_B64 = Encode-Synthetic $syntheticAi
        $env:FAKE_SHA = $release.Sha
        $script = "cd '$mountPoint'; export PATH='$mountPoint/bin':" + '$PATH; ' +
            "bash scripts/provision-chat-oci.sh '$($release.Sha)'"
        $provisionArgs = @(
            'run','--rm','--user','20000:20000','--group-add','20001','--group-add','20002',
            '--mount',"type=volume,source=$volume,target=$mountPoint",
            '-e','FAKE_SHA','-e','CHAT_PRODUCTION_ENV_B64','-e','CHAT_REDIS_PASSWORD_B64',
            '-e','CHAT_DATABASE_CONNECTION_B64','-e','CHAT_JWT_SIGNING_KEY_B64',
            '-e','CHAT_SERVICE_INGRESS_KEY_B64','-e','CHAT_IDENTITY_KEY_B64','-e','CHAT_AI_API_KEY_B64',
            '--entrypoint','/bin/bash',$sdk,'-c',$script
        )
        & docker @provisionArgs
        Assert-DockerExit 'Linux provisioning'
        & docker @provisionArgs
        Assert-DockerExit 'Same-SHA Linux provisioning'
    }

    $verify = @'
set -eu
for sha in "$OLD_SHA" "$NEW_SHA"; do
  release="$WORKSPACE/deploy/chat/.releases/$sha"
  test "$(stat -c '%u:%a' "$release/.env")" = '20000:600'
  test "$(stat -c '%u:%g:%a' "$release/secrets/chat_redis_password")" = '20000:20002:640'
  for file in chat_redis_password_api chat_database_connection chat_jwt_signing_key chat_ingress_signing_key chat_identity_signing_key chat_ai_api_key; do
    test "$(stat -c '%u:%g:%a' "$release/secrets/$file")" = '20000:20001:640'
  done
  for file in redis.conf start-chat-redis.sh check-chat-redis.sh; do
    test "$(stat -c '%a' "$release/$file")" = '644'
  done
  cmp -s "$release/secrets/chat_redis_password" "$release/secrets/chat_redis_password_api"
done
'@
    & docker run --rm --mount "type=volume,source=$volume,target=$mountPoint,readonly" --env "WORKSPACE=$mountPoint" --env "OLD_SHA=$oldSha" --env "NEW_SHA=$newSha" --entrypoint /bin/bash $sdk -c $verify
    Assert-DockerExit 'Linux owner/group/mode checks'

    $access = @'
set -eu
for sha in "$OLD_SHA" "$NEW_SHA"; do
  release="$WORKSPACE/deploy/chat/.releases/$sha/secrets"
  case "$ROLE" in
    api) test -r "$release/chat_redis_password_api"; test -r "$release/chat_jwt_signing_key"; test ! -r "$release/chat_redis_password" ;;
    redis) test -r "$release/chat_redis_password"; test ! -r "$release/chat_jwt_signing_key"; test ! -r "$release/chat_redis_password_api" ;;
    unrelated) for file in "$release"/*; do test ! -r "$file"; done ;;
  esac
done
'@
    foreach ($role in @(
        @{ Name = 'api'; User = '1654:1654'; Group = '20001' },
        @{ Name = 'redis'; User = '999:1000'; Group = '20002' },
        @{ Name = 'unrelated'; User = '30000:30000'; Group = '30000' }
    )) {
        & docker run --rm --user $role.User --group-add $role.Group --mount "type=volume,source=$volume,target=$mountPoint,readonly" --env "WORKSPACE=$mountPoint" --env "OLD_SHA=$oldSha" --env "NEW_SHA=$newSha" --env "ROLE=$($role.Name)" --entrypoint /bin/bash $sdk -c $access
        Assert-DockerExit "Linux $($role.Name) access isolation"
    }
    $results.status = 'LINUX_PERMISSIONS_VERIFIED'

    $cliBase = @(
        'run','--rm','--user','20000:20000','--group-add','0','--group-add','20001','--group-add','20002',
        '--mount',"type=volume,source=$volume,target=$mountPoint",
        '-v','/var/run/docker.sock:/var/run/docker.sock','-e','HOME=/tmp',"-e","CHAT_IMAGE=$Image",
        '-w',$mountPoint,$cli
    )
    $composeReady = $true

    # 같은 Linux 릴리스 파일을 old → new → old 순서로 실제 Compose와 이미지에 연결한다.
    foreach ($releaseSha in @($oldSha, $newSha, $oldSha)) {
        $directory = "$mountPoint/deploy/chat/.releases/$releaseSha"
        $composeArgs = @(
            'compose','--project-name',$project,'--project-directory',$directory,
            '--env-file',"$directory/.env",'-f',"$directory/compose.yaml",
            '-f',"$directory/compose.internal-auth.yaml",'-f',"$directory/compose.production.yaml"
        )
        & docker @cliBase @composeArgs config --quiet
        Assert-DockerExit 'Linux protected release Compose config'
        & docker @cliBase @composeArgs run --rm --no-deps chat-api --validate-configuration
        Assert-DockerExit 'Non-root CHAT managed configuration'
        & docker @cliBase @composeArgs up --detach --force-recreate --no-build --pull never chat-redis
        Assert-DockerExit 'Non-root Redis startup'
        & docker @cliBase @composeArgs up --detach --force-recreate --no-build --pull never --no-deps chat-api
        Assert-DockerExit 'Non-root CHAT startup'

        $ping = & docker @cliBase @composeArgs exec -T chat-redis /bin/sh -c 'REDISCLI_AUTH="$(cat /run/secrets/chat_redis_password)" redis-cli --user chat ping'
        Assert-DockerExit 'Authenticated Redis PING'
        if ($ping -ne 'PONG') { throw 'Authenticated Redis PING did not return PONG.' }
        & docker @cliBase @composeArgs exec -T chat-api /bin/sh -c 'test -r /run/secrets/chat_redis_password_api && test -r /run/secrets/chat_jwt_signing_key && test ! -e /run/secrets/chat_redis_password'
        Assert-DockerExit 'API-mounted secret isolation'
        & docker @cliBase @composeArgs exec -T chat-redis /bin/sh -c 'test -r /run/secrets/chat_redis_password && test -r /chat-config/redis.conf && test ! -e /run/secrets/chat_jwt_signing_key'
        Assert-DockerExit 'Redis-mounted secret isolation'

        # DB가 없는 합성 환경에서도 새 API가 해당 Redis 암호로 실제 연결했는지 확인한다.
        $health = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $health = Invoke-WebRequest "http://127.0.0.1:$port/api/health" -Headers @{ Host = 'chat.invalid' } -TimeoutSec 3 -SkipHttpErrorCheck
                if ($health.StatusCode -eq 200) { break }
            } catch [System.Net.Http.HttpRequestException] { }
            Start-Sleep -Milliseconds 250
        }
        if ($null -eq $health -or $health.StatusCode -ne 200) { throw 'Actual CHAT health did not return 200.' }
        $ready = Invoke-WebRequest "http://127.0.0.1:$port/api/ready" -Headers @{ Host = 'chat.invalid' } -TimeoutSec 4 -SkipHttpErrorCheck
        $readyBody = $ready.Content | ConvertFrom-Json
        if ($ready.StatusCode -ne 503 -or $readyBody.redis -ne 'READY') {
            throw 'CHAT readiness did not confirm the current Redis password with the synthetic unavailable DB.'
        }
    }
    $results.status = 'LINUX_PROVISION_COMPOSE_RESTORE_VERIFIED'
    $results.health = 'OLD_200_NEW_200_RESTORED_OLD_200'
    $results.readiness = 'DB_503_REDIS_READY_OLD_NEW_RESTORED_OLD'
    $results.redis = 'AUTHENTICATED_PONG_OLD_NEW_RESTORED_OLD'
} finally {
    foreach ($name in @('CHAT_PRODUCTION_ENV_B64','CHAT_REDIS_PASSWORD_B64','CHAT_DATABASE_CONNECTION_B64',
        'CHAT_JWT_SIGNING_KEY_B64','CHAT_SERVICE_INGRESS_KEY_B64','CHAT_IDENTITY_KEY_B64','CHAT_AI_API_KEY_B64','FAKE_SHA')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    if ($created -and $composeReady -and $mountPoint) {
        $directory = "$mountPoint/deploy/chat/.releases/$oldSha"
        $composeArgs = @('compose','--project-name',$project,'--project-directory',$directory,
            '--env-file',"$directory/.env",'-f',"$directory/compose.yaml",
            '-f',"$directory/compose.internal-auth.yaml",'-f',"$directory/compose.production.yaml")
        & docker @cliBase @composeArgs down --remove-orphans
        if ($LASTEXITCODE -ne 0) { $results.cleanup = 'COMPOSE_DOWN_FAILED' }
    }
    if ($created) {
        $identity = & docker volume inspect --format '{{index .Labels "translacat.chat.owner"}} {{index .Labels "translacat.chat.run"}}' $volume
        if ($LASTEXITCODE -eq 0 -and $identity -eq "$owner $runId" -and $results.cleanup -ne 'COMPOSE_DOWN_FAILED') {
            & docker volume rm $volume | Out-Null
            if ($LASTEXITCODE -ne 0) { $results.cleanup = 'OWNED_VOLUME_REMOVE_FAILED' }
            else { $results.cleanup = 'OWNED_RESOURCES_REMOVED' }
        } else {
            $results.cleanup = 'OWNED_VOLUME_RETAINED_FOR_INSPECTION'
        }
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output "CHAT Linux permission evidence: $evidence"
}
