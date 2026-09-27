param(
    [Parameter(Mandatory)][string]$Image
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$runId = [Guid]::NewGuid().ToString('N')
$output = Join-Path $workspace "../.codex-workspace/verification/chat/TestResults/OciProductionBinding/$runId"
New-Item -ItemType Directory -Path $output -Force | Out-Null

# 준비 — 운영 원본을 읽지 않고, 이 실행만 소유하는 합성 여섯 방향/저장 secret을 만든다.
$secretNames = @('redis', 'database', 'user-jwt', 'ingress', 'identity', 'ai')
$paths = @{}
foreach ($name in $secretNames) {
    $paths[$name] = Join-Path $output "$name.secret"
    $value = if ($name -eq 'database') {
        'Server=127.0.0.1;Port=1;Database=translacat_chat;User ID=synthetic;Password=' +
            [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) +
            ';Connection Timeout=1;Pooling=False'
    } elseif ($name -eq 'redis') {
        [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    } else {
        [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    }
    [IO.File]::WriteAllText($paths[$name], $value, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $paths.redis -Destination "$($paths.redis)_api"

$substitutions = @{
    CHAT_ALLOWED_HOSTS = 'chat.invalid'
    CHAT_BROWSER_ORIGIN = 'https://chat.invalid'
    CHAT_SOURCE_TIME_ZONE = 'Asia/Tokyo'
    CHAT_IDENTITY_BASE_URL = 'https://be.invalid/'
    CHAT_CORE_BASE_URL = 'https://be.invalid/'
    CHAT_AI_BASE_URL = 'https://ai.invalid/'
    CHAT_REDIS_PASSWORD_FILE = $paths.redis.Replace('\', '/')
    CHAT_DATABASE_CONNECTION_FILE = $paths.database.Replace('\', '/')
    CHAT_JWT_SIGNING_KEY_FILE = $paths.'user-jwt'.Replace('\', '/')
    CHAT_SERVICE_INGRESS_KEY_FILE = $paths.ingress.Replace('\', '/')
    CHAT_IDENTITY_KEY_FILE = $paths.identity.Replace('\', '/')
    CHAT_AI_API_KEY_FILE = $paths.ai.Replace('\', '/')
}
$environmentFile = Join-Path $output 'compose.env'
$lines = foreach ($line in Get-Content -LiteralPath (Join-Path $workspace 'deploy/chat/.env.example')) {
    if ($line -match '^([A-Z][A-Z0-9_]*)=' -and $substitutions.ContainsKey($Matches[1])) {
        "$($Matches[1])=$($substitutions[$Matches[1]])"
    } else {
        $line
    }
}
[IO.File]::WriteAllLines($environmentFile, $lines, [Text.UTF8Encoding]::new($false))

$previousImage = $env:CHAT_IMAGE
$compose = $null
try {
    # 실행 — 검사기는 경로/형식을, Compose는 전달을, 실제 image는 managed binding을 검사한다.
    $preparation = & (Join-Path $PSScriptRoot 'Test-ChatProductionPreparation.ps1') `
        -EnvironmentFile $environmentFile -PassThru
    if (-not $preparation.Ready) { throw 'Synthetic Production preparation was not structurally ready.' }

    $env:CHAT_IMAGE = $Image
    $compose = @('compose', '--project-name', "chat-oci-binding-$runId", '--project-directory',
        (Join-Path $workspace 'deploy/chat'), '--env-file', $environmentFile,
        '-f', (Join-Path $workspace 'deploy/chat/compose.yaml'),
        '-f', (Join-Path $workspace 'deploy/chat/compose.internal-auth.yaml'),
        '-f', (Join-Path $workspace 'deploy/chat/compose.production.yaml'))
    & docker @compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Production Compose config failed.' }

    & docker @compose run --rm --no-deps chat-api --validate-configuration
    if ($LASTEXITCODE -ne 0) { throw 'Production managed configuration binding failed.' }

    # 검증 — 실제 외부 DB/Redis/BE/AI 호출과 구분해 비밀 없는 범위만 남긴다.
    [ordered]@{
        runId = $runId
        image = $Image
        productionPreparation = 'VERIFIED_SYNTHETIC_STRUCTURE'
        compose = 'VERIFIED_SYNTHETIC_INTERPOLATION'
        managedBinding = 'VERIFIED_SYNTHETIC_OPTIONS_DI'
        externalConnections = 0
        actualProductionSecrets = 0
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    Write-Output "Production synthetic binding passed. Evidence: $output"
} finally {
    # 이 실행의 무작위 Compose project가 만든 네트워크만 정리한다. 공용/운영 project는 대상이 아니다.
    if ($null -ne $compose) {
        & docker @compose down --remove-orphans | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned synthetic Compose project cleanup failed.' }
    }
    $env:CHAT_IMAGE = $previousImage
}
