param(
    [string]$EnvironmentFile = (Join-Path $PSScriptRoot '../deploy/chat/.env.prod.example'),
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
$checks = [Collections.Generic.List[object]]::new()
$values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$known = @(
    'DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'CHAT_API_PORT', 'CHAT_ALLOWED_HOSTS',
    'CHAT_BROWSER_ORIGIN', 'CHAT_REDIS_NAMESPACE', 'CHAT_SOURCE_TIME_ZONE',
    'CHAT_REDIS_PASSWORD_FILE', 'CHAT_DATABASE_CONNECTION_FILE', 'CHAT_JWT_SIGNING_KEY_FILE',
    'CHAT_SERVICE_INGRESS_ENABLED', 'CHAT_SERVICE_INGRESS_ISSUER', 'CHAT_SERVICE_INGRESS_AUDIENCE',
    'CHAT_SERVICE_INGRESS_SERVICE', 'CHAT_SERVICE_INGRESS_KEY_FILE',
    'CHAT_IDENTITY_ENABLED', 'CHAT_IDENTITY_BASE_URL', 'CHAT_IDENTITY_TIMEOUT_SECONDS',
    'CHAT_IDENTITY_ISSUER', 'CHAT_IDENTITY_AUDIENCE', 'CHAT_IDENTITY_SERVICE', 'CHAT_IDENTITY_KEY_FILE',
    'CHAT_CORE_ENABLED', 'CHAT_CORE_BASE_URL', 'CHAT_CORE_TIMEOUT_SECONDS',
    'CHAT_CORE_ISSUER', 'CHAT_CORE_AUDIENCE', 'CHAT_CORE_SERVICE',
    'CHAT_AI_ENABLED', 'CHAT_TRANSLATION_ENABLED', 'CHAT_AI_BASE_URL', 'CHAT_AI_API_KEY_FILE'
)

function Add-Check([string]$Name, [string]$Reason) {
    # 상태는 공통 용어로 통일하고 구조/파일 존재에 한정된 검증 범위는 원래 진단에 남긴다.
    $status = if ($Reason -in @('VERIFIED_STRUCTURE', 'FILE_PRESENT_CONTENT_NOT_VERIFIED')) {
        'VERIFIED'
    } elseif ($Reason.StartsWith('NEEDS_INPUT', [StringComparison]::Ordinal)) {
        'NEEDS_INPUT'
    } else {
        'MISMATCH'
    }
    $checks.Add([pscustomobject]@{ Name = $Name; Status = $status; Reason = $Reason })
}

function Has-Placeholder([string]$Value) {
    return $Value -match '\$\{|^<.*>$|(?:^|://)(?i:changeme|replaceme|placeholder|replace-with-|your-)' -or
        $Value -match '(?:^|\.)example\.(?:com|org|net)(?::|/|$)'
}

function Require-Value([string]$Name) {
    if (-not $values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($values[$Name])) {
        Add-Check $Name 'NEEDS_INPUT'
        return $false
    }
    if (Has-Placeholder $values[$Name]) {
        Add-Check $Name 'REJECTED_PLACEHOLDER'
        return $false
    }
    return $true
}

function Check-Https([string]$Name, [switch]$Origin) {
    if (-not (Require-Value $Name)) { return }
    $address = $null
    $valid = [uri]::TryCreate($values[$Name], [UriKind]::Absolute, [ref]$address)
    if (-not $valid -or $address.Scheme -cne 'https' -or $address.IsLoopback -or
        $address.UserInfo -or $address.Query -or $address.Fragment -or
        ($Origin -and $values[$Name] -cne $address.GetLeftPart([UriPartial]::Authority))) {
        Add-Check $Name 'REJECTED_ADDRESS'
    } else { Add-Check $Name 'VERIFIED_STRUCTURE' }
}

# Compose용 공개 예제와 같은 literal KEY=value 형식만 검사한다. 값/비밀 원문은 출력하지 않는다.
try {
    $bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($EnvironmentFile))
    if ($bytes.Length -gt 65536 -or ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) {
        throw 'Invalid encoding or size.'
    }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    foreach ($line in $text -split '\r?\n') {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
        if ($line -cnotmatch '^([A-Z][A-Z0-9_]*)=(.*)$') {
            Add-Check 'ENV_FILE_FORMAT' 'REJECTED'
            continue
        }
        $name = $Matches[1]
        $value = $Matches[2]
        if ($name -cnotin $known) {
            # 잘못된 이름에 민감한 문자열을 넣었어도 진단으로 되풀이하지 않는다.
            Add-Check 'UNREGISTERED_SETTING' 'REJECTED'
            continue
        }
        if ($values.ContainsKey($name)) {
            Add-Check $name 'REJECTED_DUPLICATE'
            continue
        }
        if ($value -match '[\x00-\x1f\x7f]' -or $value -match '["'']|\s#') {
            Add-Check $name 'REJECTED_NON_LITERAL'
            continue
        }
        $values.Add($name, $value)
    }
} catch {
    Add-Check 'ENV_FILE' 'NEEDS_INPUT_OR_INVALID_FILE'
} finally {
    $bytes = $null
    $text = $null
}

# 이 검사는 프로세스 환경을 바꾸거나 Compose를 실행하지 않는다. 우선순위로 가려질 상충만 거절한다.
foreach ($name in $known) {
    $inherited = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrEmpty($inherited) -and
        (-not $values.ContainsKey($name) -or $inherited -cne $values[$name])) {
        Add-Check $name 'MISMATCH_PROCESS_ENVIRONMENT'
    }
}
foreach ($item in Get-ChildItem Env: | Where-Object { $_.Name -like 'CHAT_*' }) {
    if ($item.Name -cnotin $known -and ($item.Name.EndsWith('_FILE') -or
        $item.Name -match '(?i)(?:API_?KEY|PASSWORD|SIGNINGKEY|SIGNING_KEY|CONNECTIONSTRING|CONNECTION_STRING)$')) {
        Add-Check 'UNREGISTERED_PROCESS_SECRET_SETTING' 'REJECTED'
    }
}
foreach ($name in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT')) {
    if (Require-Value $name) {
        Add-Check $name $(if ($values[$name] -ceq 'Production') { 'VERIFIED_STRUCTURE' } else { 'REJECTED_ENVIRONMENT' })
    }
}

if (Require-Value 'CHAT_API_PORT') {
    $port = 0
    $valid = [int]::TryParse($values['CHAT_API_PORT'], [ref]$port) -and $port -ge 1024 -and $port -le 65535
    Add-Check 'CHAT_API_PORT' $(if ($valid) { 'VERIFIED_STRUCTURE' } else { 'REJECTED_PORT' })
}
if (Require-Value 'CHAT_ALLOWED_HOSTS') {
    $hosts = $values['CHAT_ALLOWED_HOSTS'] -split ';'
    $valid = $hosts.Count -gt 0 -and @($hosts | Where-Object {
        [string]::IsNullOrWhiteSpace($_) -or $_ -match '[:/*\s]' -or $_ -in @('localhost', '127.0.0.1') -or
        [uri]::CheckHostName($_) -eq [UriHostNameType]::Unknown
    }).Count -eq 0
    Add-Check 'CHAT_ALLOWED_HOSTS' $(if ($valid) { 'VERIFIED_STRUCTURE' } else { 'REJECTED_HOSTS' })
}
Check-Https 'CHAT_BROWSER_ORIGIN' -Origin
if (Require-Value 'CHAT_REDIS_NAMESPACE') {
    $valid = $values['CHAT_REDIS_NAMESPACE'] -cmatch '^translacat:chat:Production(?::[A-Za-z0-9_-]+)?$'
    Add-Check 'CHAT_REDIS_NAMESPACE' $(if ($valid) { 'VERIFIED_STRUCTURE' } else { 'REJECTED_ENVIRONMENT_SCOPE' })
}
if (Require-Value 'CHAT_SOURCE_TIME_ZONE') {
    try {
        $null = [TimeZoneInfo]::FindSystemTimeZoneById($values['CHAT_SOURCE_TIME_ZONE'])
        Add-Check 'CHAT_SOURCE_TIME_ZONE' 'VERIFIED_STRUCTURE'
    } catch { Add-Check 'CHAT_SOURCE_TIME_ZONE' 'REJECTED_TIME_ZONE' }
}

# internal-auth overlay는 enabled=false여도 세 방향의 file mount를 요구한다.
foreach ($name in $known | Where-Object { $_.EndsWith('_FILE') }) {
    if (-not (Require-Value $name)) { continue }
    $exists = $false
    try {
        $exists = [IO.Path]::IsPathFullyQualified($values[$name]) -and
            (Test-Path -LiteralPath $values[$name] -PathType Leaf -ErrorAction Stop)
        if ($exists) { $exists = (Get-Item -LiteralPath $values[$name] -ErrorAction Stop).Length -gt 0 }
    } catch { $exists = $false }
    Add-Check $name $(if ($exists) { 'FILE_PRESENT_CONTENT_NOT_VERIFIED' } else { 'NEEDS_INPUT_FILE' })
}

foreach ($name in $known | Where-Object { $_.EndsWith('_ENABLED') }) {
    if (Require-Value $name) {
        Add-Check $name $(if ($values[$name] -cin @('true', 'false')) { 'VERIFIED_STRUCTURE' } else { 'REJECTED_BOOLEAN' })
    }
}

# overlay의 Core 항목은 비어 있을 때 동일 방향 Identity 설정을 상속한다.
foreach ($suffix in @('BASE_URL', 'ISSUER', 'AUDIENCE', 'SERVICE')) {
    $core = "CHAT_CORE_$suffix"
    $identity = "CHAT_IDENTITY_$suffix"
    if ((-not $values.ContainsKey($core) -or [string]::IsNullOrEmpty($values[$core])) -and $values.ContainsKey($identity)) {
        $values[$core] = $values[$identity]
    }
}
foreach ($section in @('SERVICE_INGRESS', 'IDENTITY', 'CORE')) {
    if (-not $values.ContainsKey("CHAT_${section}_ENABLED") -or $values["CHAT_${section}_ENABLED"] -cne 'true') { continue }
    foreach ($suffix in @('ISSUER', 'AUDIENCE', 'SERVICE')) {
        $name = "CHAT_${section}_$suffix"
        if (Require-Value $name) { Add-Check $name 'VERIFIED_STRUCTURE' }
    }
    if ($section -ne 'SERVICE_INGRESS') {
        Check-Https "CHAT_${section}_BASE_URL"
        $name = "CHAT_${section}_TIMEOUT_SECONDS"
        if (Require-Value $name) {
            $timeout = 0
            $valid = [int]::TryParse($values[$name], [ref]$timeout) -and $timeout -ge 1 -and $timeout -le 30
            Add-Check $name $(if ($valid) { 'VERIFIED_STRUCTURE' } else { 'REJECTED_TIMEOUT' })
        }
    }
}
if (($values.ContainsKey('CHAT_AI_ENABLED') -and $values['CHAT_AI_ENABLED'] -ceq 'true') -or
    ($values.ContainsKey('CHAT_TRANSLATION_ENABLED') -and $values['CHAT_TRANSLATION_ENABLED'] -ceq 'true')) {
    Check-Https 'CHAT_AI_BASE_URL'
}

# 준비 상태는 배포 승인이나 런타임 인증 성공을 뜻하지 않는다. secret 내용/ACL/서명은 기존 도구와 Options의 책임이다.
$ready = @($checks | Where-Object Status -ne 'VERIFIED').Count -eq 0
$mismatch = @($checks | Where-Object Status -eq 'MISMATCH').Count -gt 0
$result = [pscustomobject]@{
    Environment = 'Production'
    Status = $(if ($ready) { 'VERIFIED' } elseif ($mismatch) { 'MISMATCH' } else { 'NEEDS_INPUT' })
    Reason = $(if ($ready) { 'STRUCTURE_READY_RUNTIME_NOT_VERIFIED' } else { 'NEEDS_INPUT_OR_REJECTED' })
    Ready = $ready
    Checks = @($checks)
    RuntimeBindingVerified = $false
    SecretContentOrPermissionsVerified = $false
    ProductionKeysGenerated = 0
    ExternalCalls = 0
}
if ($PassThru) { return $result }
$result | ConvertTo-Json -Depth 5
if (-not $ready) { throw 'Production preparation requires the named inputs or conflict resolution. No value was changed.' }
