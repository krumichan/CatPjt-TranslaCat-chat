# Development 실행에 필요한 비밀 없는 manifest만 읽는다. 상대 경로는 manifest 디렉터리 기준이다.
function Get-ChatDevelopmentConfiguration([string]$ConfigurationFile) {
    $document = $null
    try {
        $path = [IO.Path]::GetFullPath($ConfigurationFile)
        $content = [IO.File]::ReadAllText($path)
        $document = [Text.Json.JsonDocument]::Parse($content)
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expected = @('Environment', 'ChatPort', 'BePort', 'AiPort', 'AiEnabled', 'TranslationEnabled', 'SourceTimeZone', 'BrowserOrigin',
            'RedisEndpoint', 'RedisNamespace', 'SecretDirectory', 'BeLocalSecretsFile', 'AiEnvironmentFile', 'BeJarPath')
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $names.Add($property.Name) -or $property.Name -cnotin $expected) { throw 'Invalid property.' }
        }
        if ($names.Count -ne $expected.Count) { throw 'Missing property.' }
        $configuration = $content | ConvertFrom-Json -AsHashtable
        if ($configuration.Environment -cne 'Development') { throw 'Invalid environment.' }
        foreach ($name in @('ChatPort', 'BePort', 'AiPort')) {
            if ($configuration[$name] -isnot [long] -and $configuration[$name] -isnot [int]) { throw 'Invalid port.' }
            if ($configuration[$name] -lt 1024 -or $configuration[$name] -gt 65535) { throw 'Invalid port.' }
        }
        if (@($configuration.ChatPort, $configuration.BePort, $configuration.AiPort | Select-Object -Unique).Count -ne 3) {
            throw 'Ports must differ.'
        }
        foreach ($name in @('AiEnabled', 'TranslationEnabled')) {
            if ($configuration[$name] -isnot [bool]) { throw 'Invalid activation flag.' }
        }
        foreach ($name in $expected | Where-Object { $_ -notin @('ChatPort', 'BePort', 'AiPort', 'AiEnabled', 'TranslationEnabled') }) {
            if ($configuration[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($configuration[$name]) -or
                $configuration[$name] -match '[\x00-\x1f\x7f]') { throw 'Invalid string.' }
        }
        $null = [TimeZoneInfo]::FindSystemTimeZoneById($configuration.SourceTimeZone)
        $origin = [uri]$configuration.BrowserOrigin
        if (-not $origin.IsAbsoluteUri -or -not $origin.IsLoopback -or $origin.Scheme -notin @('http', 'https') -or
            $origin.UserInfo -or $origin.Query -or $origin.Fragment -or $origin.AbsolutePath -ne '/' -or
            $configuration.BrowserOrigin -cne $origin.GetLeftPart([UriPartial]::Authority)) { throw 'Invalid origin.' }
        if ($configuration.RedisEndpoint -notmatch '^127\.0\.0\.1:([0-9]{4,5})$' -or
            [int]$Matches[1] -lt 1024 -or [int]$Matches[1] -gt 65535) { throw 'Invalid Redis endpoint.' }
        if ([int]$Matches[1] -in @($configuration.ChatPort, $configuration.BePort, $configuration.AiPort)) {
            throw 'Redis and application ports must differ.'
        }
        if ($configuration.RedisNamespace -notmatch '^translacat:chat:Development(?:[:][A-Za-z0-9_-]+)?$') { throw 'Invalid namespace.' }

        $directory = Split-Path -Parent $path
        foreach ($name in @('SecretDirectory', 'BeLocalSecretsFile', 'AiEnvironmentFile', 'BeJarPath')) {
            $configuration[$name] = [IO.Path]::GetFullPath($configuration[$name], $directory)
        }
        $configuration.GeneratedDirectory = Join-Path $directory 'generated'
        $configuration.ManifestPath = $path
        return $configuration
    } catch {
        throw 'Invalid Development manifest. Check required names, loopback addresses, ports and paths; secret values are not accepted.'
    } finally {
        if ($document) { $document.Dispose() }
    }
}

function Get-ChatDevelopmentBindings([hashtable]$Configuration) {
    $c = $Configuration
    $secret = $c.SecretDirectory
    $chat = [ordered]@{
        DOTNET_ENVIRONMENT = 'Development'; ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = "http://127.0.0.1:$($c.ChatPort)"; AllowedHosts = 'localhost;127.0.0.1'
        Chat__SourceTimeZone = $c.SourceTimeZone
        Chat__Database__ConnectionString_FILE = (Join-Path $secret 'chat-database-runtime')
        Chat__Authentication__Enabled = 'true'
        Chat__Authentication__Base64SigningKey_FILE = (Join-Path $secret 'be-user-jwt-signing-key')
        Chat__ServiceAuthentication__Ingress__Enabled = 'true'
        Chat__ServiceAuthentication__Ingress__Issuer = 'translacat-be'
        Chat__ServiceAuthentication__Ingress__Audience = 'translacat-chat'
        Chat__ServiceAuthentication__Ingress__Service = 'translacat-be'
        Chat__ServiceAuthentication__Ingress__Base64SigningKey_FILE = (Join-Path $secret 'be-to-chat-service-signing-key')
        Chat__Redis__Endpoint = $c.RedisEndpoint; Chat__Redis__User = 'chat'
        Chat__Redis__Password_FILE = (Join-Path $secret 'redis-password')
        Chat__Redis__UseTls = 'false'; Chat__Redis__AllowPrivatePlaintext = 'true'
        Chat__Redis__Namespace = $c.RedisNamespace; Chat__Presence__Enabled = 'true'
        Chat__Realtime__AllowedOrigins__0 = $c.BrowserOrigin
        Logging__LogLevel__Default = 'Warning'; Logging__LogLevel__Microsoft = 'Warning'
    }
    foreach ($section in @('Identity', 'Core')) {
        $chat["Chat__${section}__Enabled"] = 'true'
        $chat["Chat__${section}__BaseUrl"] = "http://127.0.0.1:$($c.BePort)"
        $chat["Chat__${section}__TimeoutSeconds"] = '5'
        $chat["Chat__${section}__ServiceAuthentication__Issuer"] = 'translacat-chat'
        $chat["Chat__${section}__ServiceAuthentication__Audience"] = 'translacat-be'
        $chat["Chat__${section}__ServiceAuthentication__Service"] = 'translacat-chat'
        $chat["Chat__${section}__ServiceAuthentication__Base64SigningKey_FILE"] = Join-Path $secret 'chat-to-be-service-signing-key'
    }
    foreach ($section in @('Ai', 'Translation')) {
        $chat["Chat__${section}__Enabled"] = $c[($section + 'Enabled')].ToString().ToLowerInvariant()
        $chat["Chat__${section}__AiBaseUri"] = "http://127.0.0.1:$($c.AiPort)"
        $chat["Chat__${section}__ApiKey_FILE"] = Join-Path $secret 'chat-to-ai-api-key'
    }

    # BE는 기존 계정 DB와 JWT 공급원을 유지한다. schema 생성은 모든 일상 실행에서 금지한다.
    $be = @('server.address=127.0.0.1', "server.port=$($c.BePort)",
        'spring.jpa.hibernate.ddl-auto=none', 'spring.jpa.show-sql=false',
        'chat.gateway.enabled=true', "chat.gateway.base-url=http://127.0.0.1:$($c.ChatPort)",
        'chat.gateway.environment=Development', 'chat.gateway.issuer=translacat-be',
        'chat.gateway.audience=translacat-chat', 'chat.gateway.service=translacat-be',
        'chat.core.identity.enabled=true', 'chat.core.identity.environment=Development',
        'chat.core.identity.issuer=translacat-chat', 'chat.core.identity.audience=translacat-be',
        'chat.core.identity.service=translacat-chat', 'logging.level.root=WARN',
        'logging.level.jdbc=OFF', 'logging.level.jdbc.sqlonly=OFF', 'logging.level.jdbc.sqltiming=OFF',
        'logging.level.jdbc.audit=OFF', 'logging.level.jdbc.resultset=OFF',
        'logging.level.jdbc.resultsettable=OFF', 'logging.level.jdbc.connection=OFF',
        'logging.level.jp.co.translacat.global.logging=OFF') -join [Environment]::NewLine
    return @{ Chat = ($chat | ConvertTo-Json -Depth 5); Be = $be }
}
