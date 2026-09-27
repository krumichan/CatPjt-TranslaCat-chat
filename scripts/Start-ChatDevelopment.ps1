param(
    [Parameter(Mandatory)][ValidateSet('BE', 'CHAT', 'AI', 'Redis')][string]$Service,
    [string]$ConfigurationFile = (Join-Path $PSScriptRoot '../deploy/local/Development.json'),
    [switch]$NoBuild,
    [switch]$ValidateOnly,
    [switch]$DisableAiWarmup
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChatDevelopmentConfiguration.ps1')
$c = Get-ChatDevelopmentConfiguration $ConfigurationFile
$repository = Split-Path -Parent $PSScriptRoot
$parent = Split-Path -Parent $repository
$secret = $c.SecretDirectory
$bindings = Get-ChatDevelopmentBindings $c

# manifest 변경 후 오래된 생성 파일을 사용하지 않는다. 비밀 내용은 인자에 넣지 않는다.
foreach ($pair in @(@('chat-environment.json', $bindings.Chat), @('be.properties', $bindings.Be))) {
    $path = Join-Path $c.GeneratedDirectory $pair[0]
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [IO.File]::ReadAllText($path) -cne $pair[1]) {
        throw 'Development bindings are missing or stale. Run Initialize-ChatDevelopment.ps1 with this manifest first.'
    }
}

switch ($Service) {
    'Redis' {
        & (Join-Path $PSScriptRoot 'Start-ChatRedisLocal.ps1') -ConfigurationFile $ConfigurationFile -ValidateOnly:$ValidateOnly
    }
    'CHAT' {
        & (Join-Path $PSScriptRoot 'Start-ChatLocal.ps1') -Environment Development -Port $c.ChatPort `
            -ConfigurationFile (Join-Path $c.GeneratedDirectory 'chat-environment.json') -NoBuild:$NoBuild -ValidateConfiguration:$ValidateOnly
    }
    'BE' {
        $beRoot = Join-Path $parent 'CatPjt-TranslaCat-be'
        if (-not $NoBuild -and -not $ValidateOnly) { & (Join-Path $beRoot 'scripts/Build-ChatProxyLocal.ps1') }
        & (Join-Path $beRoot 'scripts/Start-ChatProxyLocal.ps1') -JarPath $c.BeJarPath `
            -ConfigurationFile (Join-Path $c.GeneratedDirectory 'be.properties') -LocalSecretsConfigurationFile $c.BeLocalSecretsFile `
            -BeToChatSigningKeyFile (Join-Path $secret 'be-to-chat-service-signing-key') `
            -ChatToBeSigningKeyFile (Join-Path $secret 'chat-to-be-service-signing-key') `
            -Environment Development -ChatBaseUrl "http://127.0.0.1:$($c.ChatPort)" -Port $c.BePort -ValidateOnly:$ValidateOnly
    }
    'AI' {
        & (Join-Path $parent 'CatPjt-TranslaCat-ai/scripts/Start-ChatAiLocal.ps1') -Environment Development `
            -ApiKeyFile (Join-Path $secret 'chat-to-ai-api-key') -EnvironmentFile $c.AiEnvironmentFile `
            -Port $c.AiPort -ValidateOnly:$ValidateOnly -DisableWarmup:$DisableAiWarmup
    }
}
