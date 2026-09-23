using System.Net;
using System.Net.Http;
using System.Text.Json;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>Raised when a lookup fails; message is safe to show to the user.</summary>
public sealed class ScryfallException(string message) : Exception(message);

/// <summary>
/// Minimal Scryfall client: fuzzy card lookup by name. Sends a User-Agent + Accept header
/// and throttles requests per Scryfall's guidelines (~100 ms between calls).
/// </summary>
public sealed class ScryfallClient
{
    private const string Base = "https://api.scryfall.com";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cardinator/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>Looks up a card by (fuzzy) name. Returns null if no card matches.</summary>
    public async Task<CardModel?> LookupAsync(string name, CancellationToken ct = default)
        => (await LookupFacesAsync(name, ct)).FirstOrDefault();

    /// <summary>
    /// Looks up a card and returns all faces (one for a normal card, two for a double-faced card).
    /// Empty if no card matches.
    /// </summary>
    public async Task<IReadOnlyList<CardModel>> LookupFacesAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<CardModel>();

        var url = $"{Base}/cards/named?fuzzy={Uri.EscapeDataString(name.Trim())}";
        var json = await GetAsync(url, ct);
        if (json is null) return Array.Empty<CardModel>();

        return ScryfallMapper.MapFaces(json);
    }

    /// <summary>
    /// Runs a Scryfall search (e.g. "t:dragon", "set:dom", "c:r cmc=1") and returns up to
    /// <paramref name="maxCards"/> mapped cards, following pagination. Empty if nothing matched.
    /// </summary>
    public async Task<IReadOnlyList<CardModel>> SearchAsync(
        string query, int maxCards = 60, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CardModel>();

        var results = new List<CardModel>();
        string? url = $"{Base}/cards/search?unique=cards&q={Uri.EscapeDataString(query.Trim())}";
        while (url != null && results.Count < maxCards)
        {
            var json = await GetAsync(url, ct);
            if (json is null) break;   // 404 -> no cards matched
            results.AddRange(ScryfallMapper.MapSearch(json));
            progress?.Report($"Fetched {results.Count} card(s)…");
            url = NextPage(json);
        }
        if (results.Count > maxCards) results.RemoveRange(maxCards, results.Count - maxCards);
        return results;
    }

    private static string? NextPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True
               && root.TryGetProperty("next_page", out var np) && np.ValueKind == JsonValueKind.String
            ? np.GetString()
            : null;
    }

    private static async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var sinceLast = DateTime.UtcNow - _lastCall;
            var minGap = TimeSpan.FromMilliseconds(100);
            if (sinceLast < minGap)
                await Task.Delay(minGap - sinceLast, ct);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.GetAsync(url, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ScryfallException("Couldn't reach Scryfall. Check your internet connection. " + ex.Message);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout elapsed (surfaces as TaskCanceledException, not HttpRequestException).
                throw new ScryfallException("Scryfall request timed out. Check your internet connection.");
            }
            finally
            {
                _lastCall = DateTime.UtcNow;
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!resp.IsSuccessStatusCode)
                throw new ScryfallException($"Scryfall returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            return await resp.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

}
