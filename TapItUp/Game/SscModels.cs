namespace TapItUp.Game;

public sealed class SscSong
{
    public string Title { get; init; } = "Unknown Title";
    public string Artist { get; init; } = "Unknown Artist";
    public string MusicPath { get; init; } = string.Empty;
    public string BannerPath { get; init; } = string.Empty;
    public string BackgroundPath { get; init; } = string.Empty;
    public string PreviewVideoPath { get; init; } = string.Empty;
    public double OffsetSeconds { get; init; }
    public IReadOnlyList<BpmChange> BpmChanges { get; init; } = [];
    /// <summary>
    /// Parsed from #TICKCOUNTS. Each entry defines how many ticks-per-beat apply
    /// starting at a given beat position. Defaults to 4 ticks/beat if absent.
    /// </summary>
    public IReadOnlyList<TickCount> TickCounts { get; init; } = [];
    public IReadOnlyList<SscChart> Charts { get; init; } = [];
    public string? SourcePath { get; init; }
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// On Android, the SAF document tree URI for the song's folder.
    /// Used to open audio and image files via ContentResolver instead of File I/O.
    /// </summary>
    public string? SongDocumentUri { get; set; }
}

public sealed class SscChart
{
    /// <summary> Usually pump-single, pump-double </summary>
    public string StepType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public int Meter { get; init; }
    public IReadOnlyList<ChartNote> Notes { get; init; } = [];

    public double LastNoteTimeSeconds => Notes.Count == 0 ? 0d : Notes.Max(note => note.TimeSeconds);

    public override string ToString()
    {
        var description = string.IsNullOrWhiteSpace(Description) ? string.Empty : $" • {Description}";
        return $"{Difficulty} {Meter}{description}".Trim();
    }
}

public enum NoteType
{
    None = 0,
    Tap = 1,
    HoldStart = 2,
    HoldEnd = 3,
    HoldBody = 4
}

public sealed class ChartNote
{
    public required int Lane { get; init; }
    public required double Beat { get; init; }
    public required double TimeSeconds { get; init; }
    public required NoteType Type { get; init; }

    public bool IsHoldHead => Type == NoteType.HoldStart;
    public bool IsHoldTail => Type == NoteType.HoldEnd;
    public bool IsHoldBody => Type == NoteType.HoldBody;
    public bool IsHold => Type == NoteType.HoldStart || Type == NoteType.HoldEnd || Type == NoteType.HoldBody;
    public bool IsTap => Type == NoteType.Tap;
}

public readonly record struct BpmChange(double Beat, double Bpm);

/// <summary>
/// A single segment of the #TICKCOUNTS map: from <see cref="Beat"/> onward,
/// <see cref="TicksPerBeat"/> ticks are generated per beat inside holds.
/// </summary>
public readonly record struct TickCount(double Beat, int TicksPerBeat);

public enum HitJudgment
{
    Perfect,
    Great,
    Good,
    Bad,
    Miss
}

public sealed class PlayableNote
{
    public required int Lane { get; init; }
    public required double Beat { get; init; }
    public required double TimeSeconds { get; init; }
    public required NoteType Type { get; init; }
    public bool Consumed { get; set; }
    public bool Missed { get; set; }

    public PlayableNote? HoldPartner { get; set; }
    public bool IsHoldActive { get; set; }

    public bool IsHoldHead => Type == NoteType.HoldStart;
    public bool IsHoldTail => Type == NoteType.HoldEnd;
    public bool IsHold => Type == NoteType.HoldStart || Type == NoteType.HoldEnd || Type == NoteType.HoldBody;
    public bool IsTap => Type == NoteType.Tap;
}

/// <summary>
/// A dynamically generated tick event that lives inside a hold note.
/// One tick fires every (60 / bpm) / ticksPerBeat seconds.
/// Perfect if the lane is held at fire time; Miss otherwise.
/// A missed tick does NOT end the hold — the player can re-press for the next one.
/// </summary>
public sealed class HoldTick
{
    public required int Lane { get; init; }
    public required double TimeSeconds { get; init; }
    public bool Scored { get; set; }
}
