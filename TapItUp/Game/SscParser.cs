using TapItUp.Extensions;
using System.Globalization;

namespace TapItUp.Game;

public static class SscParser
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Fully parses an .ssc file including all chart note data.</summary>
    public static SscSong Parse(string content, string? sourcePath = null)
    {
        var parser = new PegParser(content);
        var globalTags = parser.ParseGlobalTags();
        var (bpmChanges, tickCounts, speedChanges) = ParseTimingData(globalTags);
        var offsetSeconds = ParseDouble(globalTags.Get("OFFSET")) ?? 0d;

        var charts = parser
            .EnumerateNoteDataSections()
            .Select(section => ParseSingleChart(section, bpmChanges, offsetSeconds))
            .OfType<SscChart>()
            .ToList();

        return BuildSong(globalTags, bpmChanges, tickCounts, speedChanges, charts, sourcePath);
    }

    /// <summary>
    /// Parses only global header tags — no chart or note data.
    /// Suitable for building the song list at startup; charts are lazy-loaded on selection.
    /// </summary>
    public static SscSong ParseHeaderOnly(string content, string? sourcePath = null)
    {
        var parser = new PegParser(content);
        var globalTags = parser.ParseGlobalTags();
        var (bpmChanges, tickCounts, speedChanges) = ParseTimingData(globalTags);

        return BuildSong(globalTags, bpmChanges, tickCounts, speedChanges, charts: [], sourcePath);
    }

    // -------------------------------------------------------------------------
    // Song construction helpers
    // -------------------------------------------------------------------------

    private static (List<BpmChange> Bpm, List<TickCount> Ticks, List<SpeedChange> Speeds)
        ParseTimingData(Dictionary<string, string> tags) => (
            ParseBpmString(tags.Get("BPMS")),
            ParseTickCountString(tags.Get("TICKCOUNTS")),
            ParseSpeedString(tags.Get("SPEEDS")));

    private static SscSong BuildSong(
        Dictionary<string, string> tags,
        List<BpmChange> bpmChanges,
        List<TickCount> tickCounts,
        List<SpeedChange> speedChanges,
        List<SscChart> charts,
        string? sourcePath) => new()
        {
            Title = tags.Get("TITLE", "Unknown Title"),
            Artist = tags.Get("ARTIST", "Unknown Artist"),
            OffsetSeconds = tags.GetDouble("OFFSET", 0),
            BpmChanges = bpmChanges,
            TickCounts = tickCounts,
            SpeedChanges = speedChanges,
            Charts = charts,
            SourcePath = sourcePath,
            MusicPath = tags.Get("MUSIC", tags.Get("SONG", "")),
            BackgroundPath = tags.Get("BANNER", tags.Get("BACKGROUND", ""))
        };

    // -------------------------------------------------------------------------
    // Chart parsing
    // -------------------------------------------------------------------------

    private static SscChart? ParseSingleChart(
        string section,
        IReadOnlyList<BpmChange> globalBpmChanges,
        double globalOffsetSeconds)
    {
        var tags = ParseChartTags(section);

        var stepType = tags.GetValueOrDefault("STEPSTYPE") ?? string.Empty;
        if (!IsKnownStepType(stepType))
        {
            System.Diagnostics.Debug.WriteLine($"   ⚠️ Skipping unknown STEPSTYPE: '{stepType}'");
            return null;
        }

        var description = tags.GetValueOrDefault("DESCRIPTION") ?? string.Empty;
        if (description.Contains("UCS", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"   ⏭️ Skipping UCS chart: '{description}'");
            return null;
        }

        var difficulty = tags.GetValueOrDefault("DIFFICULTY") ?? "Beginner";
        var meter = int.TryParse(tags.GetValueOrDefault("METER"), out var m) ? m : 1;

        var bpmChanges = tags.TryGetValue("BPMS", out var chartBpms) && !string.IsNullOrEmpty(chartBpms)
            ? ParseBpmString(chartBpms)
            : globalBpmChanges;

        var offsetSeconds = ParseDouble(tags.GetValueOrDefault("OFFSET")) ?? globalOffsetSeconds;

        var notesText = ExtractNotesBlock(section);
        if (notesText is null) return null;

        var notes = ParseNotes(notesText, bpmChanges, offsetSeconds, stepType);
        if (notes.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine($"   ⚠️ No notes parsed for {stepType} {difficulty} {meter}");
            return null;
        }

        return new SscChart
        {
            StepType = stepType,
            Description = description,
            Difficulty = difficulty,
            Meter = meter,
            Notes = notes,
            SpeedChanges = ParseSpeedString(tags.GetValueOrDefault("SPEEDS"))
        };
    }

    /// <summary>Scans a #NOTEDATA section for #KEY:VALUE; pairs.</summary>
    private static Dictionary<string, string> ParseChartTags(string section)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;

        while (i < section.Length)
        {
            var hash = section.IndexOf('#', i);
            if (hash < 0) break;

            var colon = section.IndexOf(':', hash + 1);
            if (colon < 0) break;

            var semi = section.IndexOf(';', colon + 1);
            if (semi < 0) break;

            var key = section[(hash + 1)..colon].Trim();
            if (!string.IsNullOrEmpty(key))
                tags[key] = section[(colon + 1)..semi].Trim();

            i = semi + 1;
        }

        return tags;
    }

    /// <summary>
    /// Finds the #NOTES: block within a notedata section and returns the raw
    /// measure text, or <see langword="null"/> if the block is absent.
    /// </summary>
    private static string? ExtractNotesBlock(string section)
    {
        var notesIdx = IndexOfIgnoreCase(section, "#NOTES:");
        if (notesIdx < 0) return null;

        var notesSub = section[(notesIdx + "#NOTES:".Length)..].TrimStart();
        var terminator = notesSub.IndexOf(';');
        var notesBlock = terminator >= 0 ? notesSub[..terminator] : notesSub;

        // Skip any header lines before the first measure row or comma separator
        var lines = notesBlock.Split('\n').Select(l => l.Trim('\r')).ToList();
        var measureStart = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var isNoteRow = line.Length >= 5 && line.All(c => c is >= '0' and <= '4' or ' ');
            if (line.Contains(',') || isNoteRow)
            {
                measureStart = i;
                break;
            }
        }

        return string.Join('\n', lines.Skip(measureStart));
    }

    // -------------------------------------------------------------------------
    // Note parsing
    // -------------------------------------------------------------------------

    private static List<ChartNote> ParseNotes(
        string notesText,
        IReadOnlyList<BpmChange> bpmChanges,
        double offsetSeconds,
        string stepType = "pump-single")
    {
        var notes = new List<ChartNote>();
        if (string.IsNullOrWhiteSpace(notesText) || bpmChanges.Count == 0) return notes;

        var isDouble =
            stepType.Equals("pump-double", StringComparison.OrdinalIgnoreCase) ||
            stepType.Equals("dance-double", StringComparison.OrdinalIgnoreCase);
        var expectedRowLength = isDouble ? 10 : 5;

        // Pre-sort once so BeatToSeconds does not sort on every note
        var sortedBpm = bpmChanges.OrderBy(c => c.Beat).ToList();

        var measures = notesText.Trim().TrimEnd(';')
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(measure => measure
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SanitizeRow)
                .Where(row => row.Length >= expectedRowLength && row.All(c => c is >= '0' and <= '4'))
                .ToList())
            .Where(measure => measure.Count > 0)
            .ToList();

        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            var rows = measures[measureIndex];
            var rowBeatSpan = 4d / rows.Count;

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var beat = measureIndex * 4d + rowIndex * rowBeatSpan;
                var laneCount = Math.Min(expectedRowLength, row.Length);

                for (var lane = 0; lane < laneCount; lane++)
                {
                    var noteType = CharToNoteType(row[lane]);
                    if (noteType is NoteType.None) continue;

                    notes.Add(new ChartNote
                    {
                        Lane = lane,
                        Beat = beat,
                        TimeSeconds = BeatToSeconds(beat, sortedBpm) - offsetSeconds,
                        Type = noteType
                    });
                }
            }
        }

        return notes;
    }

    // -------------------------------------------------------------------------
    // Timing tag parsers
    // -------------------------------------------------------------------------

    private static List<BpmChange> ParseBpmString(string? bpmText)
    {
        var changes = ParseKeyValuePairs(bpmText, segments =>
            double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat) &&
            double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
                ? new BpmChange(beat, bpm)
                : (BpmChange?)null);

        if (changes.Count == 0)
            changes.Add(new BpmChange(0d, 120d));

        return changes;
    }

    private static List<TickCount> ParseTickCountString(string? tickText)
    {
        var result = ParseKeyValuePairs(tickText, segments =>
            double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat) &&
            int.TryParse(segments[1], out var ticks) && ticks >= 0
                ? new TickCount(beat, ticks)
                : (TickCount?)null);

        if (result.Count == 0)
            result.Add(new TickCount(0d, 4));

        return result;
    }

    /// <summary>
    /// Parses a #SPEEDS string such as "0=1=0=0,64=0.5=0=0".
    /// Only beat and multiplier are used; duration and mode are ignored.
    /// </summary>
    private static List<SpeedChange> ParseSpeedString(string? speedText) =>
        ParseKeyValuePairs(speedText, segments =>
            segments.Length >= 2 &&
            double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat) &&
            double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier) &&
            multiplier > 0d
                ? new SpeedChange(beat, multiplier)
                : (SpeedChange?)null);

    /// <summary>
    /// Shared comma-separated "beat=value" parser. The <paramref name="factory"/>
    /// receives the split segments and returns a value or <see langword="null"/> to skip.
    /// </summary>
    private static List<T> ParseKeyValuePairs<T>(string? text, Func<string[], T?> factory)
        where T : struct
    {
        var result = new List<T>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = part.Split('=');
            if (factory(segments) is { } value)
                result.Add(value);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    private static bool IsKnownStepType(string stepType) =>
        stepType.Equals("pump-single", StringComparison.OrdinalIgnoreCase) ||
        stepType.Equals("pump-double", StringComparison.OrdinalIgnoreCase) ||
        stepType.Equals("dance-single", StringComparison.OrdinalIgnoreCase) ||
        stepType.Equals("dance-double", StringComparison.OrdinalIgnoreCase);

    private static NoteType CharToNoteType(char c) => c switch
    {
        '1' => NoteType.Tap,
        '2' => NoteType.HoldStart,
        '3' => NoteType.HoldEnd,
        '4' => NoteType.HoldBody,
        _ => NoteType.None
    };

    private static string SanitizeRow(string rawRow)
    {
        var commentIdx = rawRow.IndexOf("//", StringComparison.Ordinal);
        return (commentIdx >= 0 ? rawRow[..commentIdx] : rawRow).Trim();
    }

    /// <summary>Converts a beat position to seconds using pre-sorted BPM changes.</summary>
    private static double BeatToSeconds(double beat, List<BpmChange> sortedBpm)
    {
        if (sortedBpm.Count == 0) return beat / 120d * 60d;

        var seconds = 0d;
        var lastBeat = 0d;
        var currentBpm = sortedBpm[0].Bpm;

        foreach (var change in sortedBpm)
        {
            if (change.Beat > beat) break;
            seconds += (change.Beat - lastBeat) / currentBpm * 60d;
            lastBeat = change.Beat;
            currentBpm = change.Bpm;
        }

        return seconds + (beat - lastBeat) / currentBpm * 60d;
    }

    private static double? ParseDouble(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static int IndexOfIgnoreCase(string haystack, string needle) =>
        string.IsNullOrEmpty(needle) ? -1 : haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // PEG-style header / notedata scanner
    // -------------------------------------------------------------------------

    private sealed class PegParser(string text)
    {
        private readonly string _text = text ?? string.Empty;
        private int _pos;

        private bool EOF => _pos >= _text.Length;
        private char Peek() => EOF ? '\0' : _text[_pos];
        private void Advance(int count = 1) => _pos = Math.Min(_pos + count, _text.Length);

        private void SkipWhitespace()
        {
            while (!EOF && char.IsWhiteSpace(Peek())) Advance();
        }

        private static bool StartsWith(string haystack, int pos, string needle) =>
            pos <= haystack.Length - needle.Length &&
            string.Compare(haystack, pos, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>Reads all global #TAG:VALUE; pairs before the first #NOTEDATA: block.</summary>
        public Dictionary<string, string> ParseGlobalTags()
        {
            _pos = 0;
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (!EOF)
            {
                SkipWhitespace();
                if (StartsWith(_text, _pos, "#NOTEDATA:")) break;

                if (Peek() == '#')
                {
                    var (k, v) = ReadTag();
                    if (!string.IsNullOrEmpty(k))
                        tags[k] = v;
                }
                else
                {
                    Advance();
                }
            }

            return tags;
        }

        /// <summary>Yields the raw text of each #NOTEDATA: … section.</summary>
        public IEnumerable<string> EnumerateNoteDataSections()
        {
            var pos = 0;
            while (true)
            {
                var start = _text.IndexOf("#NOTEDATA:", pos, StringComparison.OrdinalIgnoreCase);
                if (start < 0) yield break;

                var next = _text.IndexOf("#NOTEDATA:", start + 1, StringComparison.OrdinalIgnoreCase);
                var end = next >= 0 ? next : _text.Length;

                yield return _text[start..end];
                pos = end;
            }
        }

        private (string key, string value) ReadTag()
        {
            try
            {
                if (Peek() != '#') return (string.Empty, string.Empty);
                Advance();

                var keyStart = _pos;
                while (!EOF && _text[_pos] is not ':' and not ';' and not '\n' and not '\r')
                    Advance();
                var key = _text[keyStart.._pos].Trim();

                if (EOF || _text[_pos] != ':')
                {
                    var nextSemi = _text.IndexOf(';', _pos);
                    if (nextSemi >= 0) _pos = nextSemi + 1;
                    return (key, string.Empty);
                }

                Advance(); // skip ':'

                var valueStart = _pos;
                while (!EOF && _text[_pos] != ';') Advance();
                var value = _text[valueStart.._pos].Trim();

                if (!EOF) Advance(); // consume ';'
                return (key, value);
            }
            catch
            {
                var semi = _text.IndexOf(';', _pos);
                if (semi >= 0) _pos = semi + 1;
                return (string.Empty, string.Empty);
            }
        }
    }
}
