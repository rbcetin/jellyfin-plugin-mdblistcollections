using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.MdbListCollections;

/// <summary>One entry of an MDBList list, with whichever ids MDBList knows.</summary>
/// <param name="IsShow">True for a series, false for a movie.</param>
/// <param name="Imdb">IMDb id, if any.</param>
/// <param name="Tmdb">TMDb id, if any.</param>
/// <param name="Tvdb">TVDB id, if any.</param>
public record MdbListEntry(bool IsShow, string? Imdb, string? Tmdb, string? Tvdb);

/// <summary>A fetched MDBList list.</summary>
/// <param name="Name">The list's display name.</param>
/// <param name="Entries">Its items.</param>
public record MdbListList(string Name, IReadOnlyList<MdbListEntry> Entries);

/// <summary>
/// Reads lists from the MDBList API. Every failure throws -- a caller must never mistake a
/// failed fetch for an empty list, or it would empty the collection.
/// </summary>
public partial class MdbListClient
{
    private const string BaseUrl = "https://api.mdblist.com";
    private const int PageSize = 1000;

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="MdbListClient"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public MdbListClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Turns what the user typed into the API path segment: "user/slug" or a numeric id.
    /// Accepts an mdblist.com URL, "user/slug", or an id.
    /// </summary>
    /// <param name="input">User input.</param>
    /// <returns>The path segment, or null if the input is not recognisable.</returns>
    public static string? ParseListRef(string input)
    {
        var s = input.Trim();
        var url = ListUrlRegex().Match(s);
        if (url.Success)
        {
            return $"{url.Groups[1].Value}/{url.Groups[2].Value}";
        }

        if (IdRegex().IsMatch(s) || UserSlugRegex().IsMatch(s))
        {
            return s;
        }

        return null;
    }

    /// <summary>Fetches a list's name and all of its items.</summary>
    /// <param name="listRef">Path segment from <see cref="ParseListRef"/>.</param>
    /// <param name="apiKey">MDBList API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list.</returns>
    public async Task<MdbListList> GetListAsync(string listRef, string apiKey, CancellationToken cancellationToken)
    {
        var key = Uri.EscapeDataString(apiKey);

        string name;
        using (var info = await GetJsonAsync($"{BaseUrl}/lists/{listRef}?apikey={key}", cancellationToken).ConfigureAwait(false))
        {
            var root = info.Document.RootElement;
            var obj = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            name = obj.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : throw new InvalidDataException($"MDBList list '{listRef}' not found");
        }

        var entries = new List<MdbListEntry>();
        for (var offset = 0; ; offset += PageSize)
        {
            using var page = await GetJsonAsync(
                $"{BaseUrl}/lists/{listRef}/items?apikey={key}&limit={PageSize}&offset={offset}",
                cancellationToken).ConfigureAwait(false);

            var root = page.Document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"MDBList returned unexpected items JSON for '{listRef}'");
            }

            ReadEntries(root, "movies", false, entries);
            ReadEntries(root, "shows", true, entries);

            if (!page.HasMore)
            {
                break;
            }
        }

        return new MdbListList(name, entries);
    }

    private static void ReadEntries(JsonElement root, string property, bool isShow, List<MdbListEntry> into)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            into.Add(new MdbListEntry(isShow, IdString(ids, "imdb"), IdString(ids, "tmdb"), IdString(ids, "tvdb")));
        }
    }

    private static string? IdString(JsonElement ids, string name)
    {
        if (!ids.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetInt64().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private async Task<JsonPage> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("MDBList rate limit hit (429), try again later", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MDBList returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        var hasMore = response.Headers.TryGetValues("x-has-more", out var values)
            && string.Equals(values.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JsonPage(doc, hasMore);
    }

    [GeneratedRegex(@"mdblist\.com/lists/([^/?#\s]+)/([^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListUrlRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$")]
    private static partial Regex UserSlugRegex();

    private sealed record JsonPage(JsonDocument Document, bool HasMore) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}
