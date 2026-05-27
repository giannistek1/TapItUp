using TapItUp.Game;
using TapItUp.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TapItUp;

public class RemoteSongIndex
{
    [JsonPropertyName("songs")]
    public List<string> Songs { get; set; } = [];
}

public static class RemoteSongService
{
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders =
        {
            UserAgent = { ProductInfoHeaderValue.Parse("TapItUp/1.0") }
        }
    };

    // Expose for lazy SSC fetching from other callers
    public static HttpClient HttpClient => _http;

    /// <summary>
    /// Reads songs.json from the app's Raw resources and builds series/song shells
    /// entirely from path segments — no .ssc file is opened at startup.
    /// Charts are lazy-loaded when the user selects a song.
    /// </summary>
    public static async Task<Dictionary<string, List<SscSong>>> LoadEmbeddedIndexAsync(
        IProgress<LoadProgress>? progress = null)
    {
        var result = new Dictionary<string, List<SscSong>>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new LoadProgress { Message = "Reading song index..." });

        await using var indexStream = await FileSystem.OpenAppPackageFileAsync("songs.json");
        var index = await JsonSerializer.DeserializeAsync<RemoteSongIndex>(indexStream)
                    ?? throw new InvalidDataException("songs.json was empty or invalid.");

        var total = index.Songs.Count;
        var i = 0;

        foreach (var relativePath in index.Songs)
        {
            i++;
            progress?.Report(new LoadProgress
            {
                Message = $"Indexing {i}/{total}...",
                Current = i,
                Total = total
            });

            var seriesName = GetSeriesName(relativePath);
            var title = GetSongTitleFromPath(relativePath);

            // Build a shell SscSong from path alone — no file I/O
            var song = new SscSong
            {
                Title = title,
                Artist = string.Empty,   // filled in after lazy load
                SourcePath = relativePath,
                MusicPath = string.Empty,
                BackgroundPath = string.Empty,
                BpmChanges = [],
                TickCounts = [],
                SpeedChanges = [],
                Charts = []              // filled in after lazy load
            };

            if (!result.TryGetValue(seriesName, out var list))
            {
                list = [];
                result[seriesName] = list;
            }

            list.Add(song);
        }

        return result;
    }

    /// <summary>
    /// Fetches only songs.json from the CDN, then builds shells from the path list.
    /// No individual .ssc files are fetched until the user selects a song.
    /// </summary>
    public static async Task<List<SscSong>> LoadSongsAsync(
        string baseUrl,
        IProgress<LoadProgress>? progress = null,
        CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');

        progress?.Report(new LoadProgress { Message = "Fetching song index..." });

        var indexUrl = $"{baseUrl}/songs.json";
        var indexJson = await _http.GetStringAsync(indexUrl, ct);
        var index = JsonSerializer.Deserialize<RemoteSongIndex>(indexJson)
                    ?? throw new InvalidDataException("songs.json was empty or invalid.");

        var results = new List<SscSong>();
        var i = 0;

        foreach (var relativePath in index.Songs)
        {
            i++;
            var sscUrl = ResolveIfRelative(baseUrl, relativePath);
            if (sscUrl == null) continue;

            progress?.Report(new LoadProgress
            {
                Message = $"Indexing {i}/{index.Songs.Count}...",
                Current = i,
                Total = index.Songs.Count
            });

            // Shell only — title from path, no HTTP fetch until song is selected
            var song = new SscSong
            {
                Title = GetSongTitleFromPath(relativePath),
                Artist = string.Empty,
                SourcePath = sscUrl,        // full URL stored for lazy fetch
                MusicPath = string.Empty,
                BackgroundPath = string.Empty,
                BpmChanges = [],
                TickCounts = [],
                SpeedChanges = [],
                Charts = []
            };
            song.BaseUrl = baseUrl;
            results.Add(song);
        }

        return results;
    }

    /// <summary>
    /// Extracts the series display name from a path or URL.
    /// Handles URL-encoded CDN paths, absolute filesystem paths, and relative paths.
    /// e.g. "Songs/16 - PHOENIX/18042 - Song/song.ssc"          → "PHOENIX"
    /// e.g. "https://cdn.../16%20-%20PHOENIX/18042%20-.../song"  → "PHOENIX"
    /// e.g. "D:\Songs\16 - PHOENIX\song\song.ssc"               → "PHOENIX"
    /// </summary>
    public static string GetSeriesName(string path)
    {
        // Decode URL encoding first so %20 becomes space, etc.
        path = Uri.UnescapeDataString(path).Replace('\\', '/');

        // Strip scheme + host for absolute URLs, then decode again (double-encoded CDN paths)
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            path = Uri.UnescapeDataString(uri.AbsolutePath);

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Find the first segment matching "XX - SERIES NAME" pattern
        var seriesSegment = segments.FirstOrDefault(p => p.Contains(" - "));

        if (!string.IsNullOrEmpty(seriesSegment))
        {
            var split = seriesSegment.Split('-', 2);
            if (split.Length >= 2)
                return split[1].Trim().ToUpperInvariant();
        }

        // Fallback: use first non-trivial segment that isn't a drive letter or known root
        var fallback = segments.FirstOrDefault(s =>
            s.Length > 1 &&
            !s.EndsWith(':') &&
            !s.Equals("Songs", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrEmpty(fallback) ? "UNKNOWN" : fallback.ToUpperInvariant();
    }

    /// <summary>
    /// Extracts the song display title from a path segment.
    /// e.g. "Songs/12 - PRIME/1431 - Point Zero One/1431 - Point Zero One.ssc" → "Point Zero One"
    /// </summary>
    public static string GetSongTitleFromPath(string path)
    {
        path = path.Replace('\\', '/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The song folder is the second-to-last segment (before the filename)
        var songFolder = segments.Length >= 2 ? segments[^2] : segments[^1];

        // Strip leading number prefix like "1431 - "
        var dashIdx = songFolder.IndexOf(" - ", StringComparison.Ordinal);
        return dashIdx >= 0 ? songFolder[(dashIdx + 3)..].Trim() : songFolder.Trim();
    }

    public static string? ResolveIfRelative(string baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();

        if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
            return path;

        var trimmed = path.TrimStart('/', '\\');

        var segments = trimmed
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.EscapeDataString(s).Replace("%27", "'"));

        var safePath = string.Join('/', segments);
        return $"{baseUrl.TrimEnd('/')}/{safePath}";
    }

    public static string? ResolveAssetUrl(SscSong song, string? assetRelative)
    {
        if (string.IsNullOrWhiteSpace(assetRelative) ||
            string.IsNullOrWhiteSpace(song.SourcePath) ||
            string.IsNullOrWhiteSpace(song.BaseUrl))
            return null;

        var lastSlash = song.SourcePath.LastIndexOf('/');
        if (lastSlash < 0) return null;

        var dirUrl = song.SourcePath[..lastSlash];
        return $"{dirUrl}/{assetRelative.TrimStart('/')}";
    }
}