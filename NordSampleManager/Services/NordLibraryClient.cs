using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NordSampleManager.Services;

/// <summary>
/// HTTP client for the nordkeyboards.com REST API.
/// Piano and sample catalogs are embedded in Next.js pages. We fetch page 1 as HTML
/// (to discover the buildId), then use the lightweight /_next/data/{buildId}/*.json
/// API for all subsequent pages, running them in parallel.
/// </summary>
public sealed class NordLibraryClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ----------------------------------------------------------------
    // Piano Library
    // ----------------------------------------------------------------

    public async Task<IReadOnlyList<LibraryCatalogEntry>> GetPianoCatalogAsync(CancellationToken ct = default)
    {
        // Page 1 via HTML — we need this to discover the buildId.
        var html1 = await http.GetStringAsync("/sounds/piano-library/", ct).ConfigureAwait(false);
        var (root1, buildId) = ParseNextData(html1);
        if (root1 is null) return [];

        var results = ExtractItems(root1, ParsePianoEntry);
        var totalPages = root1["pagination"]?["totalPages"]?.GetValue<int>() ?? 1;

        if (totalPages > 1)
        {
            // Pages 2..N in parallel via the lightweight JSON endpoint.
            var pageTasks = Enumerable.Range(2, totalPages - 1)
                .Select(p => FetchNextDataAsync($"/sounds/piano-library.json", $"page={p}", buildId, ct));
            var roots = await Task.WhenAll(pageTasks).ConfigureAwait(false);
            foreach (var root in roots.OfType<JsonObject>())
                results.AddRange(ExtractItems(root, ParsePianoEntry));
        }

        return results;
    }

    public async Task<PianoDownloads?> GetPianoDownloadsAsync(int productCode, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(
                $"/wt/api/main/v1/piano/downloads/{productCode}/", JsonOpts, ct).ConfigureAwait(false);
            if (json is null) return null;

            var options = json["downloads"]?.AsArray()
                .OfType<JsonNode>()
                .Select(d => new PianoDownloadOption(
                    Size:        d["size"]!.GetValue<string>(),
                    FileSize:    d["file_size"]!.GetValue<string>(),
                    DownloadUrl: d["download_url"]!.GetValue<string>(),
                    VersionName: d["version_name"]!.GetValue<string>()))
                .ToList() ?? [];

            var compat = ParseCompatibleKeyboards(json["compatible_products"]?.AsArray());
            return new PianoDownloads(options, compat);
        }
        catch { return null; }
    }

    // ----------------------------------------------------------------
    // Sample Library
    // ----------------------------------------------------------------

    public async Task<IReadOnlyList<LibraryCatalogEntry>> GetSampleCatalogAsync(CancellationToken ct = default)
    {
        var html = await http.GetStringAsync("/sounds/sample-library/", ct).ConfigureAwait(false);
        var (root, _) = ParseNextData(html);
        if (root is null) return [];
        return ExtractItems(root, ParseSampleEntry);
    }

    public async Task<IReadOnlyList<SampleInstrument>> GetSampleInstrumentsAsync(
        string accordionItemsUrl, string categoryTitle, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(accordionItemsUrl, JsonOpts, ct).ConfigureAwait(false);
            return json?["accordion_items"]?.AsArray()
                .OfType<JsonNode>()
                .Select(i => new SampleInstrument(
                    Id:                  i["id"]!.GetValue<int>(),
                    Title:               i["title"]!.GetValue<string>(),
                    FileSize:            i["file_size"]!.GetValue<string>(),
                    Version:             i["latest_version"]!.GetValue<string>(),
                    DownloadUrlTemplate: i["download_url"]!.GetValue<string>(),
                    PreviewMp3Url:       i["preview_file"]?.GetValue<string?>(),
                    Category:            categoryTitle))
                .ToList() ?? [];
        }
        catch { return []; }
    }

    // ----------------------------------------------------------------
    // Compatibility
    // ----------------------------------------------------------------

    public async Task<CompatibleProducts?> GetCompatibleProductsAsync(int productCode, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(
                $"/wt/api/main/v1/compatible_products/{productCode}/", JsonOpts, ct).ConfigureAwait(false);
            if (json is null) return null;
            return new CompatibleProducts(
                Active: ParseCompatibleKeyboards(json["active_products"]?.AsArray()),
                Legacy: ParseCompatibleKeyboards(json["legacy_products"]?.AsArray()));
        }
        catch { return null; }
    }

    // ----------------------------------------------------------------
    // File download
    // ----------------------------------------------------------------

    /// <summary>
    /// Downloads a file from a relative path on nordkeyboards.com.
    /// For sample instruments: substitute the keyboard code in the URL first via
    /// <see cref="SubstituteKeyboardCode"/> before calling.
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(
        string relativeUrl,
        IProgress<(long received, long total)>? progress = null,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var ms = total > 0 ? new MemoryStream((int)Math.Min(total, int.MaxValue)) : new MemoryStream();

        var buffer = new byte[81_920];
        long received = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            progress?.Report((received, total));
        }

        return ms.ToArray();
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Parses __NEXT_DATA__ from an HTML page.
    /// Returns (componentProps root, buildId). buildId is null when not found.
    /// </summary>
    private static (JsonObject? root, string? buildId) ParseNextData(string html)
    {
        var m = Regex.Match(html,
            @"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>",
            RegexOptions.Singleline);
        if (!m.Success) return (null, null);

        var data = JsonNode.Parse(m.Groups[1].Value);
        var root = data?["props"]?["pageProps"]?["componentProps"] as JsonObject;
        var buildId = data?["buildId"]?.GetValue<string>();
        return (root, buildId);
    }

    /// <summary>
    /// Fetches a Next.js server data JSON endpoint. Falls back to null on any error.
    /// path: e.g. "/sounds/piano-library.json"
    /// query: e.g. "page=2"
    /// </summary>
    private async Task<JsonObject?> FetchNextDataAsync(
        string path, string? query, string? buildId, CancellationToken ct)
    {
        if (buildId is null) return null;
        var url = $"/_next/data/{buildId}/sounds{path}" + (query is null ? "" : $"?{query}");
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(url, JsonOpts, ct).ConfigureAwait(false);
            return json?["pageProps"]?["componentProps"] as JsonObject;
        }
        catch { return null; }
    }

    private static List<LibraryCatalogEntry> ExtractItems(JsonObject root, Func<JsonNode, LibraryCatalogEntry> parser)
    {
        var arr = root["items"] as JsonArray;
        if (arr is null) return [];
        return arr.OfType<JsonNode>().Select(parser).ToList();
    }

    private static LibraryCatalogEntry ParsePianoEntry(JsonNode item)
    {
        var cp = item["compatibleProducts"]?.GetValue<string>() ?? "";
        return new LibraryCatalogEntry(
            Title:                 item["title"]?.GetValue<string>() ?? "",
            Description:           item["text"]?.GetValue<string>() ?? "",
            PreviewMp3Url:         item["playerData"]?.GetValue<string?>(),
            PianoDownloadsUrl:     item["pianoDownloads"]?.GetValue<string?>(),
            AccordionItemsUrl:     null,
            CompatibleProductsUrl: cp,
            LibraryType:           "Piano",
            Tags:                  item["tags"]?.AsArray()
                                       .OfType<JsonNode>()
                                       .Select(t => t["tag"]?.GetValue<string>() ?? "")
                                       .ToArray() ?? []);
    }

    private static LibraryCatalogEntry ParseSampleEntry(JsonNode item) =>
        new(Title:                 item["title"]?.GetValue<string>() ?? "",
            Description:           item["text"]?.GetValue<string>() ?? "",
            PreviewMp3Url:         null,
            PianoDownloadsUrl:     null,
            AccordionItemsUrl:     item["accordionItems"]?.GetValue<string?>(),
            CompatibleProductsUrl: item["compatibleProducts"]?.GetValue<string>() ?? "",
            LibraryType:           "Sample",
            Tags:                  []);

    private static List<CompatibleKeyboard> ParseCompatibleKeyboards(JsonArray? arr) =>
        arr?.OfType<JsonNode>()
            .Select(k => new CompatibleKeyboard(k["label"]!.GetValue<string>(), k["value"]!.GetValue<int>()))
            .ToList() ?? [];

    /// <summary>
    /// Replaces the keyboard code in a sample download URL template.
    /// The API returns URLs with a default code (e.g. "8"); substitute the connected keyboard's code.
    /// Pattern: /download/sample_instruments_by_sound_version/{kbCode}/{id}/
    /// </summary>
    public static string SubstituteKeyboardCode(string urlTemplate, int keyboardCode) =>
        Regex.Replace(urlTemplate,
            @"(?<=sample_instruments_by_sound_version/)\d+(?=/)",
            keyboardCode.ToString());

    /// <summary>Parses a file-size string like "208.54 MB" into bytes. Returns 0 on failure.</summary>
    public static long ParseFileSizeBytes(string fileSize)
    {
        var m = Regex.Match(fileSize.Trim(), @"^([\d.]+)\s*(KB|MB|GB)$", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return 0;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            _    => 0,
        };
    }

    /// <summary>Extracts the product code from a compatible_products URL.</summary>
    public static int ExtractProductCode(string compatibleProductsUrl)
    {
        var parts = compatibleProductsUrl.TrimEnd('/').Split('/');
        return int.TryParse(parts.LastOrDefault(), out var code) ? code : 0;
    }
}
