param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$OutputDirectory,
    [string[]]$Kinds = @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'chat-to-ai-api-key'),
    [Collections.IDictionary]$ExistingValues = @{},
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$keyNames = @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'chat-to-ai-api-key')
$sourceNames = $keyNames + 'be-user-jwt-signing-key'
$allNames = $sourceNames + 'redis-password'
$failureState = 'FAILED'
$identity = if ($IsWindows) { [Security.Principal.WindowsIdentity]::GetCurrent().User } else { $null }

function Assert-NoLink([string]$Path) {
    # 최종 파일뿐 아니라 부모 junction도 거절한다.
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Credential paths cannot contain a symbolic link or junction.'
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Assert-PrivateAccess([string]$Path, [bool]$Directory) {
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $Path
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $identity.Value) {
            throw 'Credential ownership does not match the current user.'
        }
        $readable = $false
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
                if ($rule.IdentityReference.Value -cne $identity.Value) {
                    throw 'Credential permissions must allow only the current owner.'
                }
                if ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadData) { $readable = $true }
            }
        }
        if (-not $readable) { throw 'Credential owner does not have read access.' }
    } else {
        $mode = [IO.File]::GetUnixFileMode($Path)
        $other = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::GroupExecute -bor
            [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band $other) -or -not ($mode -band [IO.UnixFileMode]::UserRead) -or
            ($Directory -and -not ($mode -band [IO.UnixFileMode]::UserExecute))) {
            throw 'Credential permissions must allow only the owner.'
        }
    }
}

function Assert-CredentialValue([string]$Name, [string]$Value) {
    # 기존 발급자의 원문을 trim/decode 후 재인코딩해서 바꾸지 않는다.
    if ($Value.Length -eq 0 -or $Value.Length -gt 4096 -or $Value -notmatch '\A[\x21-\x7E]+\z' -or
        $Value.Contains('"') -or $Value.Contains("'")) { throw "Credential format is invalid: $Name" }
    if ($Name -in @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'be-user-jwt-signing-key')) {
        try { $decoded = [Convert]::FromBase64String($Value) }
        catch { throw "Credential Base64 is invalid: $Name" }
        try {
            if ($decoded.Length -lt 32 -or $decoded.Length -gt 128 -or [Convert]::ToBase64String($decoded) -cne $Value) {
                throw "Credential decoded length or encoding is invalid: $Name"
            }
        } finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($decoded) }
    } elseif ($Name -eq 'chat-to-ai-api-key' -and $Value.Length -lt 32) {
        throw "Credential length is invalid: $Name"
    }
}

function Read-Credential([string]$Name) {
    $path = Join-Path $target $Name
    Assert-NoLink $path
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Credential file is invalid: $Name" }
    Assert-PrivateAccess $path $false

    # BOM·공백·개행·quote를 숨겨서 정규화하지 않는다. API key는 opaque 문자열이다.
    $bytes = [IO.File]::ReadAllBytes($path)
    try {
        if ($bytes.Length -eq 0 -or $bytes.Length -gt 4096) { throw "Credential length is invalid: $Name" }
        $value = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        Assert-CredentialValue $Name $value
        return $value
    } finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
}

function Assert-Independent([hashtable]$Values) {
    $present = @($allNames | Where-Object { $null -ne $Values[$_] })
    for ($left = 0; $left -lt $present.Count; $left++) {
        for ($right = $left + 1; $right -lt $present.Count; $right++) {
            if ($Values[$present[$left]] -ceq $Values[$present[$right]]) {
                throw 'Development credentials for different purposes must be independent.'
            }
        }
    }
}

function New-PrivateDirectory {
    if (Test-Path -LiteralPath $target) { return }
    $parent = Split-Path -Parent $target
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-NoLink $parent
    $staging = Join-Path $parent ('.credential-directory-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    try {
        if ($IsWindows) {
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $acl.SetOwner($identity)
            $acl.SetAccessRuleProtection($true, $false)
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl',
                'ContainerInherit, ObjectInherit', 'None', 'Allow'))
            Set-Acl -LiteralPath $staging -AclObject $acl
        } else {
            [IO.File]::SetUnixFileMode($staging, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
        }
        # 경쟁자가 먼저 만든 디렉터리의 ACL은 변경하지 않는다.
        try { [IO.Directory]::Move($staging, $target) }
        catch [IO.IOException] { if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw } }
    } finally {
        if ([IO.Directory]::Exists($staging)) { [IO.Directory]::Delete($staging, $false) }
    }
}

try {
    # Development 3개 내부 연결만 생성한다. Redis·사용자 JWT·LL 키는 생성하지 않는다.
    if ($Environment -cne 'Development') { throw 'Only the Development environment is accepted.' }
    foreach ($name in @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT')) {
        $configured = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($configured) -and $configured -cne $Environment) {
            throw 'Development credential generation cannot use conflicting environment settings.'
        }
    }
    if (-not $Kinds.Count -or @($Kinds | Select-Object -Unique).Count -ne $Kinds.Count -or
        @($Kinds | Where-Object { $_ -cnotin $keyNames }).Count) {
        throw 'Only distinct approved Development internal credential kinds may be requested.'
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $workspace '.secrets/development' }
    $target = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
    $allowedRoots = @((Join-Path $workspace '.secrets/development'), $resultsRoot)
    $allowed = $false
    foreach ($root in $allowedRoots) {
        if ($target.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $allowed = $true }
    }
    if (-not $allowed) { throw 'Credential output must stay in CHAT development secrets or central CHAT TestResults.' }

    # 생성 전에 기존 파일을 모두 검사한다. 기존 ACL이나 키는 수정하지 않는다.
    Assert-NoLink $target
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Credential output must be a directory.' }
        Assert-PrivateAccess $target $true
    }
    $values = @{}
    foreach ($name in $allNames) { $values[$name] = Read-Credential $name }
    Assert-Independent $values
    $supplied = @{}
    if ($null -eq $ExistingValues) { $ExistingValues = @{} }
    foreach ($name in $ExistingValues.Keys) {
        if ($name -isnot [string] -or $name -cnotin $sourceNames -or $ExistingValues[$name] -isnot [string]) {
            throw 'Only approved in-memory existing credential values may be supplied.'
        }
        Assert-CredentialValue $name $ExistingValues[$name]
        if ($null -ne $values[$name] -and $values[$name] -cne $ExistingValues[$name]) {
            $failureState = 'MISMATCH'
            throw 'Existing credential sources disagree.'
        }
        $supplied[$name] = $ExistingValues[$name]
    }
    $proposed = $values.Clone()
    foreach ($name in $supplied.Keys) { $proposed[$name] = $supplied[$name] }
    Assert-Independent $proposed
    $statuses = @{}
    $sources = @{}
    foreach ($name in $allNames) {
        $statuses[$name] = if ($null -ne $values[$name]) { 'REUSED' } elseif ($supplied.ContainsKey($name)) { 'DEFERRED' }
            elseif ($DryRun -and $name -in $Kinds) { 'DEFERRED' } else { 'NEEDS_INPUT' }
        $sources[$name] = if ($null -ne $values[$name]) { 'FILE' } elseif ($supplied.ContainsKey($name)) { 'EXISTING_VALUE' }
            elseif ($DryRun -and $name -in $Kinds) { 'GENERATED' } else { 'MISSING' }
    }

    $writeNames = @(@($Kinds) + @($supplied.Keys) | Select-Object -Unique)
    if (-not $DryRun -and @($writeNames | Where-Object { $null -eq $values[$_] }).Count) {
        New-PrivateDirectory
        Assert-NoLink $target
        Assert-PrivateAccess $target $true
        foreach ($name in $writeNames) {
            # 경쟁자가 먼저 완성한 파일을 다시 읽으며 부분 작성 파일은 공개하지 않는다.
            $current = Read-Credential $name
            if ($null -ne $current) {
                if ($supplied.ContainsKey($name) -and $supplied[$name] -cne $current) {
                    $failureState = 'MISMATCH'
                    throw 'A concurrently supplied credential does not match its source.'
                }
                $values[$name] = $current
                $statuses[$name] = 'REUSED'
                $sources[$name] = 'FILE'
                continue
            }
            $random = $null
            $bytes = $null
            $temporary = Join-Path $target ('.credential-write-' + [Guid]::NewGuid().ToString('N'))
            try {
                if ($supplied.ContainsKey($name)) {
                    $value = $supplied[$name]
                } else {
                    # CSPRNG 생성은 여전히 3개 신규 내부 키에만 제한된다.
                    if ($name -cnotin $keyNames) { throw 'This credential requires an existing issuer value.' }
                    $random = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
                    $value = [Convert]::ToBase64String($random)
                }
                $bytes = [Text.Encoding]::ASCII.GetBytes($value)
                $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($temporary, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush($true)
                } finally { $stream.Dispose() }
                Assert-PrivateAccess $temporary $false
                try {
                    [IO.File]::Move($temporary, (Join-Path $target $name), $false)
                    $statuses[$name] = if ($supplied.ContainsKey($name)) { 'APPLIED' } else { 'CREATED' }
                    $sources[$name] = if ($supplied.ContainsKey($name)) { 'EXISTING_VALUE' } else { 'GENERATED' }
                } catch [IO.IOException] {
                    if (-not (Test-Path -LiteralPath (Join-Path $target $name) -PathType Leaf)) { throw }
                    $statuses[$name] = 'REUSED'
                    $sources[$name] = 'FILE'
                }
                $values[$name] = Read-Credential $name
                if ($supplied.ContainsKey($name) -and $supplied[$name] -cne $values[$name]) {
                    $failureState = 'MISMATCH'
                    throw 'A concurrently created credential does not match its source.'
                }
                Assert-Independent $values
            } finally {
                if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
                if ($null -ne $random) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($random) }
                if ($null -ne $bytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
                $value = $null
            }
        }
    }

    # 생성 중 경쟁자가 채운 항목까지 최종 재확인한다. 기존 발급 원문과 다른 파일은 수용하지 않는다.
    if (-not $DryRun) {
        foreach ($name in $allNames) {
            $values[$name] = Read-Credential $name
            if ($supplied.ContainsKey($name) -and $supplied[$name] -cne $values[$name]) {
                $failureState = 'MISMATCH'
                throw 'Applied credential does not match its existing source.'
            }
        }
        Assert-Independent $values
    }

    # 양측 공급·실제 인증은 별도 bootstrap 책임이다. 원문·hash를 출력하지 않는다.
    foreach ($name in $allNames) {
        [pscustomobject]@{ Name = $name; Status = $statuses[$name]; Source = $sources[$name]; Path = (Join-Path $target $name); Environment = 'Development' }
    }
} catch {
    # 파일·encoding·ACL 예외에 포함될 수 있는 입력 원문을 전달하지 않는다.
    throw "Development credential preparation $failureState. Check environment, allowed paths, owner permissions, credential format and purpose separation; existing credentials are not overwritten."
} finally {
    $values = $null
    $current = $null
    $value = $null
    $supplied = $null
    $proposed = $null
    $ExistingValues = $null
}
