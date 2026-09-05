namespace Niratan.Messages;

public sealed record GoogleDriveConnectionStateChangedMessage(
    bool IsConnected,
    bool RequiresReconnect = false);
