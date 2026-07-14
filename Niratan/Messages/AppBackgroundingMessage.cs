using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Niratan.Messages;

public enum AppLifecycleCheckpointReason
{
    Background,
    Closing,
}

public sealed class AppBackgroundingMessage(
    AppLifecycleCheckpointReason reason) : AsyncRequestMessage<bool>
{
    public AppLifecycleCheckpointReason Reason { get; } = reason;
}
