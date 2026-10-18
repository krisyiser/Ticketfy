using CommunityToolkit.Mvvm.Messaging;

namespace Ticketfy.Core.Messages;

public sealed record HardwareFeedbackMessage(string Text, bool IsError)
{
    public static void Report(string text, bool isError = false) =>
        WeakReferenceMessenger.Default.Send(new HardwareFeedbackMessage(text, isError));
}
