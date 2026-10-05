param([string]$ResultDirectory, [switch]$IncludeAiSettings)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$central = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
if ([string]::IsNullOrWhiteSpace($ResultDirectory)) {
    $ResultDirectory = Join-Path $central ('DevelopmentBootstrap/' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($ResultDirectory)
if (-not $output.StartsWith($central + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Bootstrap tests require a new central CHAT TestResults directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
. (Join-Path $PSScriptRoot 'ChatDevelopmentConfiguration.ps1')
$initialize = Join-Path $PSScriptRoot 'Initialize-ChatDevelopment.ps1'
$start = Join-Path $PSScriptRoot 'Start-ChatDevelopment.ps1'
$redisStart = Join-Path $PSScriptRoot 'Start-ChatRedisLocal.ps1'
$checks = [Collections.Generic.List[string]]::new()
$skipped = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Development bootstrap check failed: $Name" }
    $checks.Add($Name)
}

function Write-Manifest([string]$Path, [hashtable]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 5), $utf8)
}

function Get-FileSnapshot([string]$Directory) {
    $snapshot = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
        $snapshot[[IO.Path]::GetRelativePath($Directory, $file.FullName)] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
    }
    return $snapshot
}

function Assert-Snapshot([string]$Directory, [hashtable]$Before, [string]$Name) {
    $after = Get-FileSnapshot $Directory
    Assert-Check ($after.Count -eq $Before.Count) "$Name file count"
    foreach ($name in $Before.Keys) { Assert-Check ($after[$name] -ceq $Before[$name]) "$Name preserved: $name" }
}

function Assert-Refused([string]$Name, [scriptblock]$Action, [string]$ExpectedMessage = '') {
    # 실행 / 검증 — 원문 fixture canary가 예외에도 나타나지 않는지 확인한다.
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        $failed = $true
        Assert-Check (-not $_.Exception.ToString().Contains('synthetic-bootstrap-canary')) "$Name diagnostic redaction"
        if ($ExpectedMessage) { Assert-Check ($_.Exception.Message.Contains($ExpectedMessage)) "$Name expected diagnostic" }
    }
    Assert-Check $failed $Name
}

function New-Fixture([string]$Name) {
    # 준비 — 실제 BE/AI/CHAT secret은 읽지 않는다. 기존 발급자 역할은 이 합성 파일 두 개로 한정한다.
    $directory = Join-Path $output $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $values = @{
        'be-to-chat-service-signing-key' = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        'chat-to-be-service-signing-key' = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        'chat-to-ai-api-key' = 'synthetic-bootstrap-canary-api-opaque-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
        'be-user-jwt-signing-key' = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
    }
    $beSource = Join-Path $directory 'be-source.properties'
    $beText = @(
        'chat.gateway.secret-base64=' + $values['be-to-chat-service-signing-key']
        'chat.core.identity.secret-base64=' + $values['chat-to-be-service-signing-key']
        'jwt.token.secret-key=' + $values['be-user-jwt-signing-key']
        'language-learning.internal-jwt.secret-base64=synthetic-bootstrap-canary-ll-unchanged'
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText($beSource, $beText, $utf8)
    $aiSource = Join-Path $directory 'ai-source.env'
    $aiText = 'CHAT_AUTH_MODE=dedicated' + [Environment]::NewLine +
        'SERVER_API_KEY=synthetic-bootstrap-canary-global-independent' + [Environment]::NewLine +
        'CHAT_SERVER_API_KEY="' + $values['chat-to-ai-api-key'] + '"' + [Environment]::NewLine
    [IO.File]::WriteAllText($aiSource, $aiText, $utf8)
    $manifest = @{
        Environment = 'Development'; ChatPort = 15079; BePort = 18080; AiPort = 18000
        SourceTimeZone = 'Asia/Tokyo'; BrowserOrigin = 'http://localhost:13000'
        AiEnabled = $false; TranslationEnabled = $false
        RedisEndpoint = '127.0.0.1:16380'; RedisNamespace = 'translacat:chat:Development:synthetic'
        SecretDirectory = 'keys'; BeLocalSecretsFile = 'be-source.properties'
        AiEnvironmentFile = 'ai-source.env'; BeJarPath = 'synthetic-proxy-metadata-only.jar'
    }
    $path = Join-Path $directory 'Development.json'
    Write-Manifest $path $manifest
    return @{ Directory = $directory; Manifest = $manifest; Path = $path; Values = $values
        SecretDirectory = (Join-Path $directory 'keys'); BeSource = $beSource; AiSource = $aiSource }
}

function Supply-NonGeneratedFixtureInputs([hashtable]$Fixture) {
    # 준비 — canonical 폴더는 실제 생성 도구가 owner-only로 만든다. 서버에 접속할 수 없는 합성 입력만 추가한다.
    [IO.File]::WriteAllText((Join-Path $Fixture.SecretDirectory 'redis-password'), 'synthetic-bootstrap-canary-existing-redis', $utf8)
    [IO.File]::WriteAllText((Join-Path $Fixture.SecretDirectory 'chat-database-runtime'),
        'Server=127.0.0.1;Port=13306;Database=translacat_chat;User ID=synthetic;Password=synthetic-bootstrap-canary-db;', $utf8)
    [IO.File]::WriteAllText((Join-Path $Fixture.SecretDirectory 'chat-database-schema'),
        'Server=127.0.0.1;Port=13306;Database=translacat_chat;User ID=synthetic_ddl;Password=synthetic-bootstrap-canary-ddl;', $utf8)
}

# 준비 — Docker/dotnet 호출은 테스트 sink에서 인자와 비밀 없는 FILE 경로만 기록한다.
$probe = Join-Path $output 'runtime-sink.ps1'
@'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
if ($Arguments[0] -eq 'container') {
    # Docker metadata 조회도 합성 sink로 한정한다. 실제 daemon/컨테이너를 호출하지 않는다.
    ($Arguments | ConvertTo-Json -Compress) | Add-Content -LiteralPath $env:CHAT_BOOTSTRAP_TEST_DOCKER_CALLS -Encoding utf8
    $fixture = if ($env:CHAT_BOOTSTRAP_TEST_DOCKER_METADATA) {
        Get-Content -Raw -LiteralPath $env:CHAT_BOOTSTRAP_TEST_DOCKER_METADATA | ConvertFrom-Json -AsHashtable
    } else { @{ Ids = @(); Containers = @() } }
    $global:LASTEXITCODE = 0
    if ($Arguments[1] -eq 'ls') {
        if ($fixture.InventoryFailure) { $global:LASTEXITCODE = 17; return }
        $fixture.Ids | Write-Output
        return
    }
    if ($Arguments[1] -eq 'inspect') {
        if ($fixture.InspectionFailure) { $global:LASTEXITCODE = 18; return }
        ConvertTo-Json -InputObject @($fixture.Containers) -Depth 12
        return
    }
    throw 'Unexpected container mutation attempted by the launcher.'
}
$keys = @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'CHAT_HOST_REDIS_PORT',
    'CHAT_REDIS_PASSWORD_FILE', 'CHAT_DATABASE_CONNECTION_FILE', 'CHAT_JWT_SIGNING_KEY_FILE',
    'Chat__Database__ConnectionString_FILE', 'Chat__Authentication__Base64SigningKey_FILE',
    'Chat__ServiceAuthentication__Ingress__Base64SigningKey_FILE',
    'Chat__Core__ServiceAuthentication__Base64SigningKey_FILE', 'Chat__Identity__ServiceAuthentication__Base64SigningKey_FILE',
    'Chat__Ai__ApiKey_FILE', 'Chat__Translation__ApiKey_FILE')
$settings = @{}
foreach ($key in $keys) { $settings[$key] = [Environment]::GetEnvironmentVariable($key) }
@{ arguments = @($Arguments); environment = $settings } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $env:CHAT_BOOTSTRAP_TEST_SINK -Encoding utf8
$global:LASTEXITCODE = [int]$env:CHAT_BOOTSTRAP_TEST_EXIT
'@ | Set-Content -LiteralPath $probe -Encoding utf8
function Get-Command {
    [CmdletBinding()]
    param([string]$Name, [object]$CommandType)
    if ($Name -in @('docker', 'dotnet')) { return [pscustomobject]@{ Source = $probe } }
    Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}
$environmentPattern = '^(?i:CHAT_|DOTNET_|ASPNETCORE_|AllowedHosts$|Logging__|SPRING_|SERVER_API_KEY$|OPENAI_API_KEY$|GOOGLE_API_KEY$|AI_SETTINGS_ENV_FILE$)'
$previous = @{}
foreach ($entry in Get-ChildItem Env: | Where-Object Name -match $environmentPattern) {
    $previous[$entry.Name] = $entry.Value
    [Environment]::SetEnvironmentVariable($entry.Name, [NullString]::Value)
}
try {
    $env:CHAT_BOOTSTRAP_TEST_SINK = Join-Path $output 'runtime-sink.json'
    $env:CHAT_BOOTSTRAP_TEST_EXIT = '0'
    $env:CHAT_BOOTSTRAP_TEST_DOCKER_CALLS = Join-Path $output 'docker-metadata-calls.jsonl'

    # 실행 / 검증 — 필수 manifest와 상대 경로가 실제 loader에서 해석된다.
    $fixture = New-Fixture 'valid'
    $c = Get-ChatDevelopmentConfiguration $fixture.Path
    Assert-Check ($c.Environment -ceq 'Development' -and $c.ChatPort -eq 15079) 'Development manifest parsed'
    Assert-Check ($c.SecretDirectory -ceq $fixture.SecretDirectory) 'Secret directory resolved relative to manifest'
    Assert-Check ($c.GeneratedDirectory -ceq (Join-Path $fixture.Directory 'generated')) 'Generated bindings resolved relative to manifest'
    $dry = @(& $initialize -ConfigurationFile $fixture.Path -DryRun)
    Assert-Check (@($dry | Where-Object { $_ -isnot [string] -and $_.Status -eq 'DEFERRED' }).Count -eq 4) 'Read-only preflight identifies four reusable issuer sources'
    Assert-Check (-not (Test-Path -LiteralPath $fixture.SecretDirectory) -and -not (Test-Path -LiteralPath $c.GeneratedDirectory)) 'Preflight creates no keys or bindings'
    $sourceBefore = @{ Be = [IO.File]::ReadAllText($fixture.BeSource); Ai = [IO.File]::ReadAllText($fixture.AiSource) }
    $applied = @(& $initialize -ConfigurationFile $fixture.Path)
    Assert-Check (@($applied | Where-Object { $_ -isnot [string] -and $_.Status -eq 'APPLIED' -and $_.Name -in $fixture.Values.Keys }).Count -eq 4) 'Missing canonical inputs copy four original issuer values'
    foreach ($name in $fixture.Values.Keys) {
        Assert-Check ([IO.File]::ReadAllText((Join-Path $fixture.SecretDirectory $name)) -ceq $fixture.Values[$name]) "Copied original bytes: $name"
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $fixture.SecretDirectory 'redis-password'))) 'Bootstrap does not invent existing Redis password'
    Supply-NonGeneratedFixtureInputs $fixture
    $keysBefore = Get-FileSnapshot $fixture.SecretDirectory
    $repeat = @(& $initialize -ConfigurationFile $fixture.Path)
    Assert-Snapshot $fixture.SecretDirectory $keysBefore 'Repeat bootstrap key bytes'
    Assert-Check ([IO.File]::ReadAllText($fixture.BeSource) -ceq $sourceBefore.Be -and [IO.File]::ReadAllText($fixture.AiSource) -ceq $sourceBefore.Ai) 'Original issuer files and LL setting remain unchanged'

    # 준비 / 실행 / 검증 — process의 기존 동일 방향 원본도 신규 생성보다 먼저 재사용한다.
    $environmentFixture = New-Fixture 'existing-process-source'
    $globalOnly = 'CHAT_AUTH_MODE=dedicated' + [Environment]::NewLine +
        'SERVER_API_KEY=synthetic-bootstrap-canary-global-independent' + [Environment]::NewLine
    [IO.File]::WriteAllText($environmentFixture.AiSource, $globalOnly, $utf8)
    $processKey = 'synthetic-bootstrap-canary-existing-process-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    [Environment]::SetEnvironmentVariable('CHAT_SERVER_API_KEY', $processKey)
    try {
        & $initialize -ConfigurationFile $environmentFixture.Path | Out-Null
        Assert-Check ([IO.File]::ReadAllText((Join-Path $environmentFixture.SecretDirectory 'chat-to-ai-api-key')) -ceq $processKey) 'Missing canonical AI file reuses inherited process source'
        Assert-Check ([IO.File]::ReadAllText($environmentFixture.AiSource) -ceq $globalOnly) 'Process source reuse does not rewrite AI dotenv file'
    } finally { [Environment]::SetEnvironmentVariable('CHAT_SERVER_API_KEY', [NullString]::Value) }

    # 실행 / 검증 — legacy 기본 모드에서는 전용 키가 있어도 기존 SERVER_API_KEY를 그대로 재사용한다.
    foreach ($modeLine in @('', 'CHAT_AUTH_MODE=legacy')) {
        $legacyFixture = New-Fixture ('legacy-' + [Guid]::NewGuid().ToString('N'))
        $legacyKey = 'synthetic-bootstrap-canary-legacy-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
        $legacySource = $modeLine + [Environment]::NewLine + 'SERVER_API_KEY=' + $legacyKey + [Environment]::NewLine +
            'CHAT_SERVER_API_KEY=' + $legacyFixture.Values['chat-to-ai-api-key'] + [Environment]::NewLine
        [IO.File]::WriteAllText($legacyFixture.AiSource, $legacySource, $utf8)
        & $initialize -ConfigurationFile $legacyFixture.Path | Out-Null
        Assert-Check ([IO.File]::ReadAllText((Join-Path $legacyFixture.SecretDirectory 'chat-to-ai-api-key')) -ceq $legacyKey) 'Legacy mode reuses global AI receiver key'
        Assert-Check ([IO.File]::ReadAllText($legacyFixture.AiSource) -ceq $legacySource) 'Legacy mode preserves original AI settings'
    }
    $environmentMismatch = New-Fixture 'process-source-mismatch'
    [Environment]::SetEnvironmentVariable('CHAT_GATEWAY_SECRETBASE64', [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    try {
        Assert-Refused 'Process and existing issuer mismatch rejected' { & $initialize -ConfigurationFile $environmentMismatch.Path } 'MISMATCH'
        Assert-Check (-not (Test-Path -LiteralPath $environmentMismatch.SecretDirectory) -and
            -not (Test-Path -LiteralPath (Join-Path $environmentMismatch.Directory 'generated'))) 'Process mismatch writes no canonical key or binding'
    } finally { [Environment]::SetEnvironmentVariable('CHAT_GATEWAY_SECRETBASE64', [NullString]::Value) }

    # 검증 — 양쪽 공유 원본, 서로 다른 방향과 모든 SQL logger OFF를 실제 생성 파일에서 확인한다.
    $jsonPath = Join-Path $c.GeneratedDirectory 'chat-environment.json'
    $bePath = Join-Path $c.GeneratedDirectory 'be.properties'
    $settings = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json -AsHashtable
    $beSettings = @{}
    foreach ($line in [IO.File]::ReadAllLines($bePath)) { $parts = $line -split '=', 2; $beSettings[$parts[0]] = $parts[1] }
    Assert-Check ($settings.Chat__ServiceAuthentication__Ingress__Base64SigningKey_FILE -ceq (Join-Path $fixture.SecretDirectory 'be-to-chat-service-signing-key')) 'BE-to-CHAT canonical ingress path'
    Assert-Check ($settings.Chat__Identity__ServiceAuthentication__Base64SigningKey_FILE -ceq (Join-Path $fixture.SecretDirectory 'chat-to-be-service-signing-key')) 'CHAT-to-BE canonical outbound path'
    Assert-Check ($settings.Chat__Core__ServiceAuthentication__Base64SigningKey_FILE -ceq $settings.Chat__Identity__ServiceAuthentication__Base64SigningKey_FILE) 'Identity and Core share one outbound original'
    Assert-Check ($settings.Chat__Ai__ApiKey_FILE -ceq (Join-Path $fixture.SecretDirectory 'chat-to-ai-api-key') -and $settings.Chat__Translation__ApiKey_FILE -ceq $settings.Chat__Ai__ApiKey_FILE) 'AI reply and translation share the AI receiver original'
    Assert-Check ($settings.Chat__Ai__Enabled -ceq 'false' -and $settings.Chat__Translation__Enabled -ceq 'false') 'Default AI and translation workers remain disabled as string options'
    $enabled = $c.Clone()
    $enabled.AiEnabled = $true
    $enabled.TranslationEnabled = $true
    $enabledBindings = (Get-ChatDevelopmentBindings $enabled).Chat | ConvertFrom-Json -AsHashtable
    Assert-Check ($enabledBindings.Chat__Ai__Enabled -ceq 'true' -and $enabledBindings.Chat__Translation__Enabled -ceq 'true') 'Explicit AI and translation flags map to true string options'
    Assert-Check ($beSettings['chat.gateway.base-url'] -ceq $settings.ASPNETCORE_URLS) 'BE gateway and CHAT bind address correspond'
    Assert-Check ($settings.Chat__Core__BaseUrl -ceq ('http://127.0.0.1:' + $beSettings['server.port'])) 'CHAT Core and BE bind address correspond'
    foreach ($logger in @('sqlonly', 'sqltiming', 'audit', 'resultset', 'resultsettable', 'connection')) {
        Assert-Check ($beSettings["logging.level.jdbc.$logger"] -ceq 'OFF') "BE concrete SQL logger disabled: $logger"
    }
    Assert-Check ($beSettings['spring.jpa.hibernate.ddl-auto'] -ceq 'none' -and $beSettings['spring.jpa.show-sql'] -ceq 'false') 'BE DDL and SQL display stay disabled'
    $bindingText = [IO.File]::ReadAllText($jsonPath) + [IO.File]::ReadAllText($bePath)
    foreach ($value in $fixture.Values.Values) { Assert-Check (-not $bindingText.Contains($value)) 'Generated binding contains no issuer secret value' }
    Assert-Check (-not $bindingText.Contains('language-learning.internal-jwt')) 'LL secret is not copied into CHAT bindings'

    # 실행 / 검증 — 실제 wrapper가 CHAT과 Redis에 설정을 전달하되 실행기는 테스트 sink만 사용한다.
    & $start -Service CHAT -ConfigurationFile $fixture.Path -NoBuild -ValidateOnly | Out-Null
    $probeResult = Get-Content -Raw -LiteralPath $env:CHAT_BOOTSTRAP_TEST_SINK | ConvertFrom-Json -AsHashtable
    Assert-Check ($probeResult.arguments -contains '--validate-configuration') 'CHAT wrapper requests actual configuration mode'
    Assert-Check ($probeResult.environment.Chat__Database__ConnectionString_FILE -ceq $settings.Chat__Database__ConnectionString_FILE) 'CHAT wrapper forwards runtime DB file path'
    Assert-Check ($probeResult.environment.Chat__Ai__ApiKey_FILE -ceq $probeResult.environment.Chat__Translation__ApiKey_FILE) 'CHAT wrapper keeps identical AI key source'
    Assert-Check ($null -eq [Environment]::GetEnvironmentVariable('DOTNET_ENVIRONMENT') -and
        $null -eq [Environment]::GetEnvironmentVariable('Chat__Ai__ApiKey_FILE')) 'CHAT wrapper removes originally absent process environment names'
    [IO.File]::WriteAllText($env:CHAT_BOOTSTRAP_TEST_SINK, '', $utf8)
    & $start -Service Redis -ConfigurationFile $fixture.Path -ValidateOnly | Out-Null
    $probeResult = Get-Content -Raw -LiteralPath $env:CHAT_BOOTSTRAP_TEST_SINK | ConvertFrom-Json -AsHashtable
    Assert-Check ($probeResult.arguments -contains 'config' -and $probeResult.arguments -contains '--quiet' -and $probeResult.arguments -notcontains 'up') 'Redis ValidateOnly uses Compose config without startup'
    Assert-Check ($probeResult.environment.CHAT_HOST_REDIS_PORT -ceq '16380') 'Redis wrapper passes manifest loopback host port'
    Assert-Check ($probeResult.environment.CHAT_REDIS_PASSWORD_FILE -ceq (Join-Path $fixture.SecretDirectory 'redis-password')) 'Redis wrapper passes existing password file only'
    Assert-Check ($null -eq [Environment]::GetEnvironmentVariable('CHAT_REDIS_PASSWORD_FILE') -and
        $null -eq [Environment]::GetEnvironmentVariable('DOTNET_ENVIRONMENT')) 'Redis wrapper removes originally absent process environment names'

    # 준비 / 실행 / 검증 — 기존 컨테이너의 AUTH/PING과 무관하게 실제 manifest metadata 일치를 먼저 요구한다.
    $metadata = @{
        Ids = @('a' * 64)
        Containers = @(@{
            Id = ('a' * 64); Name = '/translacat-chat-development-chat-redis-1'
            Config = @{ Labels = @{ 'com.docker.compose.project' = 'translacat-chat-development'; 'com.docker.compose.service' = 'chat-redis' }
                Env = @('CHAT_REDIS_NAMESPACE=translacat:chat:Development:synthetic') }
            HostConfig = @{ PortBindings = @{ '6379/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = '16380' }) } }
            Mounts = @(@{ Type = 'bind'; Source = (Join-Path $fixture.SecretDirectory 'redis-password'); Destination = '/run/secrets/chat_redis_password'; RW = $false })
            State = @{ Status = 'exited' }
        })
    }
    $metadataPath = Join-Path $output 'docker-metadata-fixture.json'
    $env:CHAT_BOOTSTRAP_TEST_DOCKER_METADATA = $metadataPath
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 12), $utf8)
    $existingRedis = @(& $redisStart -ConfigurationFile $fixture.Path -ValidateOnly)
    Assert-Check (($existingRedis -join '') -match '^VERIFIED:') 'Existing stopped Redis with matching metadata validates'
    if ($IsWindows) {
        $alternatePath = ($metadata | ConvertTo-Json -Depth 12) | ConvertFrom-Json -AsHashtable
        $alternatePath.Containers[0].Mounts[0].Source = $alternatePath.Containers[0].Mounts[0].Source.ToUpperInvariant().Replace('\', '/')
        [IO.File]::WriteAllText($metadataPath, ($alternatePath | ConvertTo-Json -Depth 12), $utf8)
        $caseResult = @(& $redisStart -ConfigurationFile $fixture.Path -ValidateOnly)
        Assert-Check (($caseResult -join '') -match '^VERIFIED:') 'Windows Docker mount path slash and case normalize without guessing another root'
    }
    $metadataCases = @(
        @{ Name = 'Redis project label mismatch'; Change = { param($m) $m.Containers[0].Config.Labels['com.docker.compose.project'] = 'other-project' } }
        @{ Name = 'Redis service label mismatch'; Change = { param($m) $m.Containers[0].Config.Labels['com.docker.compose.service'] = 'other-service' } }
        @{ Name = 'Redis namespace mismatch'; Change = { param($m) $m.Containers[0].Config.Env = @('CHAT_REDIS_NAMESPACE=translacat:chat:Development:old') } }
        @{ Name = 'Duplicate Redis namespace'; Change = { param($m) $m.Containers[0].Config.Env += $m.Containers[0].Config.Env[0] } }
        @{ Name = 'Redis published port mismatch'; Change = { param($m) $m.Containers[0].HostConfig.PortBindings['6379/tcp'][0].HostPort = '16381' } }
        @{ Name = 'Redis host is not loopback'; Change = { param($m) $m.Containers[0].HostConfig.PortBindings['6379/tcp'][0].HostIp = '0.0.0.0' } }
        @{ Name = 'Redis extra port binding'; Change = { param($m) $m.Containers[0].HostConfig.PortBindings['6380/tcp'] = @(@{ HostIp = '127.0.0.1'; HostPort = '16381' }) } }
        @{ Name = 'Redis secret source mismatch'; Change = { param($m) $m.Containers[0].Mounts[0].Source = [IO.Path]::Combine([IO.Path]::GetDirectoryName($m.Containers[0].Mounts[0].Source), 'synthetic-bootstrap-canary-wrong-source') } }
        @{ Name = 'Redis writable secret mount'; Change = { param($m) $m.Containers[0].Mounts[0].RW = $true } }
        @{ Name = 'Redis missing secret mount'; Change = { param($m) $m.Containers[0].Mounts = @() } }
        @{ Name = 'Redis duplicate candidate containers'; Change = { param($m) $m.Ids += ('b' * 64) } }
        @{ Name = 'Redis container ID differs from inventory'; Change = { param($m) $m.Containers[0].Id = ('b' * 64) } }
        @{ Name = 'Redis inventory command fails'; Change = { param($m) $m.InventoryFailure = $true } }
        @{ Name = 'Redis inspect command fails'; Change = { param($m) $m.InspectionFailure = $true } }
    )
    foreach ($case in $metadataCases) {
        $modified = ($metadata | ConvertTo-Json -Depth 12) | ConvertFrom-Json -AsHashtable
        & $case.Change $modified
        $originalMetadata = $modified | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($metadataPath, $originalMetadata, $utf8)
        Assert-Refused $case.Name { & $redisStart -ConfigurationFile $fixture.Path -ValidateOnly } 'MISMATCH'
        Assert-Check ([IO.File]::ReadAllText($metadataPath) -ceq $originalMetadata) "$($case.Name) leaves container fixture unchanged"
        Assert-Check ($null -eq [Environment]::GetEnvironmentVariable('CHAT_REDIS_PASSWORD_FILE')) "$($case.Name) restores process environment"
    }
    $metadataCalls = [IO.File]::ReadAllLines($env:CHAT_BOOTSTRAP_TEST_DOCKER_CALLS)
    Assert-Check (@($metadataCalls | Where-Object { (($_ | ConvertFrom-Json)[1]) -notin @('ls', 'inspect') }).Count -eq 0) 'All existing-container checks use read-only inventory or inspect'
    [Environment]::SetEnvironmentVariable('CHAT_BOOTSTRAP_TEST_DOCKER_METADATA', [NullString]::Value)

    # 실행 / 검증 — 실패한 Compose 검사와 기존 환경 충돌도 복원·무실행으로 끝난다.
    $env:CHAT_BOOTSTRAP_TEST_EXIT = '9'
    Assert-Refused 'Redis Compose failure surfaced' { & $redisStart -ConfigurationFile $fixture.Path -ValidateOnly } 'validation failed'
    Assert-Check ($null -eq [Environment]::GetEnvironmentVariable('CHAT_REDIS_PASSWORD_FILE')) 'Failed Redis validation removes originally absent environment names'
    $env:CHAT_BOOTSTRAP_TEST_EXIT = '0'
    $env:CHAT_REDIS_NAMESPACE = 'translacat:chat:Development:inherited'
    Assert-Refused 'Conflicting inherited Redis environment rejected' { & $redisStart -ConfigurationFile $fixture.Path -ValidateOnly } 'MISMATCH'
    Assert-Check ($env:CHAT_REDIS_NAMESPACE -ceq 'translacat:chat:Development:inherited') 'Rejected Redis launch preserves inherited value'
    [Environment]::SetEnvironmentVariable('CHAT_REDIS_NAMESPACE', [NullString]::Value)

    # 준비 / 실행 / 검증 — 합성 JAR은 파일 검사 전용이며 Java/Spring 실행을 주장하지 않는다.
    $jar = [IO.Compression.ZipFile]::Open($c.BeJarPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in @('TranslacatApplication', 'infrastructure/chat/gateway/ChatGatewayConfiguration',
            'infrastructure/chat/gateway/ChatGatewayDisabledSecurity', 'infrastructure/chat/core/ChatCoreIdentityController')) {
            $null = $jar.CreateEntry('BOOT-INF/classes/jp/co/translacat/' + $entry + '.class')
        }
    } finally { $jar.Dispose() }
    $beValidation = @(& $start -Service BE -ConfigurationFile $fixture.Path -NoBuild -ValidateOnly)
    Assert-Check (($beValidation -join '') -match '^VALID:') 'BE wrapper metadata and synthetic key-file validation passes without Java'
    if ($IncludeAiSettings) {
        # 실제 Python Settings만 검증한다. app/uvicorn/model warmup은 실행하지 않는다.
        @(Get-ChildItem Env: | Where-Object { $_.Name -like '*_FILE' } | ForEach-Object {
            @{ name = $_.Name; empty = [string]::IsNullOrEmpty($_.Value) }
        }) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'pre-ai-environment-names.json') -Encoding utf8
        $aiValidation = @(& $start -Service AI -ConfigurationFile $fixture.Path -ValidateOnly -DisableAiWarmup)
        $aiResult = ($aiValidation -join '') | ConvertFrom-Json
        Assert-Check ($aiResult.pythonSettings -ceq 'VERIFIED' -and $aiResult.chatAuthMode -ceq 'dedicated') 'AI wrapper reaches actual Python Settings using synthetic inputs'
        Assert-Check ($aiResult.warmupDisabled -eq $true) 'AI configuration verification requests no warmup'
    } else { $skipped.Add('AI actual Python Settings not requested; use -IncludeAiSettings') }

    # 실행 / 검증 — 양쪽 mismatch는 어떤 canonical key나 binding도 교체하지 않는다.
    $generatedBefore = Get-FileSnapshot $c.GeneratedDirectory
    [IO.File]::WriteAllText($fixture.BeSource, $sourceBefore.Be.Replace($fixture.Values['be-to-chat-service-signing-key'],
        [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))), $utf8)
    Assert-Refused 'Existing issuer mismatch rejected' { & $initialize -ConfigurationFile $fixture.Path } 'MISMATCH'
    Assert-Snapshot $fixture.SecretDirectory $keysBefore 'Mismatch canonical files'
    Assert-Snapshot $c.GeneratedDirectory $generatedBefore 'Mismatch generated files'
    [IO.File]::WriteAllText($fixture.BeSource, $sourceBefore.Be, $utf8)

    # 실행 / 검증 — 발급자의 trailing 공백을 조용히 제거하거나 키를 바꾸지 않는다.
    [IO.File]::WriteAllText($fixture.BeSource, $sourceBefore.Be.Replace($fixture.Values['be-to-chat-service-signing-key'],
        $fixture.Values['be-to-chat-service-signing-key'] + ' '), $utf8)
    Assert-Refused 'Existing Spring trailing space rejected' { & $initialize -ConfigurationFile $fixture.Path } 'NEEDS_INPUT'
    Assert-Snapshot $fixture.SecretDirectory $keysBefore 'Whitespace rejection canonical files'
    [IO.File]::WriteAllText($fixture.BeSource, $sourceBefore.Be, $utf8)

    # 실행 / 검증 — 사용자가 바꾼 생성 파일은 보존하고 manifest 변경 후 stale 실행을 거절한다.
    [IO.File]::AppendAllText($bePath, [Environment]::NewLine + '# externally edited synthetic binding', $utf8)
    $edited = [IO.File]::ReadAllText($bePath)
    Assert-Refused 'Externally edited generated binding rejected' { & $initialize -ConfigurationFile $fixture.Path } 'MISMATCH'
    Assert-Check ([IO.File]::ReadAllText($bePath) -ceq $edited) 'Externally edited binding is not overwritten'
    Assert-Refused 'Edited bindings block wrapper startup' { & $start -Service Redis -ConfigurationFile $fixture.Path -ValidateOnly } 'stale'
    [IO.File]::WriteAllText($bePath, (Get-ChatDevelopmentBindings $c).Be, $utf8)
    $fixture.Manifest.ChatPort = 15080
    Write-Manifest $fixture.Path $fixture.Manifest
    Assert-Refused 'Changed manifest blocks stale wrapper startup' { & $start -Service CHAT -ConfigurationFile $fixture.Path -ValidateOnly } 'stale'

    # 준비 / 실행 / 검증 — 설정명·형식·origin·port 충돌은 실제 loader가 거절한다.
    $invalidCases = @(
        @{ Name = 'Unknown setting'; Key = 'SecretValue'; Value = 'synthetic-bootstrap-canary' }
        @{ Name = 'Origin trailing slash'; Key = 'BrowserOrigin'; Value = 'http://localhost:13000/' }
        @{ Name = 'Duplicate app port'; Key = 'BePort'; Value = 15080 }
        @{ Name = 'Redis and app port collision'; Key = 'RedisEndpoint'; Value = '127.0.0.1:15080' }
        @{ Name = 'Production manifest'; Key = 'Environment'; Value = 'Production' }
        @{ Name = 'String port'; Key = 'ChatPort'; Value = '15080' }
        @{ Name = 'String AI flag'; Key = 'AiEnabled'; Value = 'false' }
        @{ Name = 'Remote browser origin'; Key = 'BrowserOrigin'; Value = 'https://synthetic.example.invalid' }
        @{ Name = 'Unsupported time zone'; Key = 'SourceTimeZone'; Value = 'synthetic-bootstrap-canary-invalid-zone' }
        @{ Name = 'Remote Redis endpoint'; Key = 'RedisEndpoint'; Value = '192.0.2.1:16380' }
    )
    foreach ($case in $invalidCases) {
        $invalidManifest = $fixture.Manifest.Clone()
        $invalidManifest[$case.Key] = $case.Value
        $invalidPath = Join-Path $output (($case.Name -replace ' ', '-') + '.json')
        Write-Manifest $invalidPath $invalidManifest
        Assert-Refused $case.Name { Get-ChatDevelopmentConfiguration $invalidPath } 'Invalid Development manifest'
    }
    $duplicate = Join-Path $output 'duplicate-property.json'
    $raw = $fixture.Manifest | ConvertTo-Json
    [IO.File]::WriteAllText($duplicate, $raw.Replace('"Environment": "Development"', '"Environment": "Development", "environment": "Development"'), $utf8)
    Assert-Refused 'Case-insensitive duplicate JSON property' { Get-ChatDevelopmentConfiguration $duplicate } 'Invalid Development manifest'
} finally {
    foreach ($entry in Get-ChildItem Env: | Where-Object Name -match $environmentPattern) { [Environment]::SetEnvironmentVariable($entry.Name, [NullString]::Value) }
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
@{ passed = $checks.Count; failed = 0; skipped = $skipped.Count; checks = @($checks); skippedChecks = @($skipped)
    realSecretsReadOrChanged = $false; providerCalls = 0; serversStarted = 0
    chatAndRedisExecution = 'TEST_ONLY_ARGUMENT_AND_ENVIRONMENT_SINK'; beExecution = 'SYNTHETIC_JAR_METADATA_ONLY_NO_JAVA'
    aiExecution = $(if ($IncludeAiSettings) { 'ACTUAL_PYTHON_SETTINGS_WITH_SYNTHETIC_FILES_NO_APP_START' } else { 'NOT_RUN' }) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'development-bootstrap-checks.json') -Encoding utf8
Write-Output "Development bootstrap checks passed: $($checks.Count); skipped: $($skipped.Count). Evidence: $output"
