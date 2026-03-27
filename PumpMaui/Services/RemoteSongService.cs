using PumpMaui.Game;
using PumpMaui.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PumpMaui;

public class RemoteSongIndex
{
    [JsonPropertyName("songs")]
    public List<string> Songs { get; set; } = [];
}

public static class RemoteSongService
{
    private static readonly HttpClient _http = new HttpClient
    {
        DefaultRequestHeaders =
        {
            UserAgent = { ProductInfoHeaderValue.Parse("PumpMaui/1.0") }
        }
    };

    /// <summary>
    /// Fetches songs.json from baseUrl, then fetches + parses each .ssc file.
    /// songs.json example: { "songs": ["16 - PHOENIX/18039 - Solfeggietto/16A8 - Solfeggietto.ssc"] }
    /// </summary>
    public static async Task<List<SscSong>> LoadSongsAsync(
        string baseUrl,
        IProgress<LoadProgress>? progress = null,
        CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');

        // 1. Fetch the index
        progress?.Report(new LoadProgress { Message = "Fetching song index..." });
        var indexUrl = $"{baseUrl}/songs.json";
        var indexJson = await _http.GetStringAsync(indexUrl, ct);
        var index = JsonSerializer.Deserialize<RemoteSongIndex>(indexJson)
                    ?? throw new InvalidDataException("songs.json was empty or invalid.");

        // 2. Fetch + parse each .ssc
        var results = new List<SscSong>();
        var i = 0;

        foreach (var relativePath in index.Songs)
        {
            try
            {
                i++;
                var sscUrl = ResolveIfRelative(baseUrl, relativePath);
                progress?.Report(new LoadProgress
                {
                    Message = $"Loading {Path.GetFileNameWithoutExtension(relativePath)}...",
                    Current = i,
                    Total = index.Songs.Count
                });

                var sscContent = await _http.GetStringAsync(sscUrl, ct);

                // Parse using the existing parser — pass the full URL as the "source path"
                // so relative asset URLs can be resolved later
                var song = SscParser.Parse(sscContent, sscUrl);

                // Tag the song so the UI knows assets are remote
                song.BaseUrl = baseUrl;

                if (song.Charts.Count > 0)
                    results.Add(song);
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Status: {ex.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"❌ Message: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"❌ Remote: failed to load {relativePath}: {ex.Message}");
            }
        }

        return results;
    }
    private static string? ResolveIfRelative(string baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();

        // If already an absolute HTTP(S) URL, return it (normalized)
        if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
            return path;

        // Remove any leading slashes so we don't produce double slashes
        var trimmed = path.TrimStart('/', '\\');

        // Encode each segment separately (preserve slashes)
        var segments = trimmed
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.EscapeDataString(s).Replace("%27", "'"));

        var safePath = string.Join('/', segments);

        // Combine with baseUrl (no duplicate slashes)
        var safeUrl = $"{baseUrl.TrimEnd('/')}/{safePath}";

        return safeUrl;
    }

    public static string? ResolveAssetUrl(SscSong song, string? assetRelative)
    {
        if (string.IsNullOrWhiteSpace(assetRelative) ||
            string.IsNullOrWhiteSpace(song.SourcePath) ||
            string.IsNullOrWhiteSpace(song.BaseUrl))
            return null;

        // SourcePath is the full .ssc URL; strip the filename to get the directory URL
        var lastSlash = song.SourcePath.LastIndexOf('/');
        if (lastSlash < 0) return null;

        var dirUrl = song.SourcePath[..lastSlash];
        return $"{dirUrl}/{assetRelative.TrimStart('/')}";
    }
}