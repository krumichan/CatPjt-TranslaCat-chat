param(
    [Parameter(Mandatory)][ValidateSet('Development')][string]$Environment,
    [ValidateRange(1024, 65535)][int]$Port = 5079,
    [string]$ConfigurationFile,
    [switch]$NoBuild,
    [switch]$ValidateOnly,
    [switch]$ValidateConfiguration
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$project = Join-Path $workspace 'TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj'
$address = "http://127.0.0.1:$Port"

# JSON에는 실제 Options와 비밀 파일 경로만 허용한다. 임의 process 환경이나 raw secret은 받지 않는다.
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    'DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'AllowedHosts',
    'Chat__SourceTimeZone', 'Chat__Database__ConnectionString_FILE',
    'Chat__Authentication__Enabled', 'Chat__Authentication__Base64SigningKey_FILE',
    'Chat__Redis__ConnectionString_FILE', 'Chat__Redis__Endpoint', 'Chat__Redis__User',
    'Chat__Redis__Password_FILE', 'Chat__Redis__UseTls', 'Chat__Redis__AllowPrivatePlaintext', 'Chat__Redis__Namespace',
    'Chat__Presence__Enabled', 'Chat__Presence__SessionTtl', 'Chat__Presence__RefreshInterval', 'Chat__Presence__OfflineGrace',
    'Chat__Realtime__ConnectTimeout', 'Chat__Realtime__KeepAliveInterval'
)) { $null = $allowed.Add($name) }

foreach ($section in @('ServiceAuthentication__Ingress', 'Identity__ServiceAuthentication', 'Core__ServiceAuthentication')) {
    foreach ($name in @('Issuer', 'Audience', 'Service', 'Base64SigningKey_FILE')) {
        $null = $allowed.Add("Chat__${section}__$name")
    }
}
$null = $allowed.Add('Chat__ServiceAuthentication__Ingress__Enabled')
foreach ($section in @('Identity', 'Core')) {
    foreach ($name in @('Enabled', 'BaseUrl', 'TimeoutSeconds')) { $null = $allowed.Add("Chat__${section}__$name") }
}
foreach ($section in @('Ai', 'Translation')) {
    foreach ($name in @('Enabled', 'AiBaseUri', 'ApiKey_FILE', 'AttemptTimeout', 'ConnectTimeout', 'MaximumAttempts', 'RetryWait')) {
        $null = $allowed.Add("Chat__${section}__$name")
    }
}
foreach ($name in @('BatchTimeZone', 'RevivalLimit', 'RevivalInitialDelay', 'RevivalInterval', 'RevivalClaimTimeoutSeconds', 'RevivalFailureRetryMinutes')) {
    $null = $allowed.Add("Chat__Ai__$name")
}
foreach ($name in @('LeaseDuration', 'RetryLimit', 'SweepInterval', 'InitialDelay')) {
    $null = $allowed.Add("Chat__Translation__$name")
}

$settings = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($PSBoundParameters.ContainsKey('ConfigurationFile')) {
    if ([string]::IsNullOrWhiteSpace($ConfigurationFile) -or -not [IO.Path]::IsPathFullyQualified($ConfigurationFile)) {
        throw 'CHAT configuration requires an absolute JSON file path.'
    }

    # 파싱 오류에도 JSON 원문이나 secret 경로를 진단에 포함하지 않는다.
    $document = $null
    try {
        $file = Get-Item -LiteralPath $ConfigurationFile -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -gt 65536) { throw 'Invalid file.' }
        $json = [IO.File]::ReadAllText($file.FullName, [Text.UTF8Encoding]::new($false, $true))
        $document = [Text.Json.JsonDocument]::Parse($json)
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Invalid root.' }
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String -or $settings.ContainsKey($property.Name)) {
                throw 'Invalid or duplicate setting.'
            }
            $settings.Add($property.Name, $property.Value.GetString())
        }
    } catch {
        throw 'CHAT configuration must be a readable UTF-8 JSON object of unique string settings, at most 64 KiB.'
    } finally {
        if ($null -ne $document) { $document.Dispose() }
        $json = $null
    }

    foreach ($setting in $settings.GetEnumerator()) {
        $name = $setting.Key
        $value = $setting.Value
        $indexedOrigin = $name -cmatch '^Chat__Realtime__AllowedOrigins__(0|[1-9][0-9]*)$'
        $loggingLevel = $name -cmatch '^Logging__LogLevel__[A-Za-z0-9_.+-]+$'
        if (-not $allowed.Contains($name) -and -not $indexedOrigin -and -not $loggingLevel) {
            throw 'CHAT configuration contains an unsupported setting. Secrets require a supported _FILE setting.'
        }
        if ($value.IndexOf([char]0) -ge 0) { throw 'CHAT configuration values cannot contain a null character.' }
        if ($name.EndsWith('_FILE', [StringComparison]::OrdinalIgnoreCase)) {
            $exists = $false
            try {
                if (-not [string]::IsNullOrWhiteSpace($value) -and [IO.Path]::IsPathFullyQualified($value)) {
                    $exists = Test-Path -LiteralPath $value -PathType Leaf -ErrorAction Stop
                }
            } catch {
                # 접근 거부나 잘못된 경로에도 비밀 파일의 실제 위치를 오류에 되풀이하지 않는다.
                throw 'CHAT secret-file settings require an accessible existing absolute file path.'
            }
            if (-not $exists) { throw 'CHAT secret-file settings require an existing absolute file path.' }
        }
        if ($name -in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT') -and $value -cne $Environment) {
            throw 'Local CHAT execution requires matching Development environment settings.'
        }
        if ($name -ieq 'ASPNETCORE_URLS' -and $value -cne $address) {
            throw 'CHAT configuration URL must exactly match the loopback address selected by -Port.'
        }
    }
}

# 기존 Production 설정이나 상충한 환경을 Development로 조용히 바꾸지 않는다.
$previous = @{}
foreach ($name in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    $previous[$name] = $value
    if (-not [string]::IsNullOrWhiteSpace($value) -and $value -cne $Environment) {
        throw 'Local CHAT execution requires matching Development environment settings.'
    }
}
# 비밀 입력은 기존 raw env/다른 파일을 조용히 덮어쓰지 않는다. 실제 User Secrets 충돌은 Options loader가 검사한다.
foreach ($setting in $settings.GetEnumerator()) {
    if (-not $setting.Key.EndsWith('_FILE', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $rawName = $setting.Key.Substring(0, $setting.Key.Length - 5)
    $rawValue = [Environment]::GetEnvironmentVariable($rawName)
    $priorPath = [Environment]::GetEnvironmentVariable($setting.Key)
    if (-not [string]::IsNullOrEmpty($rawValue) -or
        (-not [string]::IsNullOrEmpty($priorPath) -and $priorPath -cne $setting.Value)) {
        throw 'Conflicting CHAT secret sources. Preserve the existing source and resolve the mismatch explicitly.'
    }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'The CHAT API project is missing.' }
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
$executable = if ($dotnet) { $dotnet.Source } else { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The existing .NET SDK is not available.' }
if ($ValidateOnly) {
    Write-Output "Local CHAT launch settings accepted: Development; loopback $address. No server was started; managed Options and secret contents were not validated."
    return
}

# 표준 Development JSON/User Secrets 뒤에 명시한 환경 설정을 공급하고 성공/실패 모두 원래 process 환경을 복원한다.
try {
    foreach ($setting in $settings.GetEnumerator()) {
        if (-not $previous.ContainsKey($setting.Key)) { $previous[$setting.Key] = [Environment]::GetEnvironmentVariable($setting.Key) }
        [Environment]::SetEnvironmentVariable($setting.Key, $setting.Value)
    }
    $env:DOTNET_ENVIRONMENT = $Environment
    $env:ASPNETCORE_ENVIRONMENT = $Environment
    $arguments = @('run', '--no-restore', '--no-launch-profile', '--project', $project)
    if ($NoBuild) { $arguments += '--no-build' }
    $arguments += @('--', '--urls', $address)
    if ($ValidateConfiguration) { $arguments += '--validate-configuration' }
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) { throw "Local CHAT process failed with exit code $LASTEXITCODE." }
} finally {
    foreach ($name in $previous.Keys) {
        # PowerShell의 null→빈 문자열 변환을 피한다. .NET 10은 빈 환경변수와 부재를 구분한다.
        $original = if ($null -eq $previous[$name]) { [NullString]::Value } else { $previous[$name] }
        [Environment]::SetEnvironmentVariable($name, $original)
    }
}
