using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Time;

public sealed class SystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}