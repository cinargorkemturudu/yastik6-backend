using Yastık6.Api.Application.Common.Interfaces;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

// IPriceProvider miras alınarak DI konteynerinde ana toptancı olarak tanınması sağlanır
public interface IFreeCurrencyClient : IPriceProvider
{
}