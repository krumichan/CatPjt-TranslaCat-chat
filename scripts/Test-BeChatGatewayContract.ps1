param(
    [string]$BeRoot = (Join-Path $PSScriptRoot '../../CatPjt-TranslaCat-be'),
    [string]$Dotnet = 'C:/Program Files/dotnet/dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$chatRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bePath = [IO.Path]::GetFullPath($BeRoot)
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $chatRoot '../.codex-workspace/verification/chat/TestResults'))
$runRoot = Join-Path $resultsRoot ('Handoff/GatewayContract/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$buildRoot = (Join-Path $runRoot 'be-build').Replace('\', '/').Replace("'", "\'")
$classPathFile = (Join-Path $runRoot 'classpath.txt').Replace('\', '/')
$groovyClasspath = $classPathFile.Replace("'", "\'")
$initFile = Join-Path $runRoot 'isolated.gradle'
@"
allprojects {
    layout.buildDirectory = file('$buildRoot')
    tasks.register('writeChatGatewayContractClasspath') {
        dependsOn testClasses
        doLast { file('$groovyClasspath').text = sourceSets.test.runtimeClasspath.asPath }
    }
}
"@ | Set-Content -LiteralPath $initFile -Encoding utf8

# 기존 BE/LL build/live 설정 대신 독립 test classpath를 사용한다.
Push-Location $bePath
try {
    & ./gradlew.bat --init-script $initFile writeChatGatewayContractClasspath --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'BE gateway fixture compilation failed.' }
} finally { Pop-Location }
$project = Join-Path $runRoot 'GatewayProbe.csproj'
$reference = [Security.SecurityElement]::Escape((Join-Path $chatRoot 'TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj'))
$source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'fixtures/BeGatewayContractProbe.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /><Compile Include="$source" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8
& $Dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'CHAT gateway fixture compilation failed.' }

$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$keys = @{}
foreach ($purpose in @('USER', 'INGRESS', 'CORE')) {
    $keys[$purpose] = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
}

function New-FixtureProcess([string]$executable) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $runRoot
    $info.Environment['CHAT_GATEWAY_CONTRACT_TEST'] = 'true'
    foreach ($purpose in @('USER', 'INGRESS', 'CORE')) { $info.Environment['CHAT_GATEWAY_CONTRACT_' + $purpose + '_KEY'] = $keys[$purpose] }
    return $info
}

try {
    # 먼저 실제 CHAT Kestrel을 시작한다. 사용자/서비스 키는 자식 환경에만 전달한다.
    $probeInfo = New-FixtureProcess $Dotnet
    $probeInfo.ArgumentList.Add((Join-Path $runRoot 'bin/Debug/net10.0/GatewayProbe.dll'))
    $probeInfo.Environment['CHAT_GATEWAY_CONTRACT_DIRECTORY'] = $runRoot
    $probe = [Diagnostics.Process]::Start($probeInfo)
    $processes.Add($probe)
    $probeOutput = $probe.StandardOutput.ReadToEndAsync()
    $probeError = $probe.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (!(Test-Path -LiteralPath (Join-Path $runRoot 'chat.json'))) {
        if ($probe.HasExited) { throw 'CHAT gateway fixture exited before readiness.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'CHAT gateway fixture readiness timed out.' }
        Start-Sleep -Milliseconds 100
    }
    $chatOrigin = (Get-Content -Raw -LiteralPath (Join-Path $runRoot 'chat.json') | ConvertFrom-Json).origin
    if ($chatOrigin -notmatch '^http://127\.0\.0\.1:[0-9]+$') { throw 'Unexpected CHAT fixture origin.' }

    # 실제 BE proxy/identity/WS를 CHAT에 연결한다. 기존 application 설정은 읽지 않는 test main이다.
    $arguments = Join-Path $runRoot 'java.args'
    $escapedClassPath = (Get-Content -Raw -LiteralPath $classPathFile).Replace('\', '/').Replace('"', '\"')
    [IO.File]::WriteAllText($arguments, "-cp`n`"$escapedClassPath`"`njp.co.translacat.infrastructure.chat.gateway.ChatGatewayContractServer`n", [Text.UTF8Encoding]::new($false))
    $javaInfo = New-FixtureProcess (Get-Command java -ErrorAction Stop).Source
    $javaInfo.ArgumentList.Add('@' + $arguments)
    $javaInfo.Environment['CHAT_GATEWAY_CONTRACT_ORIGIN'] = $chatOrigin
    $javaInfo.Environment['CHAT_GATEWAY_CONTRACT_MANIFEST'] = Join-Path $runRoot 'be.json'
    $java = [Diagnostics.Process]::Start($javaInfo)
    $processes.Add($java)
    $javaOutput = $java.StandardOutput.ReadToEndAsync()
    $javaError = $java.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(75)
    while (!$probe.HasExited) {
        if ($java.HasExited) { throw 'BE gateway fixture exited before probe completion.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Gateway contract probe timed out.' }
        Start-Sleep -Milliseconds 100
    }
    if ($probe.ExitCode -ne 0) { throw 'BE to CHAT gateway contract probe failed.' }
    $summary = ($probeOutput.GetAwaiter().GetResult() -split "`r?`n" | Where-Object { $_.StartsWith('{"probe":') } | Select-Object -Last 1)
    if (!$summary) { throw 'Gateway probe summary missing.' }
    $result = $summary | ConvertFrom-Json
    if ($result.passed -lt 16 -or $result.failed -ne 0) { throw 'Gateway contract assertions were incomplete.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), $summary, [Text.UTF8Encoding]::new($false))
    Write-Output $summary
    Write-Output ('EvidenceDirectory=' + $runRoot)
} finally {
    # 이 script가 소유한 두 process만 종료한다. LL/BE 기존 서버와 DB/Redis에 접근하지 않는다.
    foreach ($process in $processes) {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        $process.Dispose()
    }
    $keys.Clear()
}
