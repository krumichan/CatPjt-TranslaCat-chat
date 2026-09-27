using System.Text.Json;

namespace TranslaCat.Chat.Application.Ai;

public sealed record ChatModelMessage(string Role, string Content);

public sealed record ChatModelExecutionCommand(
    string TraceId,
    string Instructions,
    IReadOnlyList<ChatModelMessage> Messages,
    string Tier,
    string ReasoningEffort,
    string Verbosity,
    int MaxOutputTokens,
    int RemainingMilliseconds,
    int MaxProviderCalls,
    object? ResponseSchema,
    string? SchemaName,
    bool Strict,
    string TaskName);

public sealed record ChatModelExecutionResult(
    JsonElement Output,
    int InputTokens,
    int OutputTokens,
    string Provider,
    string Model,
    int ProviderCalls);
