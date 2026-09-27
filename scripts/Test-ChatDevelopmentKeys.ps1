param([string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$root = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
if ([string]::IsNullOrWhiteSpace($ResultDirectory)) {
    $ResultDirectory = Join-Path $root ('BootstrapKeys/' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($ResultDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Tests require a new central CHAT TestResults directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$generator = Join-Path $PSScriptRoot 'New-ChatDevelopmentKeys.ps1'
$keys = @('be-to-chat-service-signing-key', 'chat-to-be-service-signing-key', 'chat-to-ai-api-key')
$checks = [Collections.Generic.List[string]]::new()
$priorDotnet = $env:DOTNET_ENVIRONMENT
$priorAspnet = $env:ASPNETCORE_ENVIRONMENT

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Development key test failed: $Name" }
    $checks.Add($Name)
}

function New-FixtureDirectory([string]$Name) {
    $path = Join-Path $output $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetOwner($owner)
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner, 'FullControl',
            'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $path -AclObject $acl
    } else {
        [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    return $path
}

function Write-Fixture([string]$Directory, [string]$Name, [byte[]]$Bytes) {
    $path = Join-Path $Directory $Name
    [IO.File]::WriteAllBytes($path, $Bytes)
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
}

function Assert-Refused([string]$Name, [string]$Directory, [hashtable]$Extra = @{}) {
    # 실행 / 검증 — 잘못된 값은 진단에도 원문으로 출력되지 않는다.
    $failed = $false
    try { & $generator -Environment Development -OutputDirectory $Directory @Extra | Out-Null }
    catch {
        $failed = $true
        Assert-Check (-not $_.Exception.ToString().Contains('synthetic-secret-canary')) "$Name sanitized error"
    }
    Assert-Check $failed $Name
}

try {
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'

    # 준비 / 실행 / 검증 — dry-run은 디렉터리나 Redis credential을 만들지 않는다.
    $empty = Join-Path $output 'empty'
    $dry = @(& $generator -Environment Development -OutputDirectory $empty -DryRun)
    Assert-Check (-not (Test-Path -LiteralPath $empty)) 'Dry run performs no filesystem mutation'
    Assert-Check (@($dry | Where-Object { $_.Status -eq 'DEFERRED' -and $_.Source -eq 'GENERATED' }).Count -eq 3) 'Dry run identifies three deferred internal key generations'
    Assert-Check (($dry | Where-Object Name -eq redis-password).Status -eq 'NEEDS_INPUT') 'Missing Redis password requires existing server input'

    # 실행 — 합성 신규 3개를 생성한 뒤 ACL과 기존 byte 보존을 함께 확인한다.
    $created = @(& $generator -Environment Development -OutputDirectory $empty)
    Assert-Check (@($created | Where-Object Status -eq CREATED).Count -eq 3) 'Creates only approved three internal keys'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $empty 'redis-password'))) 'Does not invent a Redis server password'
    $before = @{}
    $raw = @()
    foreach ($name in $keys) {
        $before[$name] = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $empty $name)))
        $value = [IO.File]::ReadAllText((Join-Path $empty $name))
        Assert-Check ($value.Length -eq 44 -and [Convert]::FromBase64String($value).Length -eq 32) "CSPRNG encoding: $name"
        $raw += $value
    }
    Assert-Check (@($raw | Select-Object -Unique).Count -eq 3) 'Directional generated values differ'
    $aclBefore = if ($IsWindows) { (Get-Acl -LiteralPath $empty).Sddl } else { [IO.File]::GetUnixFileMode($empty) }
    $repeat = @(& $generator -Environment Development -OutputDirectory $empty)
    Assert-Check (@($repeat | Where-Object Status -eq REUSED).Count -eq 3) 'Repeat reuses all internal keys'
    foreach ($name in $keys) {
        Assert-Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $empty $name))) -ceq $before[$name]) "Repeat preserves bytes: $name"
    }
    $aclAfter = if ($IsWindows) { (Get-Acl -LiteralPath $empty).Sddl } else { [IO.File]::GetUnixFileMode($empty) }
    Assert-Check ($aclBefore -ceq $aclAfter) 'Existing directory ACL remains unchanged'
    $raw = $null

    # 준비 / 실행 / 검증 — 일부 키와 기존 Redis는 재사용하고 누락 내부 키만 채운다.
    $partial = Join-Path $output 'partial'
    & $generator -Environment Development -OutputDirectory $partial -Kinds $keys[0] | Out-Null
    $existing = [IO.File]::ReadAllText((Join-Path $partial $keys[0]))
    $redis = 'synthetic-existing-redis-password-not-a-real-server'
    Write-Fixture $partial 'redis-password' ([Text.Encoding]::ASCII.GetBytes($redis))
    $filled = @(& $generator -Environment Development -OutputDirectory $partial)
    Assert-Check (@($filled | Where-Object Status -eq CREATED).Count -eq 2) 'Partial directory creates exactly missing internal keys'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $partial $keys[0])) -ceq $existing) 'Partial fill preserves existing key'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $partial 'redis-password')) -ceq $redis) 'Partial fill preserves Redis bytes'
    Assert-Check (($filled | Where-Object Name -eq redis-password).Status -eq 'REUSED') 'Existing Redis reported reused without server claim'

    # 준비 / 실행 / 검증 — 기존 상대 서비스 원본은 생성하지 않고 동일 bytes로 canonical 파일에 공급한다.
    $supplied = @{}
    foreach ($name in $keys) { $supplied[$name] = [IO.File]::ReadAllText((Join-Path $partial $name)) }
    $supplied['be-user-jwt-signing-key'] = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
    $sourceOnly = Join-Path $output 'source-only'
    $deferred = @(& $generator -Environment Development -OutputDirectory $sourceOnly -ExistingValues $supplied -DryRun)
    Assert-Check (@($deferred | Where-Object Status -eq DEFERRED).Count -eq 4) 'Existing remote sources have four deferred canonical copies'
    Assert-Check (-not (Test-Path -LiteralPath $sourceOnly)) 'Existing source dry run writes no files'
    $applied = @(& $generator -Environment Development -OutputDirectory $sourceOnly -ExistingValues $supplied)
    Assert-Check (@($applied | Where-Object Status -eq APPLIED).Count -eq 4) 'Existing source files are applied, not generated'
    Assert-Check (@($applied | Where-Object Status -eq CREATED).Count -eq 0) 'All supplied directions avoid random generation'
    foreach ($name in $supplied.Keys) {
        Assert-Check ([IO.File]::ReadAllText((Join-Path $sourceOnly $name)) -ceq $supplied[$name]) "Existing issuer bytes preserved: $name"
    }
    $sourceRepeat = @(& $generator -Environment Development -OutputDirectory $sourceOnly -ExistingValues $supplied)
    Assert-Check (@($sourceRepeat | Where-Object Status -eq REUSED).Count -eq 4) 'Existing source repeat reuses all canonical files'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $empty 'be-user-jwt-signing-key'))) 'Missing user JWT is never generated'

    $mismatch = Join-Path $output 'source-mismatch'
    & $generator -Environment Development -OutputDirectory $mismatch -Kinds $keys[0] | Out-Null
    $original = [IO.File]::ReadAllText((Join-Path $mismatch $keys[0]))
    $failed = $false
    try { & $generator -Environment Development -OutputDirectory $mismatch -ExistingValues $supplied | Out-Null }
    catch { $failed = $_.Exception.Message.Contains('MISMATCH') }
    Assert-Check $failed 'Different existing issuer and canonical source report MISMATCH'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $mismatch $keys[0])) -ceq $original) 'Mismatch preserves original canonical bytes'
    Assert-Check (@(Get-ChildItem -LiteralPath $mismatch -File).Count -eq 1) 'Mismatch stops before any missing file is generated'
    $invalidSource = @{ 'chat-to-ai-api-key' = 'synthetic-secret-canary-invalid-source' + [char]10 }
    Assert-Refused 'Invalid supplied opaque value rejected' (Join-Path $output 'invalid-source') @{ ExistingValues = $invalidSource }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output 'invalid-source'))) 'Invalid supplied source creates no directory'
    Assert-Refused 'User JWT random generation kind rejected' (Join-Path $output 'jwt-generation') @{ Kinds = @('be-user-jwt-signing-key') }
    Assert-Refused 'Redis source is outside approved in-memory kinds' (Join-Path $output 'redis-source') @{ ExistingValues = @{ 'redis-password' = 'synthetic-secret-canary-existing-redis' } }

    # 준비 / 실행 / 검증 — opaque key를 임의 Base64 decode하지 않는다.
    $opaque = New-FixtureDirectory 'opaque'
    Write-Fixture $opaque $keys[2] ([Text.Encoding]::ASCII.GetBytes('opaque.synthetic-api-key_without-base64-format'))
    $opaqueResult = @(& $generator -Environment Development -OutputDirectory $opaque)
    Assert-Check (($opaqueResult | Where-Object Name -eq $keys[2]).Status -eq 'REUSED') 'Raw API key stays opaque'
    foreach ($length in @(32, 128)) {
        $boundary = New-FixtureDirectory "boundary-$length"
        Write-Fixture $boundary $keys[0] ([Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String([byte[]]::new($length))))
        $result = @(& $generator -Environment Development -OutputDirectory $boundary -DryRun)
        Assert-Check (($result | Where-Object Name -eq $keys[0]).Status -eq 'REUSED') "HMAC decoded boundary accepted: $length"
    }

    # 준비 / 실행 / 검증 — 형식 오류 하나도 전체 생성 전에 거절한다.
    $base64 = [Convert]::ToBase64String([byte[]]::new(32))
    $invalid = [ordered]@{
        empty = [byte[]]::new(0)
        bom = [byte[]](239,187,191) + [Text.Encoding]::ASCII.GetBytes($base64)
        newline = [Text.Encoding]::ASCII.GetBytes($base64 + [char]10)
        space = [Text.Encoding]::ASCII.GetBytes(' ' + $base64)
        quote = [Text.Encoding]::ASCII.GetBytes('"' + $base64 + '"')
        base64 = [Text.Encoding]::ASCII.GetBytes('synthetic-secret-canary-invalid-base64')
        short = [Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String([byte[]]::new(31)))
        long = [Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String([byte[]]::new(129)))
        noncanonical = [Text.Encoding]::ASCII.GetBytes(('A' * 42) + 'B=')
        utf8 = [byte[]](255,255)
    }
    foreach ($name in $invalid.Keys) {
        $directory = New-FixtureDirectory ("invalid-" + $name)
        Write-Fixture $directory $keys[0] $invalid[$name]
        Assert-Refused "Invalid $name rejected" $directory
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory $keys[1]))) "Invalid $name prevents other generation"
        Assert-Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $directory $keys[0]))) -ceq [Convert]::ToBase64String($invalid[$name])) "Invalid $name stays unchanged"
    }
    foreach ($name in @('chat-to-ai-api-key', 'redis-password')) {
        $directory = New-FixtureDirectory ("trailing-newline-" + $name)
        Write-Fixture $directory $name ([Text.Encoding]::ASCII.GetBytes('synthetic-secret-canary-opaque-input' + [char]10))
        Assert-Refused "Opaque trailing newline rejected: $name" $directory
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory $keys[0]))) "Opaque newline prevents generation: $name"
    }
    $same = New-FixtureDirectory 'same-directions'
    Write-Fixture $same $keys[0] ([Text.Encoding]::ASCII.GetBytes($base64))
    Write-Fixture $same $keys[1] ([Text.Encoding]::ASCII.GetBytes($base64))
    Assert-Refused 'Equal directional keys rejected' $same
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $same $keys[2]))) 'Equal keys prevent other generation'

    # 준비 / 실행 / 검증 — 공개 ACL과 링크를 자동 교정하거나 따라가지 않는다.
    $permissions = Join-Path $output 'public-permissions'
    [IO.Directory]::CreateDirectory($permissions) | Out-Null
    if ($IsWindows) {
        # 기본 상속 ACL을 그대로 사용한다. 합성 키를 쓰거나 실제 폴더 권한을 넓히지 않는다.
        $unchanged = (Get-Acl -LiteralPath $permissions).Sddl
    } else {
        [IO.File]::SetUnixFileMode($permissions, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::OtherRead)
        $unchanged = [IO.File]::GetUnixFileMode($permissions)
    }
    Assert-Refused 'Broad permissions rejected' $permissions
    $after = if ($IsWindows) { (Get-Acl -LiteralPath $permissions).Sddl } else { [IO.File]::GetUnixFileMode($permissions) }
    Assert-Check ($unchanged -ceq $after) 'Unsafe existing ACL is not silently rewritten'
    $link = Join-Path $output 'linked'
    if ($IsWindows) { New-Item -ItemType Junction -Path $link -Target $partial | Out-Null }
    else { New-Item -ItemType SymbolicLink -Path $link -Target $partial | Out-Null }
    Assert-Refused 'Directory link rejected' $link
    Assert-Refused 'Redis generation kind rejected' (Join-Path $output 'redis-request') @{ Kinds = @('redis-password') }
    Assert-Refused 'Unknown kind rejected' (Join-Path $output 'unknown-request') @{ Kinds = @('synthetic-secret-canary') }

    $env:DOTNET_ENVIRONMENT = 'Production'
    Assert-Refused 'Inherited Production rejected' (Join-Path $output 'prod-env')
    $env:DOTNET_ENVIRONMENT = 'Development'
    $failed = $false
    try { & $generator -Environment Production -OutputDirectory (Join-Path $output 'prod') | Out-Null }
    catch { $failed = $true }
    Assert-Check $failed 'Explicit Production generation rejected'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output 'prod'))) 'Production rejection writes no files'

    # 준비 — 독립 프로세스 둘을 동시에 실행한다. key bytes는 인자나 로그에 넣지 않는다.
    $raceDirectory = Join-Path $output 'concurrent'
    $worker = Join-Path $output 'concurrent-worker.ps1'
    @'
param([string]$Generator, [string]$Directory, [string]$Result)
$ErrorActionPreference = 'Stop'
$statuses = @(& $Generator -Environment Development -OutputDirectory $Directory)
$statuses | ConvertTo-Json | Set-Content -LiteralPath $Result -Encoding utf8
'@ | Set-Content -LiteralPath $worker -Encoding utf8
    $processes = @()
    foreach ($number in @(1, 2)) {
        $arguments = @('-NoProfile', '-File', ('"' + $worker + '"'), '-Generator', ('"' + $generator + '"'),
            '-Directory', ('"' + $raceDirectory + '"'), '-Result', ('"' + (Join-Path $output "race-$number.json") + '"'))
        $launch = @{ FilePath = (Get-Process -Id $PID).Path; ArgumentList = $arguments; PassThru = $true
            RedirectStandardOutput = (Join-Path $output "race-$number.stdout.log")
            RedirectStandardError = (Join-Path $output "race-$number.stderr.log") }
        if ($IsWindows) { $launch.WindowStyle = 'Hidden' }
        $processes += Start-Process @launch
    }

    # 실행 / 검증 — 원자적 완성 파일만 읽고 경쟁 생성도 overwrite 없이 수렴한다.
    foreach ($process in $processes) {
        Assert-Check ($process.WaitForExit(30000)) 'Concurrent generator exits within timeout'
        Assert-Check ($process.ExitCode -eq 0) 'Concurrent generator exits successfully'
    }
    $raceResults = @()
    foreach ($number in @(1, 2)) { $raceResults += @(Get-Content -Raw -LiteralPath (Join-Path $output "race-$number.json") | ConvertFrom-Json) }
    Assert-Check (@($raceResults | Where-Object Status -eq CREATED).Count -eq 3) 'Concurrent runs create exactly three files in total'
    $verified = @(& $generator -Environment Development -OutputDirectory $raceDirectory -DryRun)
    Assert-Check (@($verified | Where-Object Status -eq REUSED).Count -eq 3) 'Concurrent final keys all validate'
    Assert-Check (@(Get-ChildItem -LiteralPath $raceDirectory -Force).Count -eq 3) 'No partial writes remain after concurrent creation'
} finally {
    $env:DOTNET_ENVIRONMENT = $priorDotnet
    $env:ASPNETCORE_ENVIRONMENT = $priorAspnet
}
@{ passed = $checks.Count; failed = 0; skipped = 0; checks = @($checks)
    realCredentialsChanged = $false; externalServicesCalled = $false; concurrentProcesses = 2 } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'bootstrap-key-checks.json') -Encoding utf8
Write-Output "Development credential checks passed: $($checks.Count). Evidence: $output"
