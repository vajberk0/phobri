using System.Net.Http.Json;
using System.Text.Json;

namespace Phobri.Desktop.Services;

/// <summary>
/// Service to detect the external (public) IP address for off-LAN connectivity.
/// Uses http://ip.ie.mk/get as the primary provider.
/// </summary>
public interface IExternalIpService
{
    /// <summary>
    /// Get the current external IP address.
    /// </summary>
    /// <returns>External IP string, or null if detection fails.</returns>
    Task<string?> GetExternalIpAsync(CancellationToken ct = default);
}

public sealed class ExternalIpService : IExternalIpService
{
    private readonly HttpClient _httpClient;
    private const string IpProviderUrl = "http://ip.ie.mk/get";

    public ExternalIpService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc/>
    public async Task<string?> GetExternalIpAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(IpProviderUrl, ct);
            response.EnsureSuccessStatusCode();

            // Read content once as text
            var contentText = await response.Content.ReadAsStringAsync(ct);

            // Try JSON first
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<IpResponse>(
                    contentText,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result?.Ip is { Length: > 0 })
                    return result.Ip;
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON, treat as plain text
            }

            // Fallback: plain text trimmed
            var trimmed = contentText.Trim();
            return trimmed.Length > 0 ? trimmed : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record IpResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("ip")]
        public string? Ip { get; init; }
    }
}
