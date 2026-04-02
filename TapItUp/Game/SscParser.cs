using TapItUp.Extensions;
using System.Globalization;
using System.Text;

namespace TapItUp.Game;

public static class SscParser
{
    public static SscSong Parse(string content, string? sourcePath = null)
    {
        var parser = new PegParser(content);
        var globalTags = parser.ParseGlobalTags();
        var bpmChanges = ParseBpmString(globalTags.Get("BPMS"));
        var tickCounts = ParseTickCountString(globalTags.Get("TICKCOUNTS"));
        var charts = new List<SscChart>();

        foreach (var noteDataSection in parser.EnumerateNoteDataSections())
        {
            var chart = ParseSingleChart(
                noteDataSection,
                bpmChanges,
                ParseDouble(globalTags.Get("OFFSET")) ?? 0);
            if (chart != null)
                charts.Add(chart);
        }

        return new SscSong
        {
            Title = globalTags.Get("TITLE", "Unknown Title"),
            Artist = globalTags.Get("ARTIST", "Unknown Artist"),
            OffsetSeconds = globalTags.GetDouble("OFFSET", 0),
            BpmChanges = bpmChanges,
            TickCounts = tickCounts,
            Charts = charts,
            SourcePath = sourcePath,
            MusicPath = globalTags.Get("MUSIC", globalTags.Get("SONG", "")),
            BackgroundPath = globalTags.Get("BANNER", globalTags.Get("BACKGROUND", ""))
        };
    }

    // ---------- PEG-style parser (small recursive-descent / token-driven scanner) ----------
    private sealed class PegParser
    {
        private readonly string _text;
        private int _pos;

        public PegParser(string text) => _text = text ?? string.Empty;

        private bool EOF => _pos >= _text.Length;

        private char Peek() => EOF ? '\0' : _text[_pos];

        private void Advance(int count = 1) => _pos = Math.Min(_pos + count, _text.Length);

        private void SkipWhitespace()
        {
            while (!EOF && char.IsWhiteSpace(Peek()))
                Advance();
        }

        private static bool StartsWith(string haystack, int pos, string needle)
            => pos <= haystack.Length - needle.Length &&
               string.Compare(haystack, pos, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0;

        // Parse tags (e.g. #TITLE:Foo;) from current position until we hit the stop marker or EOF
        // This will not advance past the stop marker.
        public Dictionary<string, string> ParseGlobalTags()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _pos = 0;

            while (!EOF)
            {
                SkipWhitespace();

                if (StartsWith(_text, _pos, "#NOTEDATA:"))
                    break;

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

        // Enumerates raw #NOTEDATA: ... sections (text from "#NOTEDATA:" up to next "#NOTEDATA:" or EOF).
        public IEnumerable<string> EnumerateNoteDataSections()
        {
            var pos = 0;
            while (true)
            {
                var idx = IndexOfIgnoreCase("#NOTEDATA:", pos);
                if (idx < 0) yield break;

                // start at the marker
                var start = idx;
                // find next marker
                var next = IndexOfIgnoreCase("#NOTEDATA:", idx + 1);
                var end = next >= 0 ? next : _text.Length;
                var section = _text[start..end];
                yield return section;
                pos = end;
            }
        }

        // Helper: read a single tag at current position (assumes current char '#')
        // Advances position to after the terminating ';' or to EOF.
        private (string key, string value) ReadTag()
        {
            try
            {
                // Expect '#'
                if (Peek() != '#')
                    return (string.Empty, string.Empty);
                Advance();

                // Read key until ':'
                var keyStart = _pos;
                while (!EOF && _text[_pos] != ':' && _text[_pos] != ';' && _text[_pos] != '\n' && _text[_pos] != '\r')
                    Advance();
                var key = _text[keyStart.._pos].Trim();

                if (EOF || _text[_pos] != ':')
                {
                    // malformed tag: attempt to skip to next semicolon
                    var nextSemi = _text.IndexOf(';', _pos);
                    if (nextSemi >= 0) _pos = nextSemi + 1;
                    return (key, string.Empty);
                }
                Advance(); // skip ':'

                // Read value until ';' (value may contain colons)
                var valueStart = _pos;
                while (!EOF && _text[_pos] != ';')
                    Advance();

                var value = _text[valueStart.._pos].Trim();

                if (!EOF && _text[_pos] == ';') Advance(); // consume ';'

                return (key, value);
            }
            catch
            {
                // on any error, attempt to resync by consuming until next semicolon
                var semi = _text.IndexOf(';', _pos);
                if (semi >= 0) _pos = semi + 1;
                return (string.Empty, string.Empty);
            }
        }

        private int IndexOfIgnoreCase(string needle, int start)
            => string.IsNullOrEmpty(needle) ? -1 :
               _text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Single chart parsing (no regex) ----------
    private static SscChart? ParseSingleChart(string noteDataSection, IReadOnlyList<BpmChange> globalBpmChanges, double globalOffsetSeconds)
    {
        // Parse tags within the noteDataSection (simple scanner for #KEY:VALUE;)
        var chartTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        var text = noteDataSection;
        while (i < text.Length)
        {
            // find next '#'
            var hash = text.IndexOf('#', i);
            if (hash < 0) break;
            var colon = text.IndexOf(':', hash + 1);
            if (colon < 0) break;
            var semi = text.IndexOf(';', colon + 1);
            if (semi < 0) break;

            var key = text[(hash + 1)..colon].Trim();
            var value = text[(colon + 1)..semi].Trim();
            if (!string.IsNullOrEmpty(key))
                chartTags[key] = value;

            i = semi + 1;
        }

        var stepType = chartTags.GetValueOrDefault("STEPSTYPE") ?? "";

        // Accept all known pump/dance step types
        var isKnownStepType =
            stepType.Equals("pump-single", StringComparison.OrdinalIgnoreCase) ||
            stepType.Equals("pump-double", StringComparison.OrdinalIgnoreCase) ||
            stepType.Equals("dance-single", StringComparison.OrdinalIgnoreCase) ||
            stepType.Equals("dance-double", StringComparison.OrdinalIgnoreCase);

        if (!isKnownStepType)
        {
            System.Diagnostics.Debug.WriteLine($"   ⚠️ Skipping unknown STEPSTYPE: '{stepType}'");
            return null;
        }

        var description = chartTags.GetValueOrDefault("DESCRIPTION") ?? "";
        var difficulty = chartTags.GetValueOrDefault("DIFFICULTY") ?? "Beginner";
        var meter = int.TryParse(chartTags.GetValueOrDefault("METER"), out var m) ? m : 1;

        var chartBpmString = chartTags.GetValueOrDefault("BPMS");
        var bpmChanges = !string.IsNullOrEmpty(chartBpmString) ? ParseBpmString(chartBpmString) : globalBpmChanges;
        var offsetSeconds = ParseDouble(chartTags.GetValueOrDefault("OFFSET")) ?? globalOffsetSeconds;

        // Find the NOTES subsection and extract measure data
        var notesIdx = IndexOfIgnoreCase(text, "#NOTES:");
        if (notesIdx < 0) return null;

        var notesSub = text[(notesIdx + "#NOTES:".Length)..];

        // Trim leading whitespace/newlines
        notesSub = notesSub.TrimStart();

        // Extract up to terminating ';' for this NOTES (if present)
        var term = notesSub.IndexOf(';');
        var notesBlock = term >= 0 ? notesSub[..term] : notesSub;

        // Split lines, remove empty lines, and find first measure line (heuristic)
        var lines = notesBlock.Split('\n').Select(l => l.Trim('\r')).ToList();
        var measureStart = 0;
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Accept rows of 5 chars (pump-single) or 10 chars (pump-double / dance-double)
            var isNoteRow = line.Length >= 5 && line.All(c => c >= '0' && c <= '4' || c == ' ');
            if (line.Contains(',') || isNoteRow)
            {
                measureStart = idx;
                break;
            }
        }

        var measuresText = string.Join('\n', lines.Skip(measureStart));
        var notes = ParseNotes(measuresText, bpmChanges, offsetSeconds, stepType);

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
            Notes = notes
        };
    }

    private static int IndexOfIgnoreCase(string haystack, string needle)
        => string.IsNullOrEmpty(needle) ? -1 :
           haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

    // ---------- Measures / notes conversion (unchanged algorithmic intent) ----------
    private static List<List<string>> ParseMeasures(string text)
    {
        var measures = new List<List<string>>();
        var currentMeasure = new List<string>();
        var currentRow = new StringBuilder();

        foreach (var ch in text)
        {
            if (ch == ',')
            {
                FlushRow();
                FlushMeasure();
            }
            else if (ch == '\n')
            {
                FlushRow();
            }
            else if (ch == ';')
            {
                FlushRow();
                FlushMeasure();
                break;
            }
            else
            {
                if (ch != '\r')
                    currentRow.Append(ch);
            }
        }

        // Ensure trailing content is flushed
        FlushRow();
        FlushMeasure();

        return measures;

        void FlushRow()
        {
            var row = SanitizeRow(currentRow.ToString());
            currentRow.Clear();
            if (!string.IsNullOrWhiteSpace(row))
                currentMeasure.Add(row);
        }

        void FlushMeasure()
        {
            if (currentMeasure.Count > 0)
            {
                measures.Add(new List<string>(currentMeasure));
                currentMeasure.Clear();
            }
        }
    }

    private static List<ChartNote> ConvertToTimedNotes(
        List<List<string>> measures,
        IReadOnlyList<BpmChange> bpmChanges,
        double offsetSeconds)
    {
        var notes = new List<ChartNote>();
        if (measures == null || measures.Count == 0) return notes;

        for (var m = 0; m < measures.Count; m++)
        {
            var rows = measures[m];
            if (rows == null || rows.Count == 0) continue;

            var rowBeat = 4d / rows.Count;
            for (var r = 0; r < rows.Count; r++)
            {
                var beat = m * 4d + r * rowBeat;
                var row = rows[r];

                for (var lane = 0; lane < row.Length; lane++)
                {
                    var type = row[lane] switch
                    {
                        '1' => NoteType.Tap,
                        '2' => NoteType.HoldStart,
                        '3' => NoteType.HoldEnd,
                        '4' => NoteType.HoldBody,
                        _ => NoteType.None
                    };

                    if (type == NoteType.None) continue;

                    notes.Add(new ChartNote
                    {
                        Lane = lane,
                        Beat = beat,
                        TimeSeconds = BeatToSeconds(beat, bpmChanges) - offsetSeconds,
                        Type = type
                    });
                }
            }
        }

        return notes;
    }

    private static List<BpmChange> ParseBpmString(string bpmText)
    {
        var changes = new List<BpmChange>();
        if (string.IsNullOrWhiteSpace(bpmText)) return changes;

        foreach (var part in bpmText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = part.Split('=');
            if (segments.Length == 2 &&
                double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat) &&
                double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
            {
                changes.Add(new BpmChange(beat, bpm));
            }
        }

        if (changes.Count == 0) changes.Add(new BpmChange(0d, 120d));
        return changes;
    }

    /// <summary>
    /// Parses a #TICKCOUNTS string such as "0=4,32=2" into a list of
    /// <see cref="TickCount"/> segments. Defaults to 4 ticks/beat if the tag is absent.
    /// </summary>
    private static List<TickCount> ParseTickCountString(string? tickText)
    {
        var result = new List<TickCount>();
        if (string.IsNullOrWhiteSpace(tickText))
        {
            result.Add(new TickCount(0d, 4));
            return result;
        }

        foreach (var part in tickText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = part.Split('=');
            if (segments.Length == 2 &&
                double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat) &&
                int.TryParse(segments[1], out var ticks) &&
                ticks >= 0)
            {
                result.Add(new TickCount(beat, ticks));
            }
        }

        if (result.Count == 0) result.Add(new TickCount(0d, 4));
        return result;
    }

    private static List<ChartNote> ParseNotes(string notesText, IReadOnlyList<BpmChange> bpmChanges, double offsetSeconds, string stepType = "pump-single")
    {
        var notes = new List<ChartNote>();
        if (string.IsNullOrWhiteSpace(notesText) || bpmChanges == null || bpmChanges.Count == 0) return notes;

        // pump-double / dance-double rows are 10 characters wide; singles are 5
        var isDouble =
            stepType.Equals("pump-double", StringComparison.OrdinalIgnoreCase) ||
            stepType.Equals("dance-double", StringComparison.OrdinalIgnoreCase);
        var expectedRowLength = isDouble ? 10 : 5;

        notesText = notesText.Trim().TrimEnd(';');

        var measures = notesText
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(measure => measure
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SanitizeRow)
                .Where(row => row.Length >= expectedRowLength && row.All(c => c >= '0' && c <= '4'))
                .ToList())
            .Where(measure => measure.Count > 0)
            .ToList();

        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            var rows = measures[measureIndex];
            if (rows.Count == 0) continue;

            var rowBeatSpan = 4d / rows.Count;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var beat = (measureIndex * 4d) + (rowIndex * rowBeatSpan);

                // Only read up to the expected lane count to avoid stray characters
                for (var lane = 0; lane < Math.Min(expectedRowLength, row.Length); lane++)
                {
                    var cell = row[lane];
                    var noteType = cell switch
                    {
                        '1' => NoteType.Tap,
                        '2' => NoteType.HoldStart,
                        '3' => NoteType.HoldEnd,
                        '4' => NoteType.HoldBody,
                        _ => NoteType.None
                    };

                    if (noteType != NoteType.None)
                    {
                        notes.Add(new ChartNote
                        {
                            Lane = lane,
                            Beat = beat,
                            TimeSeconds = BeatToSeconds(beat, bpmChanges) - offsetSeconds,
                            Type = noteType
                        });
                    }
                }
            }
        }

        return notes;
    }

    private static string SanitizeRow(string rawRow)
    {
        var commentIndex = rawRow.IndexOf("//", StringComparison.Ordinal);
        var row = commentIndex >= 0 ? rawRow[..commentIndex] : rawRow;
        return row.Trim();
    }

    private static double BeatToSeconds(double beat, IReadOnlyList<BpmChange> bpmChanges)
    {
        var ordered = bpmChanges.OrderBy(c => c.Beat).ToList();
        if (ordered.Count == 0) return beat / 120d * 60d;

        var seconds = 0d;
        var currentBpm = ordered[0].Bpm;
        var lastBeat = 0d;

        foreach (var change in ordered.Where(c => c.Beat <= beat))
        {
            seconds += (change.Beat - lastBeat) / currentBpm * 60d;
            lastBeat = change.Beat;
            currentBpm = change.Bpm;
        }

        seconds += (beat - lastBeat) / currentBpm * 60d;
        return seconds;
    }

    private static double? ParseDouble(string? text)
    {
        if (text == null) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
