using Hoshi.Enums;

namespace Hoshi.Messages;

public record SwitchAppModeMessage(AppMode appMode, object? Parameter = null);
