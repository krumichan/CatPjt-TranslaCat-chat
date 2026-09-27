using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.ApiTests.Messaging;

internal sealed class ChatMessageHttpFixture(WebApplication application, HttpClient client, SyntheticMessageTransaction transaction) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public SyntheticMessageTransaction Transaction { get; } = transaction;

    public static async Task<ChatMessageHttpFixture> StartAsync(bool registerTransaction = true, bool registerAuthentication = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatMessageHttp();
        builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        var transaction = new SyntheticMessageTransaction();
        if (registerTransaction)
        {
            builder.Services.AddSingleton<IChatMessageTransaction>(transaction);
        }
        if (registerAuthentication)
        {
            // JWT 자체는 별도 Authentication suite에서 실제 검증한다. 여기서는 HTTP 입력/업무/출력 경계를 집중 검증한다.
            builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        }

        var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new ChatMessageHttpFixture(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) }, transaction);
    }

    public async Task<HttpResponseMessage> SendAsync(string path = "/api/v1/chat/rooms/41/messages", string? body = null, string? identity = "73", string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }
        if (identity is not null)
        {
            request.Headers.Add(TestReadAuthenticationHandler.HeaderName, identity);
        }
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}

internal sealed class SyntheticMessageTransaction : IChatMessageTransaction, IChatMessageSession
{
    public static readonly DateTime LocalNow = DateTime.SpecifyKind(FixedReadTimeProvider.Now.DateTime, DateTimeKind.Unspecified);
    public int ExecuteCount
    {
        get; private set;
    }
    public Exception? Failure
    {
        get; set;
    }
    public long UserId
    {
        get; private set;
    }
    public long RoomId
    {
        get; private set;
    }
    public long? CursorId
    {
        get; private set;
    }
    public bool Forward
    {
        get; private set;
    }
    public int Limit
    {
        get; private set;
    }
    public string? InsertedContent
    {
        get; private set;
    }
    public List<ChatMessageIntent> Registered { get; } = [];
    public IReadOnlyList<ChatStoredMessage> Rows { get; set; } = [Row(101)];
    public bool AnchorAccessible { get; set; } = true;

    public async Task<T> ExecuteAsync<T>(Func<IChatMessageSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        if (Failure is not null)
        {
            throw Failure;
        }
        return await work(this, cancellationToken);
    }

    public Task<ChatMessageMember> GetMemberAsync(long userId, long roomId, bool forSend, CancellationToken cancellationToken)
    {
        UserId = userId;
        RoomId = roomId;
        return Task.FromResult(new ChatMessageMember(7, userId, roomId, "DIRECT", LocalNow.AddDays(-1)));
    }

    public Task<IReadOnlyList<ChatStoredMessage>> FetchAsync(ChatMessageMember member, long? cursorId, bool forward, int limit, CancellationToken cancellationToken)
    {
        CursorId = cursorId;
        Forward = forward;
        Limit = limit;
        // 조회 결과를 사전에 제공할 뿐 pagination/cursor 정책을 fake에서 재구현하지 않는다.
        return Task.FromResult(Rows);
    }

    public Task<ChatStoredMessage?> FindAccessibleAsync(ChatMessageMember member, long messageId, CancellationToken cancellationToken)
    {
        return Task.FromResult<ChatStoredMessage?>(AnchorAccessible ? Row(messageId) : null);
    }

    public Task<IReadOnlyList<ChatMessageView>> PresentAsync(ChatMessageMember member, IReadOnlyList<ChatStoredMessage> messages, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ChatMessageView>>(messages.Select(message => View(message)).ToArray());
    }

    public Task<ChatMessageLanguages> ResolveLanguagesAsync(ChatMessageMember member, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChatMessageLanguages("ko", ["ko"]));
    }

    public Task<ChatMessageCreation> InsertTextAsync(ChatMessageMember member, string content, IReadOnlyList<string> translationLanguages, DateTime createdAt, CancellationToken cancellationToken)
    {
        InsertedContent = content;
        return Task.FromResult(new ChatMessageCreation(Row(101) with
        {
            Content = content
        }, []));
    }

    public void RegisterAfterCommit(ChatMessageIntent intent)
    {
        Registered.Add(intent);
    }

    public static ChatStoredMessage Row(long id)
    {
        return new(id, 41, 73, null, "USER", "TEXT", "합성 원문", "SENT", LocalNow, LocalNow);
    }

    public static ChatMessageView View(ChatStoredMessage? message = null)
    {
        var row = message ?? Row(101);
        return new(row.Id, row.ChatRoomId, row.SenderUserId, row.SenderAiMemberId,
            "합성 사용자", "synthetic@example.invalid", null, row.SenderType, row.MessageType, row.Content, row.Status,
            3, [new ChatMessageTranslationView(201, "en", null, "PENDING", null, null)], row.CreatedAt, row.UpdatedAt, null);
    }
}
