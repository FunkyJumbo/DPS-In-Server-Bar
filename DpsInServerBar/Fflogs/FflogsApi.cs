using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DpsInServerBar.Fflogs;

/// <summary>Minimal FFLogs API v2 client: OAuth client-credentials token + GraphQL queries.</summary>
public sealed class FflogsApi : IDisposable
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim tokenLock = new(1, 1);

    private string? token;
    private DateTime tokenExpiresUtc;
    private string credentialsKey = string.Empty;

    public void Dispose()
    {
        http.Dispose();
        tokenLock.Dispose();
    }

    /// <summary>Runs a GraphQL query and returns its "data" node.</summary>
    public async Task<JsonNode> QueryAsync(string clientId, string clientSecret, string query, CancellationToken ct)
    {
        var bearer = await GetTokenAsync(clientId, clientSecret, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            token = null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FFLogs API error {(int)response.StatusCode}: {Truncate(body)}");

        var root = JsonNode.Parse(body);
        if (root?["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException($"FFLogs query error: {Truncate(errors[0]?["message"]?.ToString())}");

        return root?["data"] ?? throw new InvalidOperationException("FFLogs returned no data.");
    }

    private async Task<string> GetTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = clientId + "\n" + clientSecret;
            if (token is not null && key == credentialsKey && DateTime.UtcNow < tokenExpiresUtc)
                return token;

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
            request.Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Only cache a token from a successful response (a cached failure would break every later call).
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"FFLogs authentication failed ({(int)response.StatusCode}). Check the client ID and secret.");
            }

            var json = JsonNode.Parse(body);
            var accessToken = json?["access_token"]?.ToString();
            if (string.IsNullOrEmpty(accessToken))
                throw new InvalidOperationException("FFLogs authentication returned no token.");

            var expiresIn = json?["expires_in"]?.GetValue<double>() ?? 3600;
            token = accessToken;
            credentialsKey = key;
            tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn - 300);
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static string Truncate(string? s) => s is null ? string.Empty : s.Length > 200 ? s[..200] + "..." : s;
}
