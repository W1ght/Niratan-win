using System;

namespace Niratan.Models;

public sealed record VideoABLoop(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}
