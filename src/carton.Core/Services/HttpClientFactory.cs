using System.Net.Http.Headers;

namespace carton.Core.Services;

/// <summary>
/// Shared HttpClient instances to avoid socket exhaustion and reduce memory overhead.
/// </summary>
public static class HttpClientFactory
{
    /// <summary>
    /// Client for the local sing-box / Clash API (127.0.0.1:9090).
    /// </summary>
    public static HttpClient LocalApi { get; } = CreateLocalApiClient();

    /// <summary>
    /// Client for external requests (GitHub, remote config downloads, etc.).
    /// </summary>
    public static HttpClient External { get; } = CreateExternalClient();

    private static HttpClient CreateLocalApiClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:9090/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private static HttpClient CreateExternalClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Carton/1.0");
        return client;
    }
}
