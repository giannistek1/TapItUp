namespace PumpMaui.Game;

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
    public IReadOnlyList<SscChart> Charts { get; init; } = [];
    public string? SourcePath { get; init; }
}

public sealed class SscChart
{
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
    HoldBody = 4 // Optional for long holds
}

public sealed class ChartNote
{
    public required int Lane { get; init; }
    public required double Beat { get; init; }
    public required double TimeSeconds { get; init; }
    public required NoteType Type { get; init; }

    // Convenience properties
    public bool IsHoldHead => Type == NoteType.HoldStart;
    public bool IsHoldTail => Type == NoteType.HoldEnd;
    public bool IsHoldBody => Type == NoteType.HoldBody;
    public bool IsHold => Type == NoteType.HoldStart || Type == NoteType.HoldEnd || Type == NoteType.HoldBody;
    public bool IsTap => Type == NoteType.Tap;
}

public readonly record struct BpmChange(double Beat, double Bpm);

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

    // Hold note tracking
    public PlayableNote? HoldPartner { get; set; } // For linking hold start/end
    public bool IsHoldActive { get; set; } // For tracking active holds

    // Convenience properties
    public bool IsHoldHead => Type == NoteType.HoldStart;
    public bool IsHoldTail => Type == NoteType.HoldEnd;
    public bool IsHold => Type == NoteType.HoldStart || Type == NoteType.HoldEnd || Type == NoteType.HoldBody;
    public bool IsTap => Type == NoteType.Tap;
}
