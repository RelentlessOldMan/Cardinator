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

    // Scryfall's published limits (scryfall.com/docs/api/rate-limits): 10 requests a second in general, but only 2
    // a second for card search, named lookups, random and collection — and a 429 locks the client out for 30 s.
    internal static TimeSpan FastGap = TimeSpan.FromMilliseconds(100);
    internal static TimeSpan SlowGap = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan TooManyRequestsWait = TimeSpan.FromSeconds(30);

    /// <summary>The spacing Scryfall asks for before a request to <paramref name="uri"/>.</summary>
    internal static TimeSpan GapFor(Uri? uri)
    {
        var path = uri?.AbsolutePath ?? "";
        foreach (var slow in new[] { "/cards/search", "/cards/named", "/cards/random", "/cards/collection" })
            if (path.StartsWith(slow, StringComparison.OrdinalIgnoreCase)) return SlowGap;
        return FastGap;
    }

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

        List<CardModel> faces;
        try { faces = ScryfallMapper.MapFaces(json); }
        catch (JsonException) { throw new ScryfallException("Scryfall returned an unexpected response."); }
        return await PreferRegularPrintingAsync(faces, ct);
    }

    /// <summary>A card found by name alone comes as Scryfall's default printing, which is sometimes a special one (Lightning
    /// Bolt's is a Marvel crossover). Then this looks for the same card's regular paper printings and returns Scryfall's
    /// pick of those; a card with none (one only ever printed as a crossover), or a failed search, keeps what it had.</summary>
    public async Task<List<CardModel>> PreferRegularPrintingAsync(IReadOnlyList<CardModel> faces, CancellationToken ct = default)
    {
        if (faces.Count == 0 || !faces[0].SpecialPrinting || string.IsNullOrWhiteSpace(faces[0].Name)) return faces.ToList();
        var name = faces[0].Name.Trim();
        var query = $"!\"{name.Replace("\"", "")}\" game:paper -is:ub -is:promo not:funny";
        try
        {
            var json = await GetAsync($"{Base}/cards/search?unique=cards&q={Uri.EscapeDataString(query)}", ct);
            if (json is null) return faces.ToList();   // 404: no regular printing
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var el in data.EnumerateArray())
                {
                    // An exact-name search also finds a card with a face of that name ("Emeritus of Conflict // Lightning
                    // Bolt"): only the same card counts.
                    var mapped = ScryfallMapper.MapElement(el);
                    if (mapped.Count > 0 && mapped[0].Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !mapped[0].SpecialPrinting)
                        return mapped;
                }
        }
        catch (Exception ex) when (ex is ScryfallException or JsonException) { /* keep the printing we have */ }
        return faces.ToList();
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

    /// <summary>Shared send with the Scryfall rate-limit throttle (<see cref="GapFor"/>) and 429/503 backoff+retry. The
    /// request is built fresh per attempt because an HttpRequestMessage can't be resent.</summary>
    private static async Task<string?> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                using var request = makeRequest();
                var sinceLast = DateTime.UtcNow - _lastCall;
                var minGap = GapFor(request.RequestUri);
                if (sinceLast < minGap)
                    await Task.Delay(minGap - sinceLast, ct);

                HttpResponseMessage resp;
                try
                {
                    resp = await Http.SendAsync(request, ct);
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
                        // A 429 means Scryfall has locked us out for 30 s: retrying sooner only extends it.
                        var wait = resp.Headers.RetryAfter?.Delta
                                   ?? (resp.StatusCode == HttpStatusCode.TooManyRequests
                                       ? TooManyRequestsWait : TimeSpan.FromMilliseconds(500 * attempt));
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
                    : !string.IsNullOrWhiteSpace(r.Set)   // "Lightning Bolt (M10)": that card in that set
                    ? new Dictionary<string, string> { ["name"] = r.Name ?? "", ["set"] = r.Set!.ToLowerInvariant() }
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
