namespace Yastık6.Api.Infrastructure.Providers.Base;

/// <summary>
/// Thrown when a provider still fails after all Polly retries. Wrapping the original
/// exception matters: an HttpClient timeout surfaces as TaskCanceledException, and if that
/// leaked out raw, PriceSnapshotRefresher would read it as shutdown and stop the provider loop.
/// </summary>
public sealed class ProviderFetchException : Exception
{
    public string ProviderName { get; }

    public ProviderFetchException(string providerName, Exception innerException)
        : base($"{providerName} fetch failed after retries: {innerException.Message}", innerException)
    {
        ProviderName = providerName;
    }
}
