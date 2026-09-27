namespace TranslaCat.Chat.Application.Messaging;

public interface IChatAiMessageDispatcher
{
    bool IsConfigured
    {
        get;
    }
    Task RecordHumanAsync(ChatHumanMessageRecordedIntent intent, CancellationToken cancellationToken);
    Task TriggerAsync(ChatAiTriggerRequestedIntent intent, CancellationToken cancellationToken);
}
