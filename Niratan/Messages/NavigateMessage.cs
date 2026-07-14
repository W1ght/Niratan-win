using System;

namespace Niratan.Messages;

public record NavigateMessage(Type PageType, object? Parameter);
