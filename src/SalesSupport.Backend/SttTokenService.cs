namespace SalesSupport.Backend;

/// <summary>
/// Issues short-lived Azure Speech tokens so the desktop client can stream audio directly
/// to STT without ever holding the subscription key (D9). Tokens live ~10 minutes; cached
/// for 8 so long calls refresh via GET /api/stt-token.
/// </summary>
public sealed class SttTokenService(IHttpClientFactory httpClientFactory)
{
    private readonly string? _key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
    private readonly string? _region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
    private string? _cachedToken;
    private DateTime _cachedUntil = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConfigured => !string.IsNullOrEmpty(_key) && !string.IsNullOrEmpty(_region);

    public async Task<SttSession> IssueAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Azure Speech is not configured on the backend (AZURE_SPEECH_KEY / AZURE_SPEECH_REGION).");

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is null || DateTime.UtcNow >= _cachedUntil)
            {
                var http = httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"https://{_region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
                request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
                request.Content = new StringContent("");

                var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                _cachedToken = await response.Content.ReadAsStringAsync(ct);
                _cachedUntil = DateTime.UtcNow.AddMinutes(8);
            }
            return new SttSession(_cachedToken, _region!, (int)(_cachedUntil - DateTime.UtcNow).TotalSeconds);
        }
        finally
        {
            _lock.Release();
        }
    }
}
