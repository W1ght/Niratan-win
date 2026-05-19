using System;

namespace Hoshi.Messages;

public record NavigateMessage(Type PageType, object? Parameter);
