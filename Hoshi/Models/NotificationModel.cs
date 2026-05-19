using System;
using Hoshi.Enums;

namespace Hoshi.Models;

public class NotificationModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;

    public bool IsActive { get; set; } = true;
}
