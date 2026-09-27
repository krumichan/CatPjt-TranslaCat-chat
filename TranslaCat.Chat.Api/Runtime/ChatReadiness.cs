using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.Api.Runtime;

public static class ChatReadiness
{
    public static WebApplication MapChatReadiness(this WebApplication app)
    {
        // 기존 /api/health의 liveness 계약과 구분한다. endpoint/secret/예외 원문을 공개하지 않는다.
        app.MapGet("/api/ready", (Func<HttpContext, Task<IResult>>)CheckAsync);
        return app;
    }

    private static async Task<IResult> CheckAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var services = context.RequestServices;
        var databaseReady = false;
        var redisReady = false;

        try
        {
            var factory = services.GetService<IDbContextFactory<ChatDbContext>>();
            if (factory is not null)
            {
                await using var database = await factory.CreateDbContextAsync(deadline.Token);
                databaseReady = await database.Database.CanConnectAsync(deadline.Token)
                    && !(await database.Database.GetPendingMigrationsAsync(deadline.Token)).Any();
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            databaseReady = false;
        }

        try
        {
            var connection = services.GetService<IConnectionMultiplexer>();
            if (connection?.IsConnected == true)
            {
                await connection.GetDatabase().PingAsync().WaitAsync(deadline.Token);
                redisReady = true;
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            redisReady = false;
        }

        // 서명 키만 준비된 것을 계정 서비스 연결 완료라고 하지 않는다.
        var jwt = services.GetService<IOptions<ChatJwtOptions>>()?.Value;
        var identityConfigured = services.GetService<IChatIdentityResolver>() is not null;
        var authenticationConfigured = jwt?.Enabled == true && HasSigningKey(jwt.Base64SigningKey) && identityConfigured;
        var ingress = services.GetService<IOptions<ChatServiceIngressOptions>>()?.Value;
        var ingressConfigured = ingress?.Enabled == true && ChatServiceJwt.IsConfigured(ingress);
        var testHost = services.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing");
        var relayReady = services.GetService<ChatRealtimeRedisRelay>()?.IsSubscribed == true && redisReady;
        var presenceEnabled = services.GetService<ChatPresenceOptions>()?.Enabled == true;
        var presenceProfilesConfigured = services.GetService<IChatPresenceProfileReader>() is not null;
        var presenceReady = !presenceEnabled || (redisReady
            && services.GetService<RedisChatPresencePublisher>()?.IsSubscribed == true && presenceProfilesConfigured);

        // 호스트의 암묵적인 local zone을 기존 데이터의 시간 정책으로 확정하지 않는다.
        var sourceTimeZoneConfigured = !string.IsNullOrWhiteSpace(
            services.GetRequiredService<IConfiguration>()["Chat:SourceTimeZone"]);
        var readReady = databaseReady && relayReady && authenticationConfigured && sourceTimeZoneConfigured
            && (testHost || ingressConfigured);
        var profilesConfigured = services.GetService<IChatMessageProfileReader>() is not null;
        var translationConfigured = services.GetService<IChatTranslationDispatcher>()?.IsConfigured == true;
        var aiConfigured = services.GetService<IChatAiMessageDispatcher>()?.IsConfigured == true;
        var roomAccountsConfigured = services.GetService<IChatRoomAccountReader>() is not null;
        var membershipDirectoryConfigured = services.GetService<IChatMembershipDirectory>() is not null;
        var memberProfilesConfigured = services.GetService<IChatMemberProfileReader>() is not null;
        var notificationProfilesConfigured = services.GetService<IChatNotificationProfileReader>() is not null;
        var openStorage = services.GetService<IOpenProfileStorage>();
        var aiStorage = services.GetService<IChatAiProfileStorage>();
        var openStorageConfigured = openStorage is not null;
        var aiStorageConfigured = aiStorage is not null;

        // 이미지 업무가 사용하는 바로 그 URL storage가 upload/delete도 지원하는지 확인한다.
        var imageUploadsConfigured = openStorage is IChatProfileImageObjectStore
            && aiStorage is IChatProfileImageObjectStore;
        var messageConfigured = profilesConfigured && translationConfigured && aiConfigured;
        var ready = readReady && presenceReady && messageConfigured && roomAccountsConfigured
            && membershipDirectoryConfigured && memberProfilesConfigured && notificationProfilesConfigured
            && openStorageConfigured && aiStorageConfigured && imageUploadsConfigured;

        context.RequestAborted.ThrowIfCancellationRequested();
        return Results.Json(new
        {
            status = ready ? "READY" : "NOT_READY",
            scope = "IMPLEMENTED_CAPABILITIES",
            database = databaseReady ? "READY" : "NOT_READY",
            redis = redisReady ? "READY" : "NOT_READY",
            realtime = relayReady ? "READY" : "NOT_READY",
            authentication = authenticationConfigured ? "CONFIGURED" : "NOT_CONFIGURED",
            serviceIngress = ingressConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            identity = identityConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            sourceTimeZone = sourceTimeZoneConfigured ? "CONFIGURED_NOT_VERIFIED" : "NOT_CONFIGURED",
            presence = !presenceEnabled ? "DISABLED" : presenceReady ? "READY" : "NOT_READY",
            presenceProfiles = presenceProfilesConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            read = readReady ? "READY" : "NOT_READY",
            messages = messageConfigured && readReady ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            roomAccounts = roomAccountsConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            membershipDirectory = membershipDirectoryConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            memberProfiles = memberProfilesConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            notificationProfiles = notificationProfilesConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            openStorage = openStorageConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            aiStorage = aiStorageConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            imageUploads = imageUploadsConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            translation = translationConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            ai = aiConfigured ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED"
        }, statusCode: ready ? 200 : 503);
    }

    private static bool HasSigningKey(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && Convert.FromBase64String(value).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
