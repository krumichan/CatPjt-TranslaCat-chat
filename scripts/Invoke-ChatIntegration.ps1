param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(ValueFromRemainingArguments)][string[]]$DotnetArguments
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$allowedRoot = Join-Path $resultsRoot 'V2\Runtime\'
if (-not $manifestPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest is outside the central CHAT runtime directory.' }
$runtime = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $runtime.testMode -or $runtime.runId -notmatch '^[a-f0-9]{32}$') { throw 'Not an owned test manifest.' }

# manifest ID와 실제 container label을 모두 확인한다. prefix 이름만 믿지 않는다.
foreach ($id in @($runtime.mysql,$runtime.redis)) {
    $label = & docker inspect --format '{{index .Config.Labels "translacat.chat.run"}}' $id
    if ($LASTEXITCODE -ne 0 -or $label -ne $runtime.runId) { throw 'Container ownership verification failed.' }
}
$secure = Import-Clixml -LiteralPath (Join-Path (Split-Path -Parent $manifestPath) 'mysql-secret.clixml')
$password = [Net.NetworkCredential]::new('', $secure).Password
$names = @('CHAT_TEST_MYSQL','CHAT_TEST_REDIS','CHAT_TEST_RUN_ID','CHAT_TEST_MANIFEST','CHAT_DATABASE_CONNECTION')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    # 비밀번호는 자식 프로세스 환경으로만 전달하고 출력하지 않는다.
    $env:CHAT_TEST_MYSQL = "Server=127.0.0.1;Port=$($runtime.mysqlPort);User ID=root;Password=$password;SslMode=Disabled;AllowPublicKeyRetrieval=True;Connection Timeout=5;Default Command Timeout=10"
    $env:CHAT_TEST_REDIS = "127.0.0.1:$($runtime.redisPort),abortConnect=false,connectTimeout=3000,syncTimeout=3000"
    $env:CHAT_TEST_RUN_ID = $runtime.runId
    $env:CHAT_TEST_MANIFEST = $manifestPath
    $env:CHAT_DATABASE_CONNECTION = $env:CHAT_TEST_MYSQL + ';Database=translacat_chat'
    & 'C:\Program Files\dotnet\dotnet.exe' @DotnetArguments
    $executionExit = $LASTEXITCODE
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    $password = $null
}
exit $executionExit
