param([string]$RunId = ([Guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'

# 실행별 소유권을 먼저 기록하고 기존 DB/Redis 자원은 재사용하지 않는다.
if ($RunId -notmatch '^[a-f0-9]{32}$') { throw 'RunId must be a new GUID in N format.' }
$workspace = Split-Path -Parent $PSScriptRoot
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$directory = Join-Path $resultsRoot "V2/Runtime/$RunId"
if (Test-Path -LiteralPath $directory) { throw 'Run directory already exists.' }
$null = New-Item -ItemType Directory -Path $directory
$manifestPath = Join-Path $directory 'manifest.json'
$docker = (Get-Command docker -ErrorAction Stop).Source
$runtime = [ordered]@{ runId=$RunId; testMode=$true; createdUtc=[DateTime]::UtcNow.ToString('o'); network=$null; volume=$null; mysql=$null; redis=$null; mysqlPort=$null; redisPort=$null; catalogs=@('translacat_chat'); status='CREATING' }
function Save-Manifest { $runtime | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8 }
function Invoke-Docker([string[]]$Arguments) {
    $result = & $docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker operation failed: $($Arguments[0])" }
    return ($result -join "`n").Trim()
}
function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
Save-Manifest

# 비밀번호는 실행마다 생성하고 현재 Windows 사용자로 암호화한다. manifest/로그에는 넣지 않는다.
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
$password | ConvertTo-SecureString -AsPlainText -Force | Export-Clixml -LiteralPath (Join-Path $directory 'mysql-secret.clixml')
$previousPassword = $env:MYSQL_ROOT_PASSWORD
try {
    $runtime.network = Invoke-Docker @('network','create','--label',"translacat.chat.run=$RunId","chat-it-$RunId")
    Save-Manifest
    $runtime.volume = Invoke-Docker @('volume','create','--label',"translacat.chat.run=$RunId","chat-it-db-$RunId")
    Save-Manifest

    # 빈 포트를 골라 고정 publish한다. Docker의 ephemeral publish는 재시작 때 포트가 바뀔 수 있다.
    # 선택 이후 다른 프로세스가 점유하면 Docker가 실패하며 그 프로세스를 종료하지 않는다.
    $runtime.mysqlPort = Get-FreeLoopbackPort
    do { $runtime.redisPort = Get-FreeLoopbackPort } while ($runtime.redisPort -eq $runtime.mysqlPort)
    Save-Manifest
    $env:MYSQL_ROOT_PASSWORD = $password
    $runtime.mysql = Invoke-Docker @('run','-d','--pull=never','--name',"chat-it-mysql-$RunId",'--label',"translacat.chat.run=$RunId",'--network',$runtime.network,'--network-alias','chat-db','-p',"127.0.0.1:$($runtime.mysqlPort):3306",'--env','MYSQL_ROOT_PASSWORD','--env','MYSQL_DATABASE=translacat_chat','--mount',"type=volume,source=$($runtime.volume),target=/var/lib/mysql",'mysql:8.4','--character-set-server=utf8mb4','--collation-server=utf8mb4_0900_ai_ci','--default-time-zone=+00:00')
    Save-Manifest
    $runtime.redis = Invoke-Docker @('run','-d','--pull=never','--name',"chat-it-redis-$RunId",'--label',"translacat.chat.run=$RunId",'--network',$runtime.network,'--network-alias','chat-redis','-p',"127.0.0.1:$($runtime.redisPort):6379",'redis:7.4.10-alpine','redis-server','--appendonly','no','--save','','--maxmemory','128mb','--maxmemory-policy','noeviction')
    Save-Manifest

    $runtime.mysqlPort = [int]((Invoke-Docker @('port',$runtime.mysql,'3306/tcp')) -split ':')[-1]
    $runtime.redisPort = [int]((Invoke-Docker @('port',$runtime.redis,'6379/tcp')) -split ':')[-1]
    $runtime.status = 'STARTED'
    Save-Manifest
    Write-Output $manifestPath
} finally {
    $env:MYSQL_ROOT_PASSWORD = $previousPassword
    $password = $null
}
