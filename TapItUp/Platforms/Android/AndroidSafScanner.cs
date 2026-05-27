using Android.Content;
using Android.Database;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace TapItUp.Platforms.Android;

/// <summary>
/// Scans a SAF (Storage Access Framework) tree URI on Android using
/// DocumentsContract + ContentResolver (no AndroidX dependency required).
/// Expected folder structure: root / [Game Series] / [Song] / song.ssc
/// </summary>
public static class AndroidSafScanner
{
    private const string MimeTypeDir = "vnd.android.document/directory";

    /// <summary>
    /// Walks the 3-level directory hierarchy under <paramref name="treeUriString"/>
    /// and returns one <see cref="ScanResult"/> for every .ssc file found.
    /// </summary>
    public static List<ScanResult> Scan(Context context, string treeUriString)
    {
        var results = new List<ScanResult>();

        var treeUri = AndroidUri.Parse(treeUriString);
        if (treeUri == null) return results;

        // Persist permission so access survives app restarts (required on Android 14+)
        try
        {
            context.ContentResolver!.TakePersistableUriPermission(
                treeUri, ActivityFlags.GrantReadUriPermission);
        }
        catch
        {
            // Non-fatal — the picker may have already persisted it
        }

        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri);
        if (rootDocId == null) return results;

        foreach (var series in ListChildren(context, treeUri, rootDocId))
        {
            if (series.MimeType != MimeTypeDir) continue;

            var seriesChildren = ListChildren(context, treeUri, series.DocId);

            // Optional banner image at the series level
            var bannerEntry = seriesChildren.FirstOrDefault(f =>
                f.MimeType != MimeTypeDir &&
                (f.Name.Equals("banner.png", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.Equals("banner.jpg", StringComparison.OrdinalIgnoreCase)));

            var bannerUri = bannerEntry.DocId != null
                ? DocumentsContract.BuildDocumentUriUsingTree(treeUri, bannerEntry.DocId)?.ToString()
                : null;

            foreach (var song in seriesChildren)
            {
                if (song.MimeType != MimeTypeDir) continue;

                var sscEntry = ListChildren(context, treeUri, song.DocId)
                    .FirstOrDefault(f =>
                        f.MimeType != MimeTypeDir &&
                        f.Name.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase));

                if (sscEntry.DocId == null) continue;

                var sscUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, sscEntry.DocId)?.ToString();
                var songDirUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, song.DocId)?.ToString();

                if (sscUri == null || songDirUri == null) continue;

                results.Add(new ScanResult
                {
                    SeriesName = series.Name.ToUpperInvariant(),
                    SongName = song.Name,
                    SscUri = sscUri,
                    SongDocumentUri = songDirUri,
                    BannerUri = bannerUri,
                });
            }
        }

        return results;
    }

    /// <summary>Reads the full text content of a SAF document URI.</summary>
    public static async Task<string> ReadTextAsync(Context context, string uriString)
    {
        var uri = AndroidUri.Parse(uriString)
            ?? throw new InvalidOperationException($"Cannot parse URI: {uriString}");

        await using var stream = context.ContentResolver!.OpenInputStream(uri)
            ?? throw new InvalidOperationException($"Cannot open stream for URI: {uriString}");

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Opens a readable stream for a SAF document URI.</summary>
    public static Stream OpenRead(Context context, string uriString)
    {
        var uri = AndroidUri.Parse(uriString)
            ?? throw new InvalidOperationException($"Cannot parse URI: {uriString}");

        return context.ContentResolver!.OpenInputStream(uri)
               ?? throw new InvalidOperationException($"Cannot open stream for URI: {uriString}");
    }

    private static List<DocEntry> ListChildren(Context context, AndroidUri treeUri, string parentDocId)
    {
        var entries = new List<DocEntry>();
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri == null) return entries;

        ICursor? cursor = null;
        try
        {
            cursor = context.ContentResolver!.Query(
                childrenUri,
                [
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType,
                ],
                null, null, null);

            if (cursor == null) return entries;

            while (cursor.MoveToNext())
            {
                var docId = cursor.GetString(0);
                var name = cursor.GetString(1);
                var mime = cursor.GetString(2);
                if (docId != null && name != null && mime != null)
                    entries.Add(new DocEntry(docId, name, mime));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidSafScanner] ListChildren failed for {parentDocId}: {ex.Message}");
        }
        finally
        {
            cursor?.Close();
        }

        return entries;
    }

    private readonly record struct DocEntry(string DocId, string Name, string MimeType);
}

public sealed class ScanResult
{
    public required string SeriesName { get; init; }
    public required string SongName { get; init; }   // display name of the song folder
    public required string SscUri { get; init; }
    public required string SongDocumentUri { get; init; }
    public string? BannerUri { get; init; }
}