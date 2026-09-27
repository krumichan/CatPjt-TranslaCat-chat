param([string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$centralRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/'))
if ([string]::IsNullOrWhiteSpace($ResultDirectory)) {
    $ResultDirectory = Join-Path $centralRoot "chat/TestResults/LocalLaunch/$([Guid]::NewGuid().ToString('N'))"
}
$output = [IO.Path]::GetFullPath($ResultDirectory)
if (-not $output.StartsWith($centralRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw 'Local launch tests require a new directory inside central verification.'
}
$null = New-Item -ItemType Directory -Path $output
$launcher = Join-Path $PSScriptRoot 'Start-ChatLocal.ps1'
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Local launch check failed: $Name" }
    $checks.Add($Name)
}

function Write-Settings([string]$Name, [object]$Settings) {
    $path = Join-Path $output "$Name.json"
    $Settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Assert-Refused([string]$Name, [string]$Path) {
    # 실행 / 검증 — 거절 사유가 입력 원문을 되풀이하지 않는지도 확인한다.
    $refused = $false
    try { & $launcher -Environment Development -Port 8123 -ConfigurationFile $Path -ValidateOnly | Out-Null }
    catch {
        $refused = $true
        Assert-Check (-not $_.Exception.Message.Contains('synthetic-secret-canary')) "$Name diagnostic redaction"
    }
    Assert-Check $refused $Name
}

# 준비 — 실제 dotnet 대신 테스트 전용 실행 sink를 사용한다. 서버, DB, Redis와 secret 원문을 읽지 않는다.
$probePath = Join-Path $output 'launch-probe.ps1'
@'
param([Parameter(ValueFromRemainingArguments)][string[]]$ProbeArguments)
@{
    arguments = @($ProbeArguments)
    environment = @{
        DOTNET_ENVIRONMENT = $env:DOTNET_ENVIRONMENT
        ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
        ASPNETCORE_URLS = $env:ASPNETCORE_URLS
        AllowedHosts = $env:AllowedHosts
        Chat__Core__BaseUrl = $env:Chat__Core__BaseUrl
        Chat__Database__ConnectionString_FILE = $env:Chat__Database__ConnectionString_FILE
        Logging__LogLevel__Default = $env:Logging__LogLevel__Default
    }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:CHAT_LAUNCH_TEST_OUTPUT -Encoding utf8
$global:LASTEXITCODE = [int]$env:CHAT_LAUNCH_TEST_EXIT
'@ | Set-Content -LiteralPath $probePath -Encoding utf8

function Get-Command {
    [CmdletBinding()]
    param([string]$Name, [object]$CommandType)
    if ($Name -eq 'dotnet') { return [pscustomobject]@{ Source = $probePath } }
    Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}

$inaccessibleProbePath = Join-Path $output 'synthetic-secret-canary-denied'
function Test-Path {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$PathType)
    if ($LiteralPath -ceq $inaccessibleProbePath) { throw "Access denied: $inaccessibleProbePath" }
    Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
}

$names = @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'AllowedHosts',
    'Chat__Core__BaseUrl', 'Chat__Database__ConnectionString_FILE', 'Chat__Database__ConnectionString', 'Logging__LogLevel__Default',
    'CHAT_LAUNCH_TEST_OUTPUT', 'CHAT_LAUNCH_TEST_EXIT')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
    $env:CHAT_LAUNCH_TEST_OUTPUT = Join-Path $output 'probe-result.json'
    $env:CHAT_LAUNCH_TEST_EXIT = '0'
    $secretFile = Join-Path $output 'synthetic-secret-file'
    'synthetic-secret-canary' | Set-Content -LiteralPath $secretFile -Encoding utf8
    $valid = Write-Settings 'valid' @{
        DOTNET_ENVIRONMENT = 'Development'; ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = 'http://127.0.0.1:8123'; AllowedHosts = '127.0.0.1'
        Chat__Core__BaseUrl = 'http://127.0.0.1:18181'
        Chat__Database__ConnectionString_FILE = $secretFile
        Logging__LogLevel__Default = 'Warning'
    }

    # 실행 / 검증 — validation-only는 환경 주입이나 하위 실행을 하지 않는다.
    & $launcher -Environment Development -Port 8123 -ConfigurationFile $valid -ValidateOnly | Out-Null
    Assert-Check (-not (Test-Path -LiteralPath $env:CHAT_LAUNCH_TEST_OUTPUT)) 'ValidateOnly starts no process'
    Assert-Check ([string]::IsNullOrEmpty($env:DOTNET_ENVIRONMENT) -and [string]::IsNullOrEmpty($env:Chat__Core__BaseUrl)) 'ValidateOnly preserves process environment'

    # 실행 — 합성 sink가 받은 환경과 실제 run 인자를 확인한다.
    $env:AllowedHosts = 'prior.example.invalid'
    $env:Chat__Core__BaseUrl = 'http://127.0.0.1:18180'
    & $launcher -Environment Development -Port 8123 -ConfigurationFile $valid -NoBuild | Out-Null
    $probe = Get-Content -LiteralPath $env:CHAT_LAUNCH_TEST_OUTPUT -Raw | ConvertFrom-Json

    # 검증 — 파일 경로 전달, loopback 바인딩과 성공 후 원래 환경 복원.
    Assert-Check ($probe.environment.DOTNET_ENVIRONMENT -ceq 'Development' -and $probe.environment.ASPNETCORE_ENVIRONMENT -ceq 'Development') 'Development environment reaches child'
    Assert-Check ($probe.environment.Chat__Core__BaseUrl -ceq 'http://127.0.0.1:18181') 'Configuration reaches child'
    Assert-Check ($probe.environment.Chat__Database__ConnectionString_FILE -ceq $secretFile) 'Only secret path reaches child'
    Assert-Check ($probe.environment.Logging__LogLevel__Default -ceq 'Warning') 'Logging setting reaches child'
    Assert-Check ($probe.arguments -contains '--no-build' -and $probe.arguments -contains '--no-restore' -and $probe.arguments -contains '--no-launch-profile') 'Prebuilt launch preserves SDK flags'
    Assert-Check ($probe.arguments -contains '--urls' -and $probe.arguments -contains 'http://127.0.0.1:8123') 'Launch binds requested loopback port'
    Assert-Check ($env:AllowedHosts -ceq 'prior.example.invalid' -and $env:Chat__Core__BaseUrl -ceq 'http://127.0.0.1:18180') 'Success restores existing settings'
    Assert-Check ([string]::IsNullOrEmpty($env:DOTNET_ENVIRONMENT) -and [string]::IsNullOrEmpty($env:Chat__Database__ConnectionString_FILE)) 'Success removes newly supplied settings'
    Assert-Check (-not [Environment]::GetEnvironmentVariables().Contains('Chat__Database__ConnectionString_FILE')) 'Success removes the secret environment name itself'

    # 실행 / 검증 — 파일 없는 기존 User Secrets 경로와 실패 복원도 유지한다.
    & $launcher -Environment Development -Port 8123 | Out-Null
    $probe = Get-Content -LiteralPath $env:CHAT_LAUNCH_TEST_OUTPUT -Raw | ConvertFrom-Json
    Assert-Check ($probe.arguments -notcontains '--no-build') 'Default launch still builds and permits standard User Secrets'
    $env:CHAT_LAUNCH_TEST_EXIT = '17'
    $refused = $false
    try { & $launcher -Environment Development -Port 8123 -ConfigurationFile $valid | Out-Null }
    catch { $refused = $_.Exception.Message -eq 'Local CHAT process failed with exit code 17.' }
    Assert-Check $refused 'Child failure is reported'
    Assert-Check ($env:AllowedHosts -ceq 'prior.example.invalid' -and [string]::IsNullOrEmpty($env:DOTNET_ENVIRONMENT)) 'Child failure restores process environment'
    $env:CHAT_LAUNCH_TEST_EXIT = '0'

    # 실행 / 검증 — 기존 비밀 공급원을 덮어쓰지 않고 validation-only 단계에서도 충돌을 거절한다.
    $env:Chat__Database__ConnectionString = 'synthetic-secret-canary'
    Assert-Refused 'Inherited raw secret collision rejected' $valid
    $env:Chat__Database__ConnectionString = $null
    $env:Chat__Database__ConnectionString_FILE = Join-Path $output 'other-secret'
    Assert-Refused 'Inherited different secret file rejected' $valid
    $env:Chat__Database__ConnectionString_FILE = $secretFile
    & $launcher -Environment Development -Port 8123 -ConfigurationFile $valid -ValidateOnly | Out-Null
    Assert-Check ($env:Chat__Database__ConnectionString_FILE -ceq $secretFile) 'Identical secret file source is preserved'
    $env:Chat__Database__ConnectionString_FILE = $null

    # 준비 / 실행 / 검증 — 비밀 원문, 임의 환경, 잘못된 JSON/파일/환경은 실행 전에 거절한다.
    foreach ($name in @('Chat__Database__ConnectionString', 'Chat__Redis__ConnectionString', 'Chat__Redis__Password',
        'Chat__Authentication__Base64SigningKey', 'Chat__ServiceAuthentication__Ingress__Base64SigningKey',
        'Chat__Identity__ServiceAuthentication__Base64SigningKey', 'Chat__Core__ServiceAuthentication__Base64SigningKey',
        'Chat__Ai__ApiKey', 'Chat__Translation__ApiKey')) {
        Assert-Refused "Raw secret rejected: $name" (Write-Settings ('raw-' + $checks.Count) @{ $name = 'synthetic-secret-canary' })
    }
    Assert-Refused 'Unknown process setting rejected' (Write-Settings 'unknown' @{ PATH = 'synthetic-secret-canary' })
    Assert-Refused 'Unknown CHAT option rejected' (Write-Settings 'unknown-chat' @{ Chat__Testing__UserId = '1' })
    Assert-Refused 'Nested JSON rejected' (Write-Settings 'nested' @{ Chat = @{ Presence = @{ Enabled = $true } } })
    Assert-Refused 'Non-string value rejected' (Write-Settings 'non-string' @{ Chat__Presence__Enabled = $true })
    Assert-Refused 'Production JSON rejected' (Write-Settings 'production' @{ DOTNET_ENVIRONMENT = 'Production' })
    Assert-Refused 'Environment alias rejected' (Write-Settings 'alias' @{ ASPNETCORE_ENVIRONMENT = 'local' })
    Assert-Refused 'Non-loopback URL rejected' (Write-Settings 'non-loopback' @{ ASPNETCORE_URLS = 'http://0.0.0.0:8123' })
    Assert-Refused 'Mismatched port rejected' (Write-Settings 'port' @{ ASPNETCORE_URLS = 'http://127.0.0.1:8124' })
    Assert-Refused 'Relative secret path rejected' (Write-Settings 'relative-secret' @{ Chat__Ai__ApiKey_FILE = 'synthetic-secret-file' })
    Assert-Refused 'Missing secret file rejected' (Write-Settings 'missing-secret' @{ Chat__Ai__ApiKey_FILE = (Join-Path $output 'absent') })
    Assert-Refused 'Inaccessible secret path diagnostics sanitized' (Write-Settings 'denied-secret' @{ Chat__Ai__ApiKey_FILE = $inaccessibleProbePath })
    Assert-Refused 'Relative configuration path rejected' 'relative-config.json'
    $duplicate = Join-Path $output 'duplicate.json'
    '{"Chat__Presence__Enabled":"false","chat__presence__enabled":"true"}' | Set-Content -LiteralPath $duplicate -Encoding utf8
    Assert-Refused 'Case-insensitive duplicate settings rejected' $duplicate
    $malformed = Join-Path $output 'malformed.json'
    '{synthetic-secret-canary' | Set-Content -LiteralPath $malformed -Encoding utf8
    Assert-Refused 'Malformed JSON rejected' $malformed
    $oversized = Join-Path $output 'oversized.json'
    (' ' * 65537) | Set-Content -LiteralPath $oversized -Encoding utf8
    Assert-Refused 'Oversized JSON rejected' $oversized
    $env:DOTNET_ENVIRONMENT = 'Production'
    Assert-Refused 'Inherited Production environment rejected' $valid
    Assert-Check ($env:DOTNET_ENVIRONMENT -ceq 'Production') 'Rejected launch preserves inherited environment'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}

# 검증 결과는 이름과 개수만 남긴다. 이 검사는 실제 서버 startup이나 Options 유효성을 대신하지 않는다.
@{ passed = $checks.Count; failed = 0; checks = @($checks); serverStarted = $false; processSink = 'TEST_ONLY'; secretContentsRead = $false } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'local-launch-checks.json') -Encoding utf8
Write-Output "Local launch checks passed: $($checks.Count). Evidence: $output"
