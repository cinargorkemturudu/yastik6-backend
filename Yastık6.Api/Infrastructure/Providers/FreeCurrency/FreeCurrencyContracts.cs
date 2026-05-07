using System.Text.Json.Serialization;

namespace Yastık6.Api.Infrastructure.Providers.FreeCurrency;

// FreeCurrencyAPI'nin standart JSON yanıtını temsil eder
public sealed record FreeCurrencyResponseRaw
{
    [JsonPropertyName("data")]
    public Dictionary<string, decimal> Data { get; init; } = new();
}