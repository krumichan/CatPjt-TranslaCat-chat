param(
    [string]$ConfigurationFile = (Join-Path $PSScriptRoot '../deploy/local/Development.json'),
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChatDevelopmentConfiguration.ps1')
$c = Get-ChatDevelopmentConfiguration $ConfigurationFile
$root = Split-Path -Parent $PSScriptRoot
$dockerCommand = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$docker = if ($dockerCommand) { $dockerCommand.Source } else { Join-Path $env:ProgramFiles 'Docker/Docker/resources/bin/docker.exe' }
if (-not (Test-Path -LiteralPath $docker -PathType Leaf)) { throw 'Existing Docker installation is required.' }

function Assert-ExistingRedisConfiguration {
    # 이름 충돌과 같은 Compose service의 다른 이름을 함께 찾는다. 조회 실패를 신규 상태로 간주하지 않는다.
    $project = 'translacat-chat-development'
    $service = 'chat-redis'
    $name = "$project-$service-1"
    try {
        $named = @(& $docker container ls --all --quiet --no-trunc --filter "name=^/$name$" 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Container inventory failed.' }
        $owned = @(& $docker container ls --all --quiet --no-trunc --filter "label=com.docker.compose.project=$project" `
            --filter "label=com.docker.compose.service=$service" 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Container inventory failed.' }
        $ids = @(@($named) + @($owned) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
        if ($ids.Count -eq 0) { return }
        if ($ids.Count -ne 1 -or $ids[0] -cnotmatch '^[a-f0-9]{64}$') { throw 'Ambiguous container ownership.' }
        $raw = (& $docker container inspect $ids[0] 2>$null) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) { throw 'Container inspection failed.' }
        $objects = @($raw | ConvertFrom-Json -AsHashtable)
        if ($objects.Count -ne 1) { throw 'Invalid container metadata.' }
        $existing = $objects[0]
        if ($existing.Id -cne $ids[0] -or $existing.Config.Labels['com.docker.compose.project'] -cne $project -or
            $existing.Config.Labels['com.docker.compose.service'] -cne $service) { throw 'Container ownership differs.' }

        # AUTH/PING만으로 namespace 권한과 host port 일치를 주장하지 않는다.
        $namespace = @($existing.Config.Env | Where-Object { $_.StartsWith('CHAT_REDIS_NAMESPACE=', [StringComparison]::Ordinal) })
        if ($namespace.Count -ne 1 -or $namespace[0] -cne ('CHAT_REDIS_NAMESPACE=' + $c.RedisNamespace)) {
            throw 'Redis namespace differs.'
        }
        $ports = $existing.HostConfig.PortBindings
        $expectedPort = ($c.RedisEndpoint -split ':')[-1]
        if ($ports.Count -ne 1 -or -not $ports.Contains('6379/tcp')) { throw 'Published Redis ports differ.' }
        $bindings = @($ports['6379/tcp'])
        if ($bindings.Count -ne 1 -or $bindings[0].HostIp -cne '127.0.0.1' -or
            $bindings[0].HostPort -cne $expectedPort) { throw 'Published Redis binding differs.' }

        # 실제 Docker Desktop inspect는 Windows 절대 Source를 반환한다. 다른 경로 체계를 추측해 변환하지 않는다.
        $mounts = @($existing.Mounts | Where-Object { $_.Destination -ceq '/run/secrets/chat_redis_password' })
        if ($mounts.Count -ne 1 -or $mounts[0].Type -cne 'bind' -or $mounts[0].RW -ne $false -or
            -not [IO.Path]::IsPathFullyQualified($mounts[0].Source)) { throw 'Redis secret mount differs.' }
        $source = [IO.Path]::GetFullPath($mounts[0].Source)
        $expected = [IO.Path]::GetFullPath((Join-Path $c.SecretDirectory 'redis-password'))
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $source.Equals($expected, $comparison)) { throw 'Redis secret source differs.' }
    } catch {
        # inspect의 env 값이나 파일 내용은 진단에 포함하지 않으며 기존 컨테이너를 수정하지 않는다.
        throw 'MISMATCH: existing CHAT Redis ownership, namespace, host port or secret mount could not be verified against the manifest. No container was changed.'
    } finally {
        $raw = $null
        $existing = $null
    }
}

# 기존 Compose의 명시적 interpolation 입력만 설정한다. 실제 비밀은 Docker secret file mount로 전달한다.
$settings = @{
    DOTNET_ENVIRONMENT = 'Development'; ASPNETCORE_ENVIRONMENT = 'Development'
    CHAT_SOURCE_TIME_ZONE = $c.SourceTimeZone; CHAT_REDIS_NAMESPACE = $c.RedisNamespace
    CHAT_HOST_REDIS_PORT = ($c.RedisEndpoint -split ':')[-1]
    CHAT_REDIS_PASSWORD_FILE = (Join-Path $c.SecretDirectory 'redis-password')
    CHAT_DATABASE_CONNECTION_FILE = (Join-Path $c.SecretDirectory 'chat-database-runtime')
    CHAT_JWT_SIGNING_KEY_FILE = (Join-Path $c.SecretDirectory 'be-user-jwt-signing-key')
}
$previous = @{}
foreach ($name in $settings.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    if ($previous[$name] -and $previous[$name] -cne $settings[$name]) { throw 'MISMATCH: inherited Compose configuration differs from the Development manifest.' }
    if ($name.EndsWith('_FILE') -and -not (Test-Path -LiteralPath $settings[$name] -PathType Leaf)) {
        throw 'NEEDS_INPUT: existing Redis, runtime DB and user JWT files are required. This launcher creates no credentials.'
    }
}
$arguments = @('compose', '--project-name', 'translacat-chat-development',
    '--env-file', (Join-Path $root 'deploy/local/compose.empty.env'),
    '-f', (Join-Path $root 'deploy/chat/compose.yaml'), '-f', (Join-Path $root 'deploy/local/compose.host-redis.yaml'))
try {
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $settings[$name]) }
    & $docker @arguments config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Development Redis Compose validation failed.' }
    Assert-ExistingRedisConfiguration
    if ($ValidateOnly) {
        Write-Output 'VERIFIED: Compose path bindings and any existing Redis container metadata match the manifest; Redis AUTH/PING was not executed.'
        return
    }

    # 다른 서버를 중단/재생성하지 않는다. 명시적인 CHAT 개발 프로젝트의 Redis만 시작한다.
    & $docker @arguments up -d --no-deps --no-recreate --pull never --wait --wait-timeout 45 chat-redis
    if ($LASTEXITCODE -ne 0) { throw 'CHAT Development Redis did not start. Existing containers were preserved.' }
    Write-Output 'VERIFIED: dedicated Redis healthcheck AUTH/PING passed. Existing credentials and containers were preserved.'
} finally {
    foreach ($name in $previous.Keys) {
        $original = if ($null -eq $previous[$name]) { [NullString]::Value } else { $previous[$name] }
        [Environment]::SetEnvironmentVariable($name, $original)
    }
}
