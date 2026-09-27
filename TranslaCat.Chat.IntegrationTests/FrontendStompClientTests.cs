using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class FrontendStompClientTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task ActualFrontendClientAndParser_SendAcrossChatInstancesAndConsumeReadEvent()
    {
        // 준비 — 실제 설치된 FE 패키지/source를 읽지만 FE 작업 공간에는 쓰지 않는다.
        var room = await fixture.SeedReadRoomAsync("DIRECT");
        long readerId = room.UserId + 1;
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            await db.ChatRoomMembers.Where(row => row.Id == room.MemberId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.OriginalLanguageCode, "ko").SetProperty(row => row.TranslationLanguageCode, "ko"));
            db.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = readerId,
                Role = "MEMBER",
                OriginalLanguageCode = "ko",
                TranslationLanguageCode = "ko",
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await db.SaveChangesAsync();
        }
        string prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":fe-client:" + Guid.NewGuid().ToString("N");
        await using var writer = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: ConfigureProfiles);
        await using var reader = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: ConfigureProfiles);
        var root = FindRoot();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(root, "scripts", "tests", "frontend-stomp-runtime.cjs"));

        // 실행 — 합성 JWT는 stdin으로 전달하고 stdout/stderr에는 절대로 기록하지 않는다.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Node test process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
        {
            writerAddress = writer.Client.BaseAddress!.ToString(),
            writerToken = writer.CreateToken(room.UserId),
            writerUserId = room.UserId,
            readerAddress = reader.Client.BaseAddress!.ToString(),
            readerToken = reader.CreateToken(readerId),
            roomId = room.RoomId
        }));
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // 이 테스트가 생성한 정확한 자식 프로세스만 종료한다.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        // 검증 — Node 실제 client/parser 결과와 독립 EF connection의 저장/읽음 상태를 함께 확인한다.
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"Node exit={process.ExitCode}; {output}; {error}");
        Assert.Contains("FE_STOMP_PASS", output);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var message = await read.ChatMessages.SingleAsync(row => row.ChatRoomId == room.RoomId && row.Content == "실제 FE STOMP 합성");
        var member = await read.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == room.RoomId && row.UserId == readerId);
        Assert.Equal(message.Id, member.LastReadMessageId);
        Assert.NotNull(member.LastReadAt);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CatPjt-TranslaCat-chat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CHAT workspace root is required.");
    }

    private static void ConfigureProfiles(IServiceCollection services)
    {
        services.AddSingleton<IChatMessageProfileReader, SyntheticProfiles>();
    }

    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChatUserMessageProfile(userId, "합성 사용자", $"synthetic-{userId}@example.invalid", null));
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult($"USER:{userId}");
        }
    }
}
