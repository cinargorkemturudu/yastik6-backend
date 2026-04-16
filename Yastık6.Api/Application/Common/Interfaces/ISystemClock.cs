namespace Yastık6.Api.Application.Common.Interfaces;

public interface ISystemClock
{
    DateTime UtcNow { get; }
}