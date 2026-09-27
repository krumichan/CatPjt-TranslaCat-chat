param(
    [string]$ConfigurationFile = (Join-Path $PSScriptRoot '../deploy/local/Development.json'),
    [switch]$CheckOnly,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChatDevelopmentConfiguration.ps1')
$configuration = Get-ChatDevelopmentConfiguration $ConfigurationFile
$secretDirectory = $configuration.SecretDirectory
$readOnly = $CheckOnly -or $DryRun
$existingValues = @{}

function Read-LiteralProperty([string]$Path, [string]$Name) {
    # 기존 Spring secret 파일은 필요한 항목만 메모리에서 비교한다. 복잡한 escape/placeholder를 추측하지 않는다.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $pattern = '^\s*' + [regex]::Escape($Name) + '\s*[:=][ \t]*(.*)$'
    $matchesFound = @([IO.File]::ReadAllLines($Path) | Where-Object { $_ -match $pattern })
    if ($matchesFound.Count -gt 1) { throw "MISMATCH: duplicate existing setting $Name." }
    if ($matchesFound.Count -eq 0) { return $null }
    $null = $matchesFound[0] -match $pattern
    $value = $Matches[1]
    if ($value.Contains('\') -or $value.Contains('$') -or $value -match '\s') {
        throw "NEEDS_INPUT: existing setting $Name requires its current issuer's resolved value. Nothing was replaced."
    }
    return $value
}

function Assert-ExistingPair([string]$Name, [string]$Existing) {
    if ([string]::IsNullOrEmpty($Existing)) { return }
    $path = Join-Path $secretDirectory $Name
    if ($existingValues.ContainsKey($Name) -and $existingValues[$Name] -cne $Existing) {
        throw "MISMATCH: existing sources for $Name disagree. No source was replaced."
    }
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and [IO.File]::ReadAllText($path) -cne $Existing) {
        throw "MISMATCH: $Name differs from its existing consumer. Existing values were preserved."
    }
    # 기존 원본만 있고 수신 파일이 없으면 생성 도구의 보호된 원자적 쓰기로 같은 값을 공급한다.
    $existingValues[$Name] = $Existing
}

# 파일 외에 현재 process가 이미 공급한 방향키도 신규 발급 전 확인한다. 이 값을 출력/전역 수정하지 않는다.
$environmentPairs = @{
    CHAT_GATEWAY_SECRETBASE64 = 'be-to-chat-service-signing-key'
    CHAT_CORE_IDENTITY_SECRETBASE64 = 'chat-to-be-service-signing-key'
    CHAT_SERVER_API_KEY = 'chat-to-ai-api-key'
    Chat__ServiceAuthentication__Ingress__Base64SigningKey = 'be-to-chat-service-signing-key'
    Chat__Identity__ServiceAuthentication__Base64SigningKey = 'chat-to-be-service-signing-key'
    Chat__Core__ServiceAuthentication__Base64SigningKey = 'chat-to-be-service-signing-key'
    Chat__Ai__ApiKey = 'chat-to-ai-api-key'
    Chat__Translation__ApiKey = 'chat-to-ai-api-key'
    Chat__Authentication__Base64SigningKey = 'be-user-jwt-signing-key'
}
foreach ($name in $environmentPairs.Keys) {
    Assert-ExistingPair $environmentPairs[$name] ([Environment]::GetEnvironmentVariable($name))
}

# 생성 전에 기존 발급자/수신자와 충돌 여부를 확인한다. LL key는 읽거나 CHAT으로 복사하지 않는다.
Assert-ExistingPair 'be-to-chat-service-signing-key' (Read-LiteralProperty $configuration.BeLocalSecretsFile 'chat.gateway.secret-base64')
Assert-ExistingPair 'chat-to-be-service-signing-key' (Read-LiteralProperty $configuration.BeLocalSecretsFile 'chat.core.identity.secret-base64')
Assert-ExistingPair 'be-user-jwt-signing-key' (Read-LiteralProperty $configuration.BeLocalSecretsFile 'jwt.token.secret-key')
if (Test-Path -LiteralPath $configuration.AiEnvironmentFile -PathType Leaf) {
    $lines = @([IO.File]::ReadAllLines($configuration.AiEnvironmentFile) | Where-Object { $_ -match '^\s*(?:export\s+)?CHAT_SERVER_API_KEY\s*=' })
    if ($lines.Count -gt 1) { throw 'MISMATCH: duplicate CHAT_SERVER_API_KEY in the existing AI environment file.' }
    if ($lines.Count -eq 1) {
        # dotenv의 일반적인 따옴표 한 쌍만 허용한다. escape/interpolation은 Python preflight에서 별도 검사한다.
        $value = ($lines[0] -split '=', 2)[1].Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        Assert-ExistingPair 'chat-to-ai-api-key' $value
    }
}

# 누락 신규 방향키만 준비한다. Redis/DB/사용자 JWT는 임의 난수로 대체하지 않는다.
& (Join-Path $PSScriptRoot 'New-ChatDevelopmentKeys.ps1') -Environment Development -OutputDirectory $secretDirectory -DryRun:$readOnly -ExistingValues $existingValues
$ready = $true
foreach ($name in @('chat-database-runtime', 'chat-database-schema')) {
    $path = Join-Path $secretDirectory $name
    $exists = Test-Path -LiteralPath $path -PathType Leaf
    [pscustomobject]@{ Name = $name; Status = $(if ($exists) { 'REUSED' } else { 'NEEDS_INPUT' }); Path = $path }
    if (-not $exists -and $name -ne 'chat-database-schema') { $ready = $false }
}
foreach ($name in @('BeLocalSecretsFile', 'AiEnvironmentFile')) {
    $exists = Test-Path -LiteralPath $configuration[$name] -PathType Leaf
    [pscustomobject]@{ Name = $name; Status = $(if ($exists) { 'REUSED' } else { 'NEEDS_INPUT' }); Path = $configuration[$name] }
    if (-not $exists) { $ready = $false }
}
foreach ($name in @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'chat-to-ai-api-key', 'redis-password', 'be-user-jwt-signing-key')) {
    if (-not (Test-Path -LiteralPath (Join-Path $secretDirectory $name) -PathType Leaf)) { $ready = $false }
}

$bindings = Get-ChatDevelopmentBindings $configuration
if ($readOnly) {
    Write-Output 'VERIFIED: read-only preflight finished; no files, processes or server data changed. Run without -CheckOnly/-DryRun to apply nonsecret bindings.'
    return
}

# 생성된 파일은 비밀 원문 없이 기존 런처가 직접 소비하는 형식이다. 사용자가 편집한 산출물은 덮어쓰지 않는다.
$output = $configuration.GeneratedDirectory
$statePath = Join-Path $output 'bindings-state.json'
$state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$files = @{ 'chat-environment.json' = $bindings.Chat; 'be.properties' = $bindings.Be }
foreach ($name in $files.Keys) {
    $path = Join-Path $output $name
    if (Test-Path -LiteralPath $path) {
        if (-not $state.ContainsKey($name) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $state[$name]) {
            throw 'MISMATCH: generated nonsecret bindings were edited externally. Preserve them and resolve the changes before applying the manifest.'
        }
    }
}
$null = New-Item -ItemType Directory -Path $output -Force
foreach ($name in $files.Keys) {
    $path = Join-Path $output $name
    $temporary = Join-Path $output ([Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, $files[$name], [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path, $true)
    $state[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [pscustomobject]@{ Name = $name; Status = 'APPLIED'; Path = $path }
}
[IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if (-not $ready) { Write-Output 'NEEDS_INPUT: bindings applied; supply the named existing credentials before starting services.' }
else { Write-Output 'APPLIED: same original files supply both directions. Run Start-ChatDevelopment.ps1 with -ValidateOnly for actual runtime binding checks.' }
