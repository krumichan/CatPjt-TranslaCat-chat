param(
    [Parameter(Mandatory)][ValidatePattern('^translacat-chat:[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$')][string]$Image
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$runId = [Guid]::NewGuid().ToString('N')
$owner = 'chat-image-configuration-validation'
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $workspace '../.codex-workspace/verification/chat/TestResults'))
$output = Join-Path $resultsRoot "ConfigurationImage/$runId"
$context = Join-Path $output 'context'
New-Item -ItemType Directory -Path $context | Out-Null
$manifestPath = Join-Path $output 'manifest.json'
$imageId = & docker image inspect --format '{{.Id}}' $Image
if ($LASTEXITCODE -ne 0 -or $imageId -notmatch '^sha256:[a-f0-9]{64}$') { throw 'A previously built local CHAT image is required.' }
$runtime = [ordered]@{
    testMode = $true; runId = $runId; owner = $owner; image = $Image; imageId = $imageId
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o'); scope = 'LINUX_IMAGE_AND_SYNTHETIC_DOCKER_CONTEXT_ONLY'
    container = $null; contextProbeImage = $null; checks = @(); status = 'PREPARED'
}
function Save-Manifest {
    $runtime | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
Save-Manifest

# 준비: 실제 private 파일은 복사/열람하지 않는다. 같은 경로 형태의 합성 sentinel만 새 context에 만든다.
$included = @(
    'TranslaCat.Chat.Api/appsettings.json',
    'TranslaCat.Chat.Api/appsettings.Development.json',
    'TranslaCat.Chat.Api/appsettings.Production.json'
)
$excluded = @(
    '.env', '.env.local', '.env.prod', '.git/config', 'TestResults/private', '.secrets/development/private',
    'TranslaCat.Chat.Api/.env', 'TranslaCat.Chat.Api/.env.prod', 'TranslaCat.Chat.Api/secrets.json',
    'TranslaCat.Chat.Api/user.secrets.json', 'TranslaCat.Chat.Api/secrets/private', 'TranslaCat.Chat.Api/.secrets/private',
    'TranslaCat.Chat.Api/private.key', 'TranslaCat.Chat.Api/private.pem', 'TranslaCat.Chat.Api/private.pfx',
    'TranslaCat.Chat.Api/private.p12', 'TranslaCat.Chat.Api/bin/private', 'TranslaCat.Chat.Api/obj/private',
    'TranslaCat.Chat.Api/Properties/launchSettings.json', 'TranslaCat.Chat.Api/appsettings.Local.json',
    'TranslaCat.Chat.Api/appsettings.production.json',
    'deploy/chat/.releases/synthetic-sha/.env',
    'deploy/chat/.releases/synthetic-sha/secrets/chat_ai_api_key'
)
foreach ($path in $included + $excluded) {
    $absolute = Join-Path $context $path
    [IO.Directory]::CreateDirectory((Split-Path -Parent $absolute)) | Out-Null
    [IO.File]::WriteAllText($absolute, 'synthetic-context-probe-only', [Text.Encoding]::ASCII)
}
$dockerfile = Join-Path $context 'deploy/chat/Dockerfile'
[IO.Directory]::CreateDirectory((Split-Path -Parent $dockerfile)) | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'deploy/chat/Dockerfile.dockerignore') -Destination "$dockerfile.dockerignore"
$assertions = @($included | ForEach-Object { "test -f /context/$_" }) + @($excluded | ForEach-Object { "test ! -e /context/$_" })
[IO.File]::WriteAllText($dockerfile, "FROM $Image`nCOPY . /context/`nRUN " + ($assertions -join ' && ') + "`n", [Text.Encoding]::ASCII)
$probeTag = "translacat-chat:context-probe-$runId"
$verified = $false
try {
    # 실행: Docker 자신의 ignore 해석을 사용한다. build context는 합성 파일뿐이며 network는 비활성이다.
    & docker build --network none --pull=false --label "translacat.chat.run=$runId" --label "translacat.chat.owner=$owner" `
        --file $dockerfile --tag $probeTag $context *> (Join-Path $output 'context-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Docker context inclusion/exclusion checks failed; inspect the safe build log.' }
    $runtime.contextProbeImage = & docker image inspect --format '{{.Id}}' $probeTag
    if ($LASTEXITCODE -ne 0 -or $runtime.contextProbeImage -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Context probe image identity was unavailable.' }
    $runtime.checks += "Docker context: $($included.Count) exact JSON paths included; $($excluded.Count) private or incorrect-case paths excluded"
    Save-Manifest

    # 최종 애플리케이션 image에서는 app을 시작하지 않고 실제 /app 파일 목록만 검증한다.
    $probe = @'
set -eu
test -r /app/appsettings.json
test -r /app/appsettings.Development.json
test -r /app/appsettings.Production.json
test ! -e /app/appsettings.production.json
private_paths=$(find /app \( -iname '.git' -o -iname 'TestResults' -o -iname 'secrets' -o -iname '.secrets' -o -iname '.env*' -o -iname 'secrets.json' -o -iname '*.secrets.json' -o -iname '*.key' -o -iname '*.pem' -o -iname '*.pfx' -o -iname '*.p12' -o -iname 'launchSettings.json' \) -print)
test -z "$private_paths"
printf 'LINUX_CONFIGURATION_FILES_VERIFIED\n'
'@
    $runtime.container = & docker create --read-only --network none --cap-drop ALL --security-opt no-new-privileges:true `
        --label "translacat.chat.run=$runId" --label "translacat.chat.owner=$owner" --entrypoint /bin/sh $Image -c $probe
    if ($LASTEXITCODE -ne 0 -or $runtime.container -notmatch '^[a-f0-9]{64}$') { throw 'Image inspection container creation failed.' }
    Save-Manifest
    $result = & docker start --attach $runtime.container
    $containerExit = & docker inspect --format '{{.State.ExitCode}}' $runtime.container
    if ($LASTEXITCODE -ne 0 -or $containerExit -ne '0' -or $result -ne 'LINUX_CONFIGURATION_FILES_VERIFIED') {
        throw 'The actual Linux image configuration/private-path inspection failed.'
    }
    $runtime.checks += 'Actual Linux image: three readable JSON files; private paths and wrong-case Production file absent'
    $verified = $true
} finally {
    # 이 실행에서 생성한 exact ID와 두 label을 검사한다. 기존 image나 다른 container는 정리하지 않는다.
    if ($runtime.container) {
        $identity = & docker inspect --format '{{.Id}} {{index .Config.Labels "translacat.chat.run"}} {{index .Config.Labels "translacat.chat.owner"}}' $runtime.container
        if ($LASTEXITCODE -ne 0 -or $identity -ne "$($runtime.container) $runId $owner") { throw 'Owned image inspection container identity mismatch.' }
        & docker rm $runtime.container | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned image inspection container cleanup failed.' }
    }
    if ($runtime.contextProbeImage) {
        $identity = & docker image inspect --format '{{.Id}} {{index .Config.Labels "translacat.chat.run"}} {{index .Config.Labels "translacat.chat.owner"}}' $runtime.contextProbeImage
        if ($LASTEXITCODE -ne 0 -or $identity -ne "$($runtime.contextProbeImage) $runId $owner") { throw 'Owned context probe image identity mismatch.' }
        & docker image rm $runtime.contextProbeImage | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Owned context probe image cleanup failed.' }
    }
    $runtime.status = if ($verified) { 'VERIFIED_CLEANED' } else { 'FAILED_CLEANED' }
    $runtime.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Manifest
}
Write-Output "Linux image and Docker context checks passed. Evidence: $manifestPath"
