param(
    [string]$BeRoot = (Join-Path $PSScriptRoot '../../CatPjt-TranslaCat-be'),
    [string]$Dotnet = 'C:/Program Files/dotnet/dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$chatRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bePath = [IO.Path]::GetFullPath($BeRoot)
# Codex 검증 산출물은 저장소 밖의 CHAT 전용 경로에 모은다.
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $chatRoot '../.codex-workspace/verification/chat/TestResults'))
$runRoot = Join-Path $resultsRoot ('Handoff/CoreContract/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$buildRoot = (Join-Path $runRoot 'be-build').Replace('\', '/')
$classPathFile = (Join-Path $runRoot 'classpath.txt').Replace('\', '/')
$initFile = Join-Path $runRoot 'isolated.gradle'
$groovyBuild = $buildRoot.Replace("'", "\'")
$groovyClasspath = $classPathFile.Replace("'", "\'")
@"
allprojects {
    layout.buildDirectory = file('$groovyBuild')
    tasks.register('writeChatCoreContractClasspath') {
        dependsOn testClasses
        doLast { file('$groovyClasspath').text = sourceSets.test.runtimeClasspath.asPath }
    }
}
"@ | Set-Content -LiteralPath $initFile -Encoding utf8

# 기존 LL build/live 파일을 사용하지 않고 고정 의존성으로 별도 test classpath를 만든다.
Push-Location $bePath
try {
    & ./gradlew.bat --init-script $initFile writeChatCoreContractClasspath --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'BE fixture compilation failed.' }
} finally { Pop-Location }
$java = (Get-Command java -ErrorAction Stop).Source
$manifest = Join-Path $runRoot 'server.json'
$keyBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(64)
$key = [Convert]::ToBase64String($keyBytes)
$javaInfo = [Diagnostics.ProcessStartInfo]::new($java)
$javaInfo.UseShellExecute = $false
$javaInfo.CreateNoWindow = $true
$javaInfo.RedirectStandardOutput = $true
$javaInfo.RedirectStandardError = $true
$javaInfo.WorkingDirectory = $runRoot
# Windows 명령줄 길이 제한을 피한다. classpath 파일에는 비밀이 없다.
$javaArgumentsFile = Join-Path $runRoot 'java.args'
$escapedClassPath = (Get-Content -Raw -LiteralPath $classPathFile).Replace('\', '/').Replace('"', '\"')
[IO.File]::WriteAllText($javaArgumentsFile, "-cp`n`"$escapedClassPath`"`njp.co.translacat.infrastructure.chat.core.directory.ChatCoreDirectoryContractServer`n", [Text.UTF8Encoding]::new($false))
$javaInfo.ArgumentList.Add('@' + $javaArgumentsFile)
$javaInfo.Environment['CHAT_CORE_CONTRACT_TEST'] = 'true'
$javaInfo.Environment['CHAT_CORE_CONTRACT_KEY'] = $key
$javaInfo.Environment['CHAT_CORE_CONTRACT_MANIFEST'] = $manifest
$server = [Diagnostics.Process]::Start($javaInfo)
$javaOutput = $server.StandardOutput.ReadToEndAsync()
$javaError = $server.StandardError.ReadToEndAsync()

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (!(Test-Path -LiteralPath $manifest)) {
        if ($server.HasExited) { throw 'Isolated BE Core server exited before readiness.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Isolated BE Core server readiness timed out.' }
        Start-Sleep -Milliseconds 100
    }
    $origin = (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json).origin
    if ($origin -notmatch '^http://127\.0\.0\.1:[0-9]+$') { throw 'Unexpected fixture origin.' }
    $project = Join-Path $runRoot 'CoreProbe.csproj'
    $reference = [Security.SecurityElement]::Escape((Join-Path $chatRoot 'TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj'))
    $source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'fixtures/BeCoreContractProbe.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /><Compile Include="$source" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8

    # 비밀은 명령줄/파일에 넣지 않는다. 테스트 소유 자식 process 환경에만 전달한다.
    $probeInfo = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    $probeInfo.UseShellExecute = $false
    $probeInfo.CreateNoWindow = $true
    $probeInfo.RedirectStandardOutput = $true
    $probeInfo.RedirectStandardError = $true
    $probeInfo.WorkingDirectory = $chatRoot
    $probeInfo.ArgumentList.Add('run')
    $probeInfo.ArgumentList.Add('--project')
    $probeInfo.ArgumentList.Add($project)
    $probeInfo.ArgumentList.Add('--verbosity')
    $probeInfo.ArgumentList.Add('quiet')
    $probeInfo.Environment['CHAT_CORE_CONTRACT_TEST'] = 'true'
    $probeInfo.Environment['CHAT_CORE_CONTRACT_KEY'] = $key
    $probeInfo.Environment['CHAT_CORE_CONTRACT_ORIGIN'] = $origin
    $probe = [Diagnostics.Process]::Start($probeInfo)
    $probeOutput = $probe.StandardOutput.ReadToEndAsync()
    $probeError = $probe.StandardError.ReadToEndAsync()
    $probe.WaitForExit()
    if ($probe.ExitCode -ne 0) { throw 'CHAT to BE Core contract probe failed.' }
    $summary = ($probeOutput.GetAwaiter().GetResult() -split "`r?`n" | Where-Object { $_.StartsWith('{"probe":') } | Select-Object -Last 1)
    if (!$summary) { throw 'CHAT to BE Core contract probe did not emit its result.' }
    $result = $summary | ConvertFrom-Json
    if ($result.passed -ne 21 -or $result.failed -ne 0) { throw 'CHAT to BE Core contract assertions were incomplete.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), $summary, [Text.UTF8Encoding]::new($false))
    Write-Output $summary
    Write-Output ('EvidenceDirectory=' + $runRoot)
} finally {
    # 이 script가 만든 process만 종료한다. 기존 BE/LL runtime이나 데이터에는 접근하지 않는다.
    if (!$server.HasExited) { $server.Kill($true) }
    $server.WaitForExit()
    $server.Dispose()
    [Array]::Clear($keyBytes)
}
