using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.Binance;

// IPriceProvider miras alınarak, Background Worker'ın bu servisi otomatik tanıması sağlanıyor.
public interface IBinanceClient : IPriceProvider
{
}