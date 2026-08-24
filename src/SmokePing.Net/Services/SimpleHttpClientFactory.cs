namespace SmokePing.Net.Services;

/// <summary>
/// An <see cref="IHttpClientFactory"/> backed by a single client, for the paths that
/// run outside dependency injection - the start-up validation pass and tests.
/// </summary>
public sealed class SimpleHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;

    public SimpleHttpClientFactory(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
    }

    public HttpClient CreateClient(string name) => _client;

    public void Dispose() => _client.Dispose();
}
