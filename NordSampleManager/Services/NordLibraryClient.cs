using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NordSampleManager.Services;

/// <summary>
/// HTTP client for the nordkeyboards.com REST API.
/// All URLs are relative to https://www.nordkeyboards.com.
/// Keyboard codes (e.g. 54 for Nord Stage 3) are passed by callers via KeyboardRegistry.
/// </summary>
public sealed class NordLibraryClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ----------------------------------------------------------------
    // Piano Library
    // ----------------------------------------------------------------

    public async Task<IReadOnlyList<LibraryCatalogEntry>> GetPianoCatalogAsync(CancellationToken ct = default)
    {
        var results = new List<LibraryCatalogEntry>();
        int page = 1;
        while (true)
        {
            var items = await FetchPageItemsAsync($"/sounds/piano-library/?page={page}", ct).ConfigureAwait(false);
            if (items is null) break;
            foreach (var item in items.OfType<JsonNode>())
                results.Add(ParsePianoEntry(item));

            var pagination = await FetchPaginationAsync($"/sounds/piano-library/?page={page}", ct).ConfigureAwait(false);
            if (pagination is null || page >= pagination.Value.totalPages) break;
            page++;
        }
        return results;
    }

    public async Task<PianoDownloads?> GetPianoDownloadsAsync(int productCode, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>($"/wt/api/main/v1/piano/downloads/{productCode}/", JsonOpts, ct).ConfigureAwait(false);
            if (json is null) return null;

            var options = json["downloads"]?.AsArray()
                .Select(d => new PianoDownloadOption(
                    Size:        d!["size"]!.GetValue<string>(),
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
        var items = await FetchPageItemsAsync("/sounds/sample-library/", ct).ConfigureAwait(false);
        if (items is null) return [];
        return items.OfType<JsonNode>().Select(ParseSampleEntry).ToList();
    }

    public async Task<IReadOnlyList<SampleInstrument>> GetSampleInstrumentsAsync(
        string accordionItemsUrl, string categoryTitle, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(accordionItemsUrl, JsonOpts, ct).ConfigureAwait(false);
            return json?["accordion_items"]?.AsArray()
                .Select(i => new SampleInstrument(
                    Id:                  i!["id"]!.GetValue<int>(),
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
    // Compatibility check
    // ----------------------------------------------------------------

    public async Task<CompatibleProducts?> GetCompatibleProductsAsync(int productCode, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>($"/wt/api/main/v1/compatible_products/{productCode}/", JsonOpts, ct).ConfigureAwait(false);
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
    /// For sample instruments: substitute the keyboard code in the URL before calling
    /// (replace the placeholder code with the actual connected keyboard's API code).
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
        var ms = total > 0 ? new MemoryStream((int)total) : new MemoryStream();

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

    private async Task<JsonArray?> FetchPageItemsAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            return ExtractNextDataItems(html);
        }
        catch { return null; }
    }

    private async Task<(int currentPage, int totalPages)?> FetchPaginationAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            var root = ExtractNextDataRoot(html);
            var pg = root?["pagination"];
            if (pg is null) return null;
            return (pg["currentPage"]!.GetValue<int>(), pg["totalPages"]!.GetValue<int>());
        }
        catch { return null; }
    }

    private static JsonArray? ExtractNextDataItems(string html)
    {
        var root = ExtractNextDataRoot(html);
        var itemsNode = root?["items"];
        return itemsNode as JsonArray;
    }

    private static JsonObject? ExtractNextDataRoot(string html)
    {
        var m = Regex.Match(html, @"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) return null;
        var data = JsonNode.Parse(m.Groups[1].Value);
        return data?["props"]?["pageProps"]?["componentProps"] as JsonObject;
    }

    private static LibraryCatalogEntry ParsePianoEntry(JsonNode item)
    {
        var cp = item["compatibleProducts"]?.GetValue<string>() ?? "";
        return new LibraryCatalogEntry(
            Title:                item["title"]?.GetValue<string>() ?? "",
            Description:          item["text"]?.GetValue<string>() ?? "",
            PreviewMp3Url:        item["playerData"]?.GetValue<string?>(),
            PianoDownloadsUrl:    item["pianoDownloads"]?.GetValue<string?>(),
            AccordionItemsUrl:    null,
            CompatibleProductsUrl: cp,
            LibraryType:          "Piano",
            Tags:                 item["tags"]?.AsArray().Select(t => t?["tag"]?.GetValue<string>() ?? "").ToArray() ?? []);
    }

    private static LibraryCatalogEntry ParseSampleEntry(JsonNode item) =>
        new(
            Title:                item["title"]?.GetValue<string>() ?? "",
            Description:          item["text"]?.GetValue<string>() ?? "",
            PreviewMp3Url:        null,
            PianoDownloadsUrl:    null,
            AccordionItemsUrl:    item["accordionItems"]?.GetValue<string?>(),
            CompatibleProductsUrl: item["compatibleProducts"]?.GetValue<string>() ?? "",
            LibraryType:          "Sample",
            Tags:                 []);

    private static List<CompatibleKeyboard> ParseCompatibleKeyboards(JsonArray? arr) =>
        arr?.Select(k => new CompatibleKeyboard(
            k!["label"]!.GetValue<string>(),
            k["value"]!.GetValue<int>()))
        .ToList() ?? [];

    /// <summary>
    /// Replaces the keyboard code placeholder in a sample download URL template.
    /// The template from the API contains a default keyboard code (e.g. "8"); this method
    /// substitutes the connected keyboard's code (e.g. 54 for Nord Stage 3).
    /// URL pattern: /wt/api/main/v1/download/sample_instruments_by_sound_version/{kbCode}/{id}/
    /// </summary>
    public static string SubstituteKeyboardCode(string urlTemplate, int keyboardCode)
    {
        // Replace the numeric segment after "sound_version/"
        return Regex.Replace(urlTemplate,
            @"(?<=sample_instruments_by_sound_version/)\d+(?=/)",
            keyboardCode.ToString());
    }
}
