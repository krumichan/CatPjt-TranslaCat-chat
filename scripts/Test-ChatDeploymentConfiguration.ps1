param([switch]$IncludeCompose)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$runId = [Guid]::NewGuid().ToString('N')
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$output = Join-Path $resultsRoot "Configuration/$runId"
New-Item -ItemType Directory -Path $output | Out-Null
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Deployment configuration check failed: $Name" }
    $checks.Add($Name)
}

# 준비: 실제 파일을 stage하지 않고 Git의 판정으로 공개 예제와 private 경로를 구분한다.
$private = @('.env', '.env.local', '.env.prod', '.secrets/development/key', 'deploy/chat/secrets/key',
    'TranslaCat.Chat.Api/secrets/key', 'TranslaCat.Chat.Api/private.key', 'TestResults/fixture/private')
$public = @('deploy/chat/.env.example', 'deploy/chat/.env.local.example', 'deploy/chat/.env.prod.example',
    'deploy/be/chat-internal.properties.example', 'deploy/ai/chat-auth.env.example',
    'TranslaCat.Chat.Api/appsettings.json', 'TranslaCat.Chat.Api/appsettings.Development.json', 'TranslaCat.Chat.Api/appsettings.Production.json')
foreach ($path in $private + $public) {
    & git -c "safe.directory=$workspace" -C $workspace check-ignore --no-index --quiet -- $path
    Assert-Check ($LASTEXITCODE -eq $(if ($private -contains $path) { 0 } else { 1 })) "Git policy: $path"
}

# 제공 서비스와 인증은 기본 구성에서도 켜고, 필수 자격증명 검증은 유지한다.
$baseSettings = Get-Content -LiteralPath (Join-Path $workspace 'TranslaCat.Chat.Api/appsettings.json') -Raw | ConvertFrom-Json
foreach ($section in @('Authentication', 'Identity', 'Core', 'Presence', 'Ai', 'Translation')) {
    Assert-Check ($baseSettings.Chat.$section.Enabled -eq $true) "Base service is enabled: $section"
}
Assert-Check ($baseSettings.Chat.ServiceAuthentication.Ingress.Enabled -eq $true) 'Base ingress authentication is enabled'

# 실행: 민감 원문 없이 설정 구조만 해석한다. 필수값이 빈 공개 예제는 그대로 실행할 수 없어야 한다.
foreach ($example in @('.env.local.example', '.env.prod.example')) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $workspace "deploy/chat/$example")) {
        if ($line -match '^([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }
    $expected = if ($example -eq '.env.local.example') { 'Development' } else { 'Production' }
    Assert-Check ($values.DOTNET_ENVIRONMENT -ceq $expected -and $values.ASPNETCORE_ENVIRONMENT -ceq $expected) "Environment example: $expected"
    Assert-Check ([string]::IsNullOrEmpty($values.CHAT_SOURCE_TIME_ZONE)) "Unconfirmed source zone is empty: $expected"
    foreach ($key in $values.Keys | Where-Object { $_ -like '*_FILE' }) {
        Assert-Check ([string]::IsNullOrEmpty($values[$key])) "No real secret path: $example $key"
    }
}

# 키 도구는 이 실행의 ignored 합성 경로에만 사용한다. 값은 검사 메모리 밖으로 출력하지 않는다.
$keyPath = Join-Path $output 'keys'
$previousDotnet = $env:DOTNET_ENVIRONMENT
$previousAspnet = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    & (Join-Path $PSScriptRoot 'Start-ChatLocal.ps1') -Environment Development -ValidateOnly | Out-Null
    Assert-Check ($env:DOTNET_ENVIRONMENT -ceq 'Development' -and $env:ASPNETCORE_ENVIRONMENT -ceq 'Development') 'Local launch validation preserves environment and starts no server'
    & (Join-Path $PSScriptRoot 'New-ChatDevelopmentKeys.ps1') -Environment Development -OutputDirectory $keyPath | Out-Null
    $names = @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'chat-to-ai-api-key')
    $before = @{}
    $keyValues = @()
    foreach ($name in $names) {
        $value = [IO.File]::ReadAllText((Join-Path $keyPath $name))
        $decoded = [Convert]::FromBase64String($value)
        Assert-Check ($decoded.Length -eq 32) "CSPRNG 32-byte output: $name"
        $before[$name] = (Get-FileHash -LiteralPath (Join-Path $keyPath $name) -Algorithm SHA256).Hash
        $keyValues += $value
    }
    Assert-Check (($keyValues | Select-Object -Unique).Count -eq 3) 'Independent directional credentials'
    $keyValues = $null
    $reused = @(& (Join-Path $PSScriptRoot 'New-ChatDevelopmentKeys.ps1') -Environment Development -OutputDirectory $keyPath)
    Assert-Check (@($reused | Where-Object Status -eq REUSED).Count -eq 3) 'Existing credentials reused without rotation'
    Assert-Check (($reused | Where-Object Name -eq redis-password).Status -eq 'NEEDS_INPUT' -and
        -not (Test-Path -LiteralPath (Join-Path $keyPath 'redis-password'))) 'Existing Redis server input is required, never invented'
    foreach ($name in $names) {
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $keyPath $name) -Algorithm SHA256).Hash -eq $before[$name]) "Existing credential preserved: $name"
    }

    $env:DOTNET_ENVIRONMENT = 'Production'
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'Start-ChatLocal.ps1') -Environment Development -ValidateOnly | Out-Null }
    catch { $refused = $true }
    Assert-Check $refused 'Local launch rejects a Production environment'
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'New-ChatDevelopmentKeys.ps1') -Environment Development -OutputDirectory (Join-Path $output 'production-refused') | Out-Null }
    catch { $refused = $true }
    Assert-Check ($refused -and -not (Test-Path -LiteralPath (Join-Path $output 'production-refused'))) 'Production environment cannot generate development credentials'
} finally {
    $env:DOTNET_ENVIRONMENT = $previousDotnet
    $env:ASPNETCORE_ENVIRONMENT = $previousAspnet
}

if ($IncludeCompose) {
    # 준비: Compose의 file mapping만 검사할 합성 파일이며 Redis 계정 생성이나 실제 비밀번호 공급이 아니다.
    $composeFixture = Join-Path $keyPath 'compose-synthetic-secret'
    [IO.File]::WriteAllText($composeFixture, 'synthetic-compose-only-file-no-server-credential', [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $composeFixture -Destination "$($composeFixture)_api"

    # Compose interpolation과 container environment 전달을 함께 확인한다. 결과 JSON은 메모리에서만 비교한다.
    $names = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'CHAT_*' -or $_.Name -in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT') })
    $previous = @{}
    foreach ($item in $names) {
        $previous[$item.Name] = $item.Value
        [Environment]::SetEnvironmentVariable($item.Name, $null)
    }
    try {
        foreach ($environment in @('Development', 'Production')) {
            $envFile = Join-Path $output "$environment.env"
            $safePath = $composeFixture.Replace('\', '/')
            $values = @(
                "DOTNET_ENVIRONMENT=$environment", "ASPNETCORE_ENVIRONMENT=$environment",
                'CHAT_BROWSER_ORIGIN=https://synthetic.example.invalid',
                # 제거된 availability 변수의 옛 false 값으로 필수 서비스를 끌 수 없어야 한다.
                'CHAT_SERVICE_INGRESS_ENABLED=false', 'CHAT_IDENTITY_ENABLED=false', 'CHAT_CORE_ENABLED=false',
                'CHAT_AI_ENABLED=false', 'CHAT_TRANSLATION_ENABLED=false',
                "CHAT_REDIS_NAMESPACE=translacat:chat:test:$runId", 'CHAT_SOURCE_TIME_ZONE=Etc/UTC',
                "CHAT_REDIS_PASSWORD_FILE=$safePath", "CHAT_DATABASE_CONNECTION_FILE=$safePath", "CHAT_JWT_SIGNING_KEY_FILE=$safePath",
                "CHAT_SERVICE_INGRESS_KEY_FILE=$safePath", "CHAT_IDENTITY_KEY_FILE=$safePath", "CHAT_AI_API_KEY_FILE=$safePath"
            )
            [IO.File]::WriteAllLines($envFile, $values, [Text.Encoding]::ASCII)
            $arguments = @('compose', '--project-directory', (Join-Path $workspace 'deploy/chat'), '--env-file', $envFile,
                '-f', (Join-Path $workspace 'deploy/chat/compose.yaml'), '-f', (Join-Path $workspace 'deploy/chat/compose.internal-auth.yaml'))
            & docker @arguments config --quiet
            Assert-Check ($LASTEXITCODE -eq 0) "Compose structure: $environment"
            $rendered = & docker @arguments config --format json
            Assert-Check ($LASTEXITCODE -eq 0) "Compose configuration parse: $environment"
            $configuration = ($rendered -join "`n") | ConvertFrom-Json
            $settings = $configuration.services.'chat-api'.environment
            Assert-Check ($settings.DOTNET_ENVIRONMENT -ceq $environment -and $settings.ASPNETCORE_ENVIRONMENT -ceq $environment) "Compose environment injection: $environment"
            Assert-Check ($settings.Chat__Redis__Endpoint -eq 'chat-redis:6379' -and $settings.Chat__Redis__Password_FILE -eq '/run/secrets/chat_redis_password_api') "Redis endpoint and managed secret-file: $environment"
            Assert-Check ($settings.Chat__Database__ConnectionString_FILE -eq '/run/secrets/chat_database_connection' -and
                $settings.Chat__Authentication__Base64SigningKey_FILE -eq '/run/secrets/chat_jwt_signing_key') "DB and JWT managed secret-file: $environment"
            Assert-Check ($settings.Chat__ServiceAuthentication__Ingress__Base64SigningKey_FILE -eq '/run/secrets/chat_ingress_signing_key' -and
                $settings.Chat__Identity__ServiceAuthentication__Base64SigningKey_FILE -eq '/run/secrets/chat_identity_signing_key') "Separate directional service secret-files: $environment"
            Assert-Check ($settings.Chat__Ai__ApiKey_FILE -eq '/run/secrets/chat_ai_api_key' -and
                $settings.Chat__Translation__ApiKey_FILE -eq $settings.Chat__Ai__ApiKey_FILE) "Same CHAT-to-AI direction secret-file: $environment"
            Assert-Check ($settings.Chat__Core__ServiceAuthentication__Base64SigningKey_FILE -eq $settings.Chat__Identity__ServiceAuthentication__Base64SigningKey_FILE -and
                $settings.Chat__Core__Enabled -eq 'true') "Core and identity share outbound direction and stay enabled: $environment"
            Assert-Check ($settings.Chat__Realtime__AllowedOrigins__0 -eq 'https://synthetic.example.invalid') "Explicit browser origin binding: $environment"
            Assert-Check ($settings.Chat__ServiceAuthentication__Ingress__Enabled -eq 'true' -and $settings.Chat__Identity__Enabled -eq 'true' -and
                $settings.Chat__Ai__Enabled -eq 'true' -and $settings.Chat__Translation__Enabled -eq 'true') "Retired false availability variables cannot disable required services: $environment"
            Assert-Check ($configuration.services.'chat-redis'.ports.Count -eq 0) "Redis has no host-published port: $environment"
            $rendered = $null
            $configuration = $null
        }
    } finally {
        foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
}

# 검증 결과에는 값이나 hash 대신 항목 이름만 남긴다. 합성 파일도 자동으로 운영에 사용하지 않는다.
@{ runId = $runId; testMode = $true; passed = $checks.Count; failed = 0; checks = @($checks);
    composeParsed = [bool]$IncludeCompose; dockerStarted = $false; productionKeysGenerated = 0; sharedRepositoriesEdited = $false } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'configuration-checks.json') -Encoding utf8
Write-Output "Deployment configuration checks passed: $($checks.Count). Evidence: $output"
