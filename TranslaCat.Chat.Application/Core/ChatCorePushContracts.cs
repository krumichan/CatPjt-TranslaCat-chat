using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Application.Core;

public sealed record ChatCorePushTarget(
    [property: JsonRequired] long UserId,
    [property: JsonRequired] string InstallationId,
    [property: JsonRequired] string AccountBindingId,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] long TokenVersion,
    [property: JsonRequired] string ExpoPushToken,
    [property: JsonRequired] string Platform);

public sealed record ChatCorePushTargets([property: JsonRequired] IReadOnlyList<ChatCorePushTarget> Targets);
