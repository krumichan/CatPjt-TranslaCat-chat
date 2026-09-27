param([Parameter(Mandatory)][string]$Manifest, [switch]$RemoveSyntheticResources)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$allowedRoot = Join-Path $resultsRoot 'V2\Runtime\'
if (-not $manifestPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest is outside the owned runtime directory.' }
$runtime = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $runtime.testMode -or $runtime.runId -notmatch '^[a-f0-9]{32}$') { throw 'Not a test manifest.' }
$docker = (Get-Command docker -ErrorAction Stop).Source

# 모든 대상의 정확한 ID/name과 label을 먼저 검증한다. prefix만으로 삭제하지 않는다.
foreach ($id in @($runtime.mysql, $runtime.redis)) {
    if ($id -notmatch '^[a-f0-9]{64}$') { throw 'Missing exact container ID.' }
    $label = & $docker inspect --format '{{index .Config.Labels "translacat.chat.run"}}' $id
    if ($LASTEXITCODE -ne 0 -or $label -ne $runtime.runId) { throw 'Container ownership mismatch.' }
}
foreach ($kind in @('network','volume')) {
    $id = $runtime.$kind
    if ([string]::IsNullOrWhiteSpace($id)) { throw 'Missing resource ID.' }
    $label = & $docker $kind inspect --format '{{index .Labels "translacat.chat.run"}}' $id
    if ($LASTEXITCODE -ne 0 -or $label -ne $runtime.runId) { throw 'Resource ownership mismatch.' }
}

foreach ($id in @($runtime.redis, $runtime.mysql)) {
    & $docker stop --time 10 $id
    if ($LASTEXITCODE -ne 0) { throw 'Owned container stop failed.' }
}
$runtime.status = 'STOPPED'

if ($RemoveSyntheticResources) {
    # 이번 manifest의 합성 자원만 제거한다. 일반 translacat_chat/shared DB, prune/down-v는 대상이 아니다.
    & $docker rm $runtime.redis $runtime.mysql
    if ($LASTEXITCODE -ne 0) { throw 'Owned container removal failed.' }
    & $docker network rm $runtime.network
    if ($LASTEXITCODE -ne 0) { throw 'Owned network removal failed.' }
    & $docker volume rm $runtime.volume
    if ($LASTEXITCODE -ne 0) { throw 'Owned synthetic volume removal failed.' }
    $runtime.status = 'REMOVED_SYNTHETIC_RESOURCES'
}
$runtime | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
