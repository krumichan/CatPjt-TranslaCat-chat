using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.OpenMembership;
using TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

namespace TranslaCat.Chat.Api.OpenMembership;

public static class OpenMembershipExtensions
{
    public static IServiceCollection AddOpenMembershipHttp(this IServiceCollection services)
    {
        services.AddScoped<IOpenMembershipDelivery, OpenMembershipRealtimeDelivery>();
        services.AddScoped(provider => new OpenMembershipService(
            provider.GetService<IOpenMembershipStore>() ?? throw new OpenRoomDependencyUnavailableException(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }

    public static IServiceCollection AddOpenMembershipPersistence(this IServiceCollection services)
    {
        services.AddScoped<IOpenMembershipStore>(provider => new EfOpenMembershipStore(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetRequiredService<EfOpenRoomStore>(),
            provider.GetRequiredService<IOpenMembershipDelivery>(), provider.GetRequiredService<ILogger<EfOpenMembershipStore>>(),
            provider.GetService<IChatRoomAccountReader>(), provider.GetService<IOpenProfileStorage>()));
        return services;
    }
}
