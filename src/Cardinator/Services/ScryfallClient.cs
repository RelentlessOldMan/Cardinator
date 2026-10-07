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

    /// <summary>Descriptive User-Agent per Scryfall's API guidelines; reused by the other HTTP clients.</summary>
    public const string UserAgent = "Cardinator/1.0 (+https://github.com/RelentlessOldMan/Cardinator)";

    private static readonly HttpClient RealHttp = CreateClient();
    private static HttpClient Http => TestHttp ?? RealHttp;

    /// <summary>Tests only: answers every request instead of the network.</summary>
    internal static HttpClient? TestHttp;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
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

        try { return ScryfallMapper.MapFaces(json); }
        catch (JsonException) { throw new ScryfallException("Scryfall returned an unexpected response."); }
    }

    /// <summary>Fetches one card by its Scryfall API address (e.g. a meld part's melded card from
    /// <c>all_parts</c>). Empty if it isn't found. Only api.scryfall.com addresses are followed.</summary>
    public async Task<IReadOnlyList<CardModel>> LookupUriAsync(string uri, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
            || !u.Host.Equals("api.scryfall.com", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<CardModel>();
        var json = await GetAsync(u.AbsoluteUri, ct);
        if (json is null) return Array.Empty<CardModel>();
        try { return ScryfallMapper.MapFaces(json); }
        catch (JsonException) { throw new ScryfallException("Scryfall returned an unexpected response."); }
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
            try
            {
                results.AddRange(ScryfallMapper.MapSearch(json));
                url = NextPage(json);
            }
            catch (JsonException) { throw new ScryfallException("Scryfall returned an unexpected response."); }
            progress?.Report($"Fetched {results.Count} card(s)…");
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

    private static Task<string?> GetAsync(string url, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    private static Task<string?> PostJsonAsync(string url, string jsonBody, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        }, ct);

    /// <summary>Shared send with the Scryfall rate-limit throttle (~10/s) and 429/503 backoff+retry. The
    /// request is built fresh per attempt because an HttpRequestMessage can't be resent.</summary>
    private static async Task<string?> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                var sinceLast = DateTime.UtcNow - _lastCall;
                var minGap = TimeSpan.FromMilliseconds(100);
                if (sinceLast < minGap)
                    await Task.Delay(minGap - sinceLast, ct);

                HttpResponseMessage resp;
                try
                {
                    resp = await Http.SendAsync(makeRequest(), ct);
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

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    // Scryfall rate-limits with 429 and can return transient 503 — back off and retry.
                    if ((resp.StatusCode == HttpStatusCode.TooManyRequests
                         || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                        && attempt < maxAttempts)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta
                                   ?? TimeSpan.FromMilliseconds(500 * attempt);
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                        throw new ScryfallException($"Scryfall returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

                    return await resp.Content.ReadAsStringAsync(ct);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>One card to look up in a batch: by exact name, or by a specific printing (set + collector).</summary>
    public sealed record CardRef(string? Name, string? Set, string? Collector);

    /// <summary>
    /// Looks up many cards in as few requests as possible via Scryfall's /cards/collection endpoint
    /// (up to 75 per request), instead of one call per card. Returns the mapped faces for each card that
    /// matched (double-faced cards yield multiple faces). Cards Scryfall can't find are simply omitted —
    /// the caller can fall back to a fuzzy single lookup for those. A request that fails (network blip,
    /// bad response) costs only ITS chunk: the chunks already fetched are kept, the rest still run, and the
    /// failure is added to <paramref name="failures"/>.
    /// </summary>
    public async Task<List<List<CardModel>>> LookupCollectionAsync(
        IReadOnlyList<CardRef> refs, CancellationToken ct = default, List<string>? failures = null)
    {
        var results = new List<List<CardModel>>();
        for (int start = 0; start < refs.Count; start += 75)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = refs.Skip(start).Take(75).ToList();

            var identifiers = chunk.Select(r =>
                !string.IsNullOrWhiteSpace(r.Set) && !string.IsNullOrWhiteSpace(r.Collector)
                    ? new Dictionary<string, string> { ["set"] = r.Set!.ToLowerInvariant(), ["collector_number"] = r.Collector! }
                    : new Dictionary<string, string> { ["name"] = r.Name ?? "" }).ToList();
            var body = JsonSerializer.Serialize(new { identifiers });

            var mapped = new List<List<CardModel>>();
            try
            {
                var json = await PostJsonAsync($"{Base}/cards/collection", body, ct);
                if (json is null) continue;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var el in data.EnumerateArray())
                        mapped.Add(ScryfallMapper.MapElement(el));
            }
            catch (Exception ex) when (ex is ScryfallException or JsonException)
            {
                failures?.Add($"cards {start + 1}–{start + chunk.Count}: "
                    + (ex is JsonException ? "Scryfall returned an unexpected response." : ex.Message));
                continue;
            }
            results.AddRange(mapped);
        }
        return results;
    }
}
