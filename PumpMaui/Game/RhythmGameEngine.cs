namespace PumpMaui.Game;

public sealed class RhythmGameEngine
{
    private readonly Dictionary<HitJudgment, int> _counts = Enum
        .GetValues<HitJudgment>()
        .ToDictionary(judgment => judgment, _ => 0);

    private readonly double[] _laneFlashTimes = [-10d, -10d, -10d, -10d, -10d];
    private readonly bool[] _laneHoldActive = [false, false, false, false, false];
    private List<PlayableNote> _notes = [];

    public SscSong? Song { get; private set; }
    public SscChart? Chart { get; private set; }
    public IReadOnlyList<PlayableNote> Notes => _notes;
    public IReadOnlyDictionary<HitJudgment, int> Counts => _counts;
    public double CurrentTimeSeconds { get; private set; }
    public bool IsPlaying { get; private set; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public int Score { get; private set; }
    public string Grade { get; private set; } = "D";
    public string LastJudgmentText { get; private set; } = "READY";
    public bool FullCombo => _counts[HitJudgment.Bad] == 0 && _counts[HitJudgment.Miss] == 0;
    public double AccuracyPercent => PhoenixScoring.MaxScore == 0 ? 0d : Score / (double)PhoenixScoring.MaxScore * 100d;
    public double SongDurationSeconds => (Chart?.LastNoteTimeSeconds ?? 0d) + 2.5d;

    public void Load(SscSong song, SscChart chart)
    {
        Song = song;
        Chart = chart;
        _notes = chart.Notes.Select(note => new PlayableNote
        {
            Lane = note.Lane,
            Beat = note.Beat,
            TimeSeconds = note.TimeSeconds,
            Type = note.Type
        }).ToList();

        // Link hold starts and ends
        LinkHoldNotes();
        ResetSession();
    }

    private void LinkHoldNotes()
    {
        // Group notes by lane and sort by time
        var notesByLane = _notes.GroupBy(n => n.Lane)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.TimeSeconds).ToList());

        foreach (var laneNotes in notesByLane.Values)
        {
            for (var i = 0; i < laneNotes.Count; i++)
            {
                var note = laneNotes[i];
                if (note.Type == NoteType.HoldStart)
                {
                    // Find the matching hold end
                    for (var j = i + 1; j < laneNotes.Count; j++)
                    {
                        var endNote = laneNotes[j];
                        if (endNote.Type == NoteType.HoldEnd)
                        {
                            note.HoldPartner = endNote;
                            endNote.HoldPartner = note;
                            break;
                        }
                    }
                }
            }
        }
    }

    public void Start()
    {
        if (Chart is null)
        {
            return;
        }

        ResetSession();
        IsPlaying = true;
        LastJudgmentText = "GO";
    }

    public void Stop()
    {
        IsPlaying = false;
        for (var i = 0; i < _laneHoldActive.Length; i++)
        {
            _laneHoldActive[i] = false;
        }
    }

    public void Update(double elapsedSeconds)
    {
        CurrentTimeSeconds = elapsedSeconds;
        if (!IsPlaying || Chart is null)
        {
            return;
        }

        // Handle missed notes and automatic hold endings
        foreach (var note in _notes.Where(note => !note.Consumed && !note.Missed))
        {
            var delta = elapsedSeconds - note.TimeSeconds;

            if (delta > PhoenixScoring.BadWindowSeconds)
            {
                // Note is past the bad window
                note.Consumed = true;
                note.Missed = true;

                if (note.Type == NoteType.HoldStart)
                {
                    // Missed hold start
                    RegisterJudgment(HitJudgment.Miss);
                    _laneHoldActive[note.Lane] = false;
                }
                else if (note.Type == NoteType.Tap)
                {
                    RegisterJudgment(HitJudgment.Miss);
                }
                // Don't handle HoldEnd here - it's handled automatically below
            }
            else if (note.Type == NoteType.HoldEnd && _laneHoldActive[note.Lane])
            {
                // Check if hold end should be automatically triggered
                if (Math.Abs(delta) <= PhoenixScoring.PerfectWindowSeconds)
                {
                    // Hold end is within perfect window - auto-complete with perfect timing
                    note.Consumed = true;
                    _laneHoldActive[note.Lane] = false;

                    // Find and deactivate the hold start
                    if (note.HoldPartner != null)
                    {
                        note.HoldPartner.IsHoldActive = false;
                    }

                    RegisterJudgment(HitJudgment.Perfect);
                }
                else if (Math.Abs(delta) <= PhoenixScoring.GreatWindowSeconds)
                {
                    // Hold end is within great window - auto-complete with great timing
                    note.Consumed = true;
                    _laneHoldActive[note.Lane] = false;

                    // Find and deactivate the hold start
                    if (note.HoldPartner != null)
                    {
                        note.HoldPartner.IsHoldActive = false;
                    }

                    RegisterJudgment(HitJudgment.Great);
                }
                else if (Math.Abs(delta) <= PhoenixScoring.GoodWindowSeconds)
                {
                    // Hold end is within good window - auto-complete with good timing
                    note.Consumed = true;
                    _laneHoldActive[note.Lane] = false;

                    // Find and deactivate the hold start
                    if (note.HoldPartner != null)
                    {
                        note.HoldPartner.IsHoldActive = false;
                    }

                    RegisterJudgment(HitJudgment.Good);
                }
                else if (delta > PhoenixScoring.GoodWindowSeconds)
                {
                    // Hold was held too long past the end - bad/miss
                    note.Consumed = true;
                    note.Missed = true;
                    _laneHoldActive[note.Lane] = false;

                    if (note.HoldPartner != null)
                    {
                        note.HoldPartner.IsHoldActive = false;
                    }

                    RegisterJudgment(HitJudgment.Bad);
                }
            }
        }

        // Check for hold drops (player released during hold)
        for (var lane = 0; lane < _laneHoldActive.Length; lane++)
        {
            if (_laneHoldActive[lane])
            {
                // Find if there's an active hold that should still be held
                var activeHold = _notes.FirstOrDefault(n =>
                    n.Lane == lane &&
                    n.Type == NoteType.HoldStart &&
                    n.IsHoldActive &&
                    n.Consumed);

                if (activeHold?.HoldPartner != null)
                {
                    // Check if the hold was dropped (released early)
                    var timeUntilEnd = activeHold.HoldPartner.TimeSeconds - elapsedSeconds;

                    // If hold end is more than bad window away and player isn't holding, it's a drop
                    if (timeUntilEnd > PhoenixScoring.BadWindowSeconds)
                    {
                        // Don't auto-fail here - let them re-press if needed
                        // The hold will only fail if they don't hold through to the end timing
                    }
                }
            }
        }

        if (elapsedSeconds >= SongDurationSeconds && _notes.All(note => note.Consumed))
        {
            IsPlaying = false;
            LastJudgmentText = $"FINAL {Grade}";
        }
    }

    public void HandleLaneHit(int lane)
    {
        _laneFlashTimes[lane] = CurrentTimeSeconds;
        if (!IsPlaying || Chart is null)
        {
            return;
        }

        // If there's already an active hold, this press doesn't do anything special
        // The hold will automatically end when the tail reaches the receptor
        if (_laneHoldActive[lane])
        {
            return; // Hold is already active, no need to do anything
        }

        // Find the closest note to hit
        var candidate = _notes
            .Where(note => !note.Consumed && note.Lane == lane &&
                          (note.Type == NoteType.Tap || note.Type == NoteType.HoldStart))
            .OrderBy(note => Math.Abs(note.TimeSeconds - CurrentTimeSeconds))
            .FirstOrDefault();

        if (candidate is null)
        {
            return;
        }

        var delta = CurrentTimeSeconds - candidate.TimeSeconds;
        var judgment = PhoenixScoring.GetJudgment(delta);
        if (judgment == HitJudgment.Miss)
        {
            return;
        }

        candidate.Consumed = true;

        if (candidate.Type == NoteType.HoldStart)
        {
            // Start the hold
            _laneHoldActive[lane] = true;
            candidate.IsHoldActive = true;
            RegisterJudgment(judgment);
        }
        else if (candidate.Type == NoteType.Tap)
        {
            RegisterJudgment(judgment);
        }
    }

    public void HandleLaneRelease(int lane)
    {
        // With the new system, releases don't immediately end holds
        // Holds automatically end when the tail note reaches the receptor
        // We still track releases for potential hold drops, but don't immediately end the hold

        if (!IsPlaying || Chart is null)
        {
            return;
        }

        // Optional: You could add a penalty for releasing too early
        // But the hold continues and can still be completed successfully
    }

    public double GetLaneFlashAge(int lane)
    {
        return CurrentTimeSeconds - _laneFlashTimes[lane];
    }

    public bool IsLaneHoldActive(int lane)
    {
        return lane >= 0 && lane < _laneHoldActive.Length && _laneHoldActive[lane];
    }

    private void RegisterJudgment(HitJudgment judgment)
    {
        _counts[judgment]++;
        if (PhoenixScoring.BreaksCombo(judgment))
        {
            Combo = 0;
        }
        else
        {
            Combo++;
            MaxCombo = Math.Max(MaxCombo, Combo);
        }

        Score = PhoenixScoring.CalculateScore(_counts, _notes.Count);
        Grade = PhoenixScoring.CalculateGrade(Score, FullCombo);
        LastJudgmentText = judgment.ToString().ToUpperInvariant();
    }

    private void ResetSession()
    {
        foreach (var judgment in _counts.Keys.ToList())
        {
            _counts[judgment] = 0;
        }

        foreach (var note in _notes)
        {
            note.Consumed = false;
            note.Missed = false;
            note.IsHoldActive = false;
        }

        for (var lane = 0; lane < _laneFlashTimes.Length; lane++)
        {
            _laneFlashTimes[lane] = -10d;
            _laneHoldActive[lane] = false;
        }

        CurrentTimeSeconds = 0d;
        Combo = 0;
        MaxCombo = 0;
        Score = 0;
        Grade = "D";
        LastJudgmentText = Chart is null ? "READY" : "SELECT SONG";
        IsPlaying = false;
    }
}
