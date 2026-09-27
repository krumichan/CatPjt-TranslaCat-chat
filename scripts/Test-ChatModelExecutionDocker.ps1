param(
    [string]$Image = 'translacat-ai:chat-execution-final-20260927',
    [string]$Dotnet = 'C:/Program Files/dotnet/dotnet.exe',
    [string]$IntegrationManifest
)

$ErrorActionPreference = 'Stop'
$chatRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$aiRoot = [IO.Path]::GetFullPath((Join-Path $chatRoot '../CatPjt-TranslaCat-ai'))
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $chatRoot '../.codex-workspace/verification/chat/TestResults/AiExecutionDocker'))
$runRoot = Join-Path $resultsRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

$project = Join-Path $runRoot 'ChatAiExecutionProbe.csproj'
$reference = [Security.SecurityElement]::Escape((Join-Path $chatRoot 'TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj'))
$source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'fixtures/ChatAiAuthContractProbe.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /><Compile Include="$source" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8

foreach ($mode in @('dual', 'dedicated')) {
    # 준비 — 실행별 네트워크 포트·컨테이너·합성 키를 분리한다.
    $working = Join-Path $runRoot $mode
    [IO.Directory]::CreateDirectory($working) | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $origin = "http://127.0.0.1:$port"
    $runId = [Guid]::NewGuid().ToString('N')
    $name = 'chat-ai-execution-' + $runId
    $keyBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $legacyBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $key = [Convert]::ToBase64String($keyBytes)
    $legacy = [Convert]::ToBase64String($legacyBytes)
    $container = $null

    try {
        # Python/FastAPI는 실제 앱 코드를 읽지만 SDK transport는 합성 대역이다.
        $container = (docker run -d --rm --name $name -p "127.0.0.1:${port}:8000" `
            -v "${aiRoot}:/app:ro" -v "${working}:/fixture" -w /fixture `
            -e PYTHONPATH=/app -e CHAT_AUTH_CONTRACT_TEST=true `
            -e CHAT_AUTH_CONTRACT_CONTAINER=true `
            -e CHAT_AUTH_CONTRACT_MANIFEST=/fixture/server.json `
            -e "CHAT_AUTH_CONTRACT_ORIGIN=$origin" -e "CHAT_AUTH_CONTRACT_RUN_ID=$runId" `
            -e "CHAT_AUTH_MODE=$mode" -e CHAT_AUTH_ENVIRONMENT=Development `
            -e "CHAT_SERVER_API_KEY=$key" -e "SERVER_API_KEY=$legacy" `
            -e OPENAI_API_KEY=synthetic-no-provider-key `
            -e GOOGLE_API_KEY=synthetic-no-provider-key `
            -e AI_VOICE_ENABLED=false -e OCR_WARM_UP=false `
            --entrypoint python $Image /app/tests/chat_model_execution_http_fixture.py)
        if ($LASTEXITCODE -ne 0 -or !$container) { throw 'Isolated Python container failed to start.' }

        $deadline = [DateTime]::UtcNow.AddSeconds(40)
        $manifestPath = Join-Path $working 'server.json'
        while (!(Test-Path -LiteralPath $manifestPath)) {
            if ((docker inspect -f '{{.State.Running}}' $name 2>$null) -ne 'true') {
                throw 'Isolated Python container exited before readiness.'
            }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Isolated Python readiness timed out.' }
            Start-Sleep -Milliseconds 200
        }
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        if ($manifest.runId -ne $runId -or $manifest.origin -ne $origin) {
            throw 'Isolated Python manifest did not match owned container.'
        }

        # 실행 — CHAT의 실제 DI/typed HTTP client를 Python API까지 연결한다.
        $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.WorkingDirectory = $chatRoot
        foreach ($arg in @('run', '--project', $project, '--verbosity', 'quiet')) { $start.ArgumentList.Add($arg) }
        $start.Environment['CHAT_AUTH_CONTRACT_TEST'] = 'true'
        $start.Environment['CHAT_AUTH_CONTRACT_KEY'] = $key
        $start.Environment['CHAT_AUTH_CONTRACT_LEGACY_KEY'] = $legacy
        $start.Environment['CHAT_AUTH_CONTRACT_ORIGIN'] = $origin
        $start.Environment['CHAT_AUTH_CONTRACT_MODE'] = $mode
        $probe = [Diagnostics.Process]::Start($start)
        $outputTask = $probe.StandardOutput.ReadToEndAsync()
        $errorTask = $probe.StandardError.ReadToEndAsync()
        if (!$probe.WaitForExit(180000)) { $probe.Kill($true); throw 'CHAT execution probe timed out.' }
        $output = $outputTask.GetAwaiter().GetResult()
        $errorText = $errorTask.GetAwaiter().GetResult()
        $output | Set-Content -LiteralPath (Join-Path $working 'probe.stdout.log') -Encoding utf8
        $errorText | Set-Content -LiteralPath (Join-Path $working 'probe.stderr.log') -Encoding utf8
        if ($probe.ExitCode -ne 0) { throw 'CHAT to Python model execution contract failed.' }
        $summary = @($output -split "`r?`n" | Where-Object { $_.StartsWith('{"probe":') })
        if ($summary.Count -ne 1) { throw 'CHAT execution probe did not emit one result summary.' }
        Write-Output $summary[0]
        $probe.Dispose()

        if ($mode -eq 'dual' -and $IntegrationManifest) {
            # 같은 실제 Python API를 이번 실행의 격리 MySQL/Redis·CHAT HTTP/STOMP 테스트에 연결한다.
            $names = @('CHAT_FINAL_PYTHON_ORIGIN', 'CHAT_FINAL_PYTHON_KEY', 'CHAT_AUTH_CONTRACT_TEST')
            $previous = @{}
            foreach ($variable in $names) { $previous[$variable] = [Environment]::GetEnvironmentVariable($variable) }
            try {
                $env:CHAT_FINAL_PYTHON_ORIGIN = $origin
                $env:CHAT_FINAL_PYTHON_KEY = $key
                $env:CHAT_AUTH_CONTRACT_TEST = 'true'
                & (Join-Path $PSScriptRoot 'Invoke-ChatIntegration.ps1') -Manifest $IntegrationManifest `
                    test TranslaCat.Chat.IntegrationTests/TranslaCat.Chat.IntegrationTests.csproj `
                    -c Release --no-restore `
                    --filter 'FullyQualifiedName~Human_HTTP_can_save_a_reply_from_the_owned_Python_fixture' |
                    Tee-Object -FilePath (Join-Path $working 'mysql-redis-python-runtime.log')
                if ($LASTEXITCODE -ne 0) { throw 'CHAT/MySQL/Redis/Python runtime verification failed.' }
            } finally {
                foreach ($variable in $names) {
                    [Environment]::SetEnvironmentVariable($variable, $previous[$variable])
                }
            }
        }
    } finally {
        # 검증용 컨테이너 한 개만 종료하고 실제 AI/LL 프로세스는 건드리지 않는다.
        if ($container) {
            docker logs $name 2>&1 | Set-Content -LiteralPath (Join-Path $working 'python.log') -Encoding utf8
            docker rm -f $name | Out-Null
        }
        [Array]::Clear($keyBytes)
        [Array]::Clear($legacyBytes)
    }
}

Write-Output ('EvidenceDirectory=' + $runRoot)
