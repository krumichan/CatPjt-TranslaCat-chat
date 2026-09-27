using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatRoomEntity> ChatRooms => Set<ChatRoomEntity>();
    public DbSet<ChatRoomMemberEntity> ChatRoomMembers => Set<ChatRoomMemberEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    public DbSet<ChatMessageTranslationEntity> ChatMessageTranslations => Set<ChatMessageTranslationEntity>();
    public DbSet<UserChatLanguageSettingEntity> UserChatLanguageSettings => Set<UserChatLanguageSettingEntity>();
    public DbSet<OpenChatRoomEntity> OpenChatRooms => Set<OpenChatRoomEntity>();
    public DbSet<OpenChatMemberProfileEntity> OpenChatMemberProfiles => Set<OpenChatMemberProfileEntity>();
    public DbSet<OpenChatBanEntity> OpenChatBans => Set<OpenChatBanEntity>();
    public DbSet<ChatAiAgentEntity> ChatAiAgents => Set<ChatAiAgentEntity>();
    public DbSet<ChatRoomAiMemberEntity> ChatRoomAiMembers => Set<ChatRoomAiMemberEntity>();
    public DbSet<ChatRoomAiSettingEntity> ChatRoomAiSettings => Set<ChatRoomAiSettingEntity>();
    public DbSet<ChatRoomAiActivityEntity> ChatRoomAiActivities => Set<ChatRoomAiActivityEntity>();
    public DbSet<ChatAiSystemSettingEntity> ChatAiSystemSettings => Set<ChatAiSystemSettingEntity>();
    public DbSet<ChatNotificationEntity> ChatNotifications => Set<ChatNotificationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 기존 로컬 MySQL schema에서 확인한 문자열 비교 규칙을 새 CHAT DB에 명시한다.
        modelBuilder.UseCollation("utf8mb4_0900_ai_ci");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatDbContext).Assembly);
    }
}
