using System.Text.RegularExpressions;

namespace PumpMaui.Game;

public static partial class SscParser
{
    [GeneratedRegex(@"#(?<key>[A-Z_]+):(?<value>[^;]*);", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"#NOTEDATA:;(.*?)(?=#NOTEDATA:;|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoteDataRegex();

    public static SscSong Parse(string content, string? sourcePath = null)
    {
        var globalTags = ParseTags(content);
        var offsetSeconds = ParseDouble(globalTags.GetValueOrDefault("OFFSET")) ?? 0d;
        var bpmChanges = ParseBpmString(globalTags.GetValueOrDefault("BPMS") ?? string.Empty);

        var charts = new List<SscChart>();

        // Parse each NOTEDATA section
        foreach (Match match in NoteDataRegex().Matches(content))
        {
            var noteDataSection = match.Groups[1].Value;
            var chart = ParseSingleChart(noteDataSection, bpmChanges, offsetSeconds);
            if (chart != null)
            {
                charts.Add(chart);
            }
        }

        return new SscSong
        {
            Title = globalTags.GetValueOrDefault("TITLE") ?? "Unknown Title",
            Artist = globalTags.GetValueOrDefault("ARTIST") ?? "Unknown Artist",
            MusicPath = globalTags.GetValueOrDefault("MUSIC") ?? string.Empty,
            BannerPath = globalTags.GetValueOrDefault("BANNER") ?? string.Empty,
            BackgroundPath = globalTags.GetValueOrDefault("BACKGROUND") ?? string.Empty,
            PreviewVideoPath = globalTags.GetValueOrDefault("PREVIEWVID") ?? string.Empty,
            OffsetSeconds = offsetSeconds,
            BpmChanges = bpmChanges,
            Charts = charts,
            SourcePath = sourcePath
        };
    }

    private static SscChart? ParseSingleChart(string noteDataSection, IReadOnlyList<BpmChange> globalBpmChanges, double globalOffsetSeconds)
    {
        var chartTags = ParseTags(noteDataSection);

        var stepType = chartTags.GetValueOrDefault("STEPSTYPE") ?? "";
        if (!stepType.Equals("pump-single", StringComparison.OrdinalIgnoreCase) &&
            !stepType.Equals("dance-single", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var description = chartTags.GetValueOrDefault("DESCRIPTION") ?? "";
        var difficulty = chartTags.GetValueOrDefault("DIFFICULTY") ?? "Beginner";
        var meter = int.TryParse(chartTags.GetValueOrDefault("METER"), out var m) ? m : 1;

        // Use chart-specific BPMs if available, otherwise use global BPMs
        var chartBpmString = chartTags.GetValueOrDefault("BPMS");
        var bpmChanges = !string.IsNullOrEmpty(chartBpmString) ?
            ParseBpmString(chartBpmString) : globalBpmChanges;

        // Use chart-specific offset if available, otherwise use global offset
        var offsetSeconds = ParseDouble(chartTags.GetValueOrDefault("OFFSET")) ?? globalOffsetSeconds;

        // Find the #NOTES: section and extract the note data after it
        var notesMarkerIndex = noteDataSection.IndexOf("#NOTES:", StringComparison.OrdinalIgnoreCase);
        if (notesMarkerIndex == -1)
        {
            System.Diagnostics.Debug.WriteLine("❌ No #NOTES: section found in chart");
            return null;
        }

        // Get everything after "#NOTES:"
        var notesSection = noteDataSection.Substring(notesMarkerIndex + "#NOTES:".Length);

        // Split by lines and remove empty lines at the beginning
        var lines = notesSection.Split('\n', StringSplitOptions.TrimEntries);
        var notesStartIndex = 0;

        // Skip any empty lines at the beginning
        while (notesStartIndex < lines.Length && string.IsNullOrWhiteSpace(lines[notesStartIndex]))
        {
            notesStartIndex++;
        }

        if (notesStartIndex >= lines.Length)
        {
            System.Diagnostics.Debug.WriteLine("❌ No note data found after #NOTES:");
            return null;
        }

        var notesLines = lines.Skip(notesStartIndex);
        var notesText = string.Join('\n', notesLines);

        System.Diagnostics.Debug.WriteLine($"📝 Parsing notes for {difficulty} {meter}, notes text length: {notesText.Length}");

        var notes = ParseNotes(notesText, bpmChanges, offsetSeconds);
        System.Diagnostics.Debug.WriteLine($"🎵 Found {notes.Count} notes in chart");

        if (notes.Count == 0)
        {
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

    private static Dictionary<string, string> ParseTags(string section)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagRegex().Matches(section))
        {
            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();
            tags[key] = value;
        }

        return tags;
    }

    private static List<BpmChange> ParseBpmString(string bpmText)
    {
        var changes = new List<BpmChange>();
        if (string.IsNullOrWhiteSpace(bpmText))
        {
            return changes;
        }

        foreach (var part in bpmText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = part.Split('=');
            if (segments.Length == 2 &&
                double.TryParse(segments[0], out var beat) &&
                double.TryParse(segments[1], out var bpm))
            {
                changes.Add(new BpmChange(beat, bpm));
            }
        }

        if (changes.Count == 0)
        {
            changes.Add(new BpmChange(0d, 120d));
        }

        return changes;
    }

    private static List<ChartNote> ParseNotes(string notesText, IReadOnlyList<BpmChange> bpmChanges, double offsetSeconds)
    {
        var notes = new List<ChartNote>();
        if (string.IsNullOrWhiteSpace(notesText) || bpmChanges.Count == 0)
        {
            return notes;
        }

        // Clean up the notes text - remove any trailing semicolons or other markers
        notesText = notesText.Trim().TrimEnd(';');

        var measures = notesText
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(measure => measure
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SanitizeRow)
                .Where(row => row.Length >= 5 && row.All(c => c >= '0' && c <= '4')) // Valid note row
                .ToList())
            .Where(measure => measure.Count > 0)
            .ToList();

        System.Diagnostics.Debug.WriteLine($"📊 Found {measures.Count} measures to parse");

        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            var rows = measures[measureIndex];
            var rowBeatSpan = 4d / rows.Count;

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var beat = (measureIndex * 4d) + (rowIndex * rowBeatSpan);

                for (var lane = 0; lane < Math.Min(5, row.Length); lane++)
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

        System.Diagnostics.Debug.WriteLine($"✅ Parsed {notes.Count} total notes");
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
        var ordered = bpmChanges.OrderBy(change => change.Beat).ToList();
        if (ordered.Count == 0)
        {
            return beat / 120d * 60d;
        }

        var seconds = 0d;
        var currentBpm = ordered[0].Bpm;
        var lastBeat = 0d;

        foreach (var change in ordered.Where(change => change.Beat <= beat))
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
        return double.TryParse(text, out var result) ? result : null;
    }
}
