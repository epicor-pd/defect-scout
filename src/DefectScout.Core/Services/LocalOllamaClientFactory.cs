using OllamaSharp;

namespace DefectScout.Core.Services;

internal sealed class LocalOllamaClientHandle : IDisposable
{
    private readonly HttpClient _httpClient;

    public LocalOllamaClientHandle(string endpoint, string model, TimeSpan timeout)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint, UriKind.Absolute),
            Timeout = timeout,
        };

        Client = new OllamaApiClient(_httpClient, model, jsonSerializerContext: null);
    }

    public OllamaApiClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _httpClient.Dispose();
    }
}

internal static class LocalOllamaClientFactory
{
    public static LocalOllamaClientHandle Create(string endpoint, string model, TimeSpan timeout) =>
        new(endpoint, model, timeout);
}
