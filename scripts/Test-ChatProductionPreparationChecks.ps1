param()
$ErrorActionPreference = 'Stop'
$chatRoot = Split-Path -Parent $PSScriptRoot
$checker = Join-Path $PSScriptRoot 'Test-ChatProductionPreparation.ps1'
$taskDirectory = Join-Path (Split-Path -Parent $chatRoot) '.codex-workspace/verification/chat/TestResults/ProductionPreparation'
$output = Join-Path $taskDirectory ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Synthetic Production preparation check failed: $Name" }
    $checks.Add($Name)
}

function Write-Fixture([hashtable]$Values, [string]$Suffix = '') {
    $path = Join-Path $output ([Guid]::NewGuid().ToString('N') + '.env')
    $lines = @($Values.GetEnumerator() | Sort-Object Key | ForEach-Object { $_.Key + '=' + $_.Value })
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n" + $Suffix), [Text.UTF8Encoding]::new($false))
    return $path
}

# 준비: 검사할 것은 file mount 구조다. 문자열은 실제 운영 credential 형식/발급/배포용 키가 아니다.
$fixture = Join-Path $output 'synthetic-presence-only-file'
[IO.File]::WriteAllText($fixture, 'synthetic-file-existence-check-only', [Text.UTF8Encoding]::new($false))
$baseline = @{
    DOTNET_ENVIRONMENT = 'Production'; ASPNETCORE_ENVIRONMENT = 'Production'; CHAT_API_PORT = '5085'
    CHAT_ALLOWED_HOSTS = 'chat.production.test'; CHAT_BROWSER_ORIGIN = 'https://web.production.test'
    CHAT_REDIS_NAMESPACE = 'translacat:chat:Production:synthetic'; CHAT_SOURCE_TIME_ZONE = 'Etc/UTC'
    CHAT_REDIS_PASSWORD_FILE = $fixture; CHAT_DATABASE_CONNECTION_FILE = $fixture; CHAT_JWT_SIGNING_KEY_FILE = $fixture
    CHAT_SERVICE_INGRESS_ENABLED = 'true'; CHAT_SERVICE_INGRESS_ISSUER = 'translacat-be'
    CHAT_SERVICE_INGRESS_AUDIENCE = 'translacat-chat'; CHAT_SERVICE_INGRESS_SERVICE = 'translacat-be'
    CHAT_SERVICE_INGRESS_KEY_FILE = $fixture
    CHAT_IDENTITY_ENABLED = 'true'; CHAT_IDENTITY_BASE_URL = 'https://be.production.test'
    CHAT_IDENTITY_TIMEOUT_SECONDS = '5'; CHAT_IDENTITY_ISSUER = 'translacat-chat'
    CHAT_IDENTITY_AUDIENCE = 'translacat-be'; CHAT_IDENTITY_SERVICE = 'translacat-chat'; CHAT_IDENTITY_KEY_FILE = $fixture
    CHAT_CORE_ENABLED = 'true'; CHAT_CORE_BASE_URL = 'https://be.production.test'; CHAT_CORE_TIMEOUT_SECONDS = '5'
    CHAT_CORE_ISSUER = 'translacat-chat'; CHAT_CORE_AUDIENCE = 'translacat-be'; CHAT_CORE_SERVICE = 'translacat-chat'
    CHAT_AI_ENABLED = 'true'; CHAT_TRANSLATION_ENABLED = 'true'
    CHAT_AI_BASE_URL = 'https://ai.production.test'; CHAT_AI_API_KEY_FILE = $fixture
}
$previous = @{}
foreach ($item in Get-ChildItem Env: | Where-Object { $_.Name -like 'CHAT_*' -or $_.Name -in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT') }) {
    $previous[$item.Name] = $item.Value
    [Environment]::SetEnvironmentVariable($item.Name, $null)
}
try {
    # 실행 / 검증: 공개 예제는 미공급 상태이고 완전한 합성 입력은 구조만 통과한다.
    $example = & $checker -PassThru
    Assert-Check (-not $example.Ready -and $example.Status -eq 'NEEDS_INPUT' -and
        @($example.Checks | Where-Object Status -eq 'NEEDS_INPUT').Count -ge 6) 'Unfilled public example requires inputs'
    $goodPath = Write-Fixture $baseline
    $good = & $checker -EnvironmentFile $goodPath -PassThru
    Assert-Check ($good.Ready -and $good.Status -eq 'VERIFIED' -and
        @($good.Checks | Where-Object Status -ne 'VERIFIED').Count -eq 0 -and
        @($good.Checks | Where-Object Reason -eq 'FILE_PRESENT_CONTENT_NOT_VERIFIED').Count -eq 6 -and
        -not $good.RuntimeBindingVerified -and -not $good.SecretContentOrPermissionsVerified) 'Synthetic complete structure is explicit about unverified runtime and secret content'
    Assert-Check ($good.ProductionKeysGenerated -eq 0 -and $good.ExternalCalls -eq 0) 'No production key issuance or external call'
    $inheritedCore = $baseline.Clone()
    foreach ($suffix in @('BASE_URL', 'ISSUER', 'AUDIENCE', 'SERVICE')) { $inheritedCore["CHAT_CORE_$suffix"] = '' }
    $result = & $checker -EnvironmentFile (Write-Fixture $inheritedCore) -PassThru
    Assert-Check $result.Ready 'Current Core-to-Identity Compose fallback is preserved'

    foreach ($case in @(
        @('DOTNET_ENVIRONMENT', 'Development'), @('ASPNETCORE_ENVIRONMENT', 'prod'),
        @('CHAT_API_PORT', '80'), @('CHAT_ALLOWED_HOSTS', '*'), @('CHAT_ALLOWED_HOSTS', 'localhost'),
        @('CHAT_BROWSER_ORIGIN', 'http://web.production.test'), @('CHAT_BROWSER_ORIGIN', 'https://web.production.test/'),
        @('CHAT_BROWSER_ORIGIN', 'https://localhost'), @('CHAT_REDIS_NAMESPACE', 'translacat:chat:Development'),
        @('CHAT_SOURCE_TIME_ZONE', 'not-a-zone'), @('CHAT_IDENTITY_BASE_URL', 'http://be.production.test'),
        @('CHAT_AI_BASE_URL', 'https://replace-with-host'), @('CHAT_CORE_TIMEOUT_SECONDS', '31'),
        @('CHAT_AI_ENABLED', 'yes'), @('CHAT_IDENTITY_ISSUER', ''),
        @('CHAT_DATABASE_CONNECTION_FILE', 'relative-secret'), @('CHAT_JWT_SIGNING_KEY_FILE', $output),
        @('CHAT_AI_API_KEY_FILE', (Join-Path $output 'missing')), @('CHAT_REDIS_PASSWORD_FILE', '')
    )) {
        $values = $baseline.Clone()
        $values[$case[0]] = $case[1]
        $path = Write-Fixture $values
        $result = & $checker -EnvironmentFile $path -PassThru
        Assert-Check (-not $result.Ready -and $result.Status -in @('NEEDS_INPUT', 'MISMATCH') -and
            @($result.Checks | Where-Object { $_.Status -notin @('VERIFIED', 'NEEDS_INPUT', 'MISMATCH') -or -not $_.Reason }).Count -eq 0) ('Reject invalid ' + $case[0] + ' case')
    }

    foreach ($suffix in @(
        'CHAT_UNKNOWN_FILE=/synthetic/missing',
        'CHAT_AI_API_KEY=synthetic-inline-never-print',
        'CHAT_API_PORT=5090',
        'CHAT_ALLOWED_HOSTS=${SUPPLY_HOST}',
        'lowercase_name=synthetic-inline-never-print'
    )) {
        $path = Write-Fixture $baseline $suffix
        $result = & $checker -EnvironmentFile $path -PassThru
        Assert-Check (-not $result.Ready) 'Unknown, inline, duplicate or unresolved setting is rejected'
        Assert-Check (($result | ConvertTo-Json -Depth 5) -notmatch 'synthetic-inline-never-print|SUPPLY_HOST') 'Diagnostics do not echo supplied values'
    }

    # 실행 / 검증: 상속 환경도 실제 입력과 다르면 거절하며 checker가 수정하지 않는다.
    $env:DOTNET_ENVIRONMENT = 'Development'
    $result = & $checker -EnvironmentFile $goodPath -PassThru
    Assert-Check (-not $result.Ready -and $result.Status -eq 'MISMATCH' -and
        @($result.Checks | Where-Object { $_.Status -eq 'MISMATCH' -and $_.Reason -eq 'MISMATCH_PROCESS_ENVIRONMENT' }).Count -gt 0 -and
        $env:DOTNET_ENVIRONMENT -ceq 'Development') 'Inherited environment mismatch is preserved and rejected'
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', $null)
    $env:CHAT_UNREGISTERED_FILE = '/synthetic/never-read'
    $result = & $checker -EnvironmentFile $goodPath -PassThru
    Assert-Check (-not $result.Ready) 'Unknown inherited _FILE is rejected'
    [Environment]::SetEnvironmentVariable('CHAT_UNREGISTERED_FILE', $null)

    $before = [IO.File]::ReadAllBytes($fixture)
    $null = & $checker -EnvironmentFile $goodPath -PassThru
    Assert-Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture)) -ceq [Convert]::ToBase64String($before)) 'Read-only checker preserves every supplied file'
} finally {
    foreach ($name in @('DOTNET_ENVIRONMENT', 'CHAT_UNREGISTERED_FILE')) { [Environment]::SetEnvironmentVariable($name, $null) }
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}

@{ passed = $checks.Count; failed = 0; skipped = 0; productionKeysGenerated = 0; externalCalls = 0; positiveFixtureFile = $goodPath; checks = @($checks) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
Write-Output "Production preparation synthetic checks passed: $($checks.Count). Evidence: $output"
