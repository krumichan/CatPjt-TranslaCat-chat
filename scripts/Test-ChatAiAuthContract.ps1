param(
    [string]$AiRoot = (Join-Path $PSScriptRoot '../../CatPjt-TranslaCat-ai'),
    [string]$Dotnet = 'C:/Program Files/dotnet/dotnet.exe',
    [string]$Image = 'translacat-ai:chat-execution-final-20260927',
    [string]$IntegrationManifest
)

$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../CatPjt-TranslaCat-ai'))
if ([IO.Path]::GetFullPath($AiRoot) -ne $expected) {
    throw 'This contract probe requires the sibling AI source tree.'
}

# 실제 FastAPI/Provider adapter는 격리 Docker에서 실행한다. 로컬 .venv 또는 개인 .env를 읽지 않는다.
& (Join-Path $PSScriptRoot 'Test-ChatModelExecutionDocker.ps1') -Image $Image -Dotnet $Dotnet `
    -IntegrationManifest $IntegrationManifest
exit $LASTEXITCODE
