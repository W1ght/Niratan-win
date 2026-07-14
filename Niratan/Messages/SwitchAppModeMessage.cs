using Niratan.Enums;

namespace Niratan.Messages;

public record SwitchAppModeMessage(AppMode appMode, object? Parameter = null);
