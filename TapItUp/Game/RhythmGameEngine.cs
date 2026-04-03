namespace TapItUp.Game;

/// <summary>
/// Represents a group of notes that all fall within <see cref="RhythmGameEngine.ChordWindowSeconds"/>
/// of each other and must all be pressed to count as a single hit.
/// </summary>
internal sealed class PendingChord
{
    /// <summary>All notes that belong to this chord.</summary>
    public List<PlayableNote> Notes { get; } = [];

    /// <summary>The earliest note time in the chord — used as the reference for judgement timing.</summary>
    public double ReferenceTimeSeconds => Notes.Min(n => n.TimeSeconds);

    /// <summary>Lanes that have been pressed since this chord became active.</summary>
    public HashSet<int> PressedLanes { get; } = [];

    /// <summary>Whether all lanes in the chord have been pressed.</summary>
    public bool IsComplete => Notes.All(n => PressedLanes.Contains(n.Lane));

    /// <summary>Returns true when <paramref name="currentTime"/> has passed the bad window.</summary>
    public bool IsExpired(double currentTime, double badWindow)
        => currentTime - ReferenceTimeSeconds > badWindow;
}

public sealed class RhythmGameEngine
{
    private readonly Dictionary<HitJudgment, int> _counts = Enum
        .GetValues<HitJudgment>()
        .ToDictionary(judgment => judgment, _ => 0);

    private readonly double[] _laneFlashTimes = [-10d, -10d, -10d, -10d, -10d];
    private readonly bool[] _laneHoldActive = [false, false, false, false, false];
    private readonly bool[] _lanePressed = [false, false, false, false, false];

    // Tracks the most recent judgment on each lane for per-lane visual feedback.
    private readonly HitJudgment[] _laneLastJudgment =
        [HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss];

    private List<PlayableNote> _notes = [];
    private List<HoldTick> _holdTicks = [];

    private readonly List<PendingChord> _pendingChords = [];

    private const double PreHoldAcceptanceSeconds = 0.05d;
    private const double TickWindowSeconds = 0.075d;

    /// <summary>
    /// Notes whose times fall within this window are treated as one simultaneous chord.
    /// ALL lanes in the chord must be pressed; pressing only some is a miss.
    /// </summary>
    private const double ChordWindowSeconds = 0.020d;

    public SscSong? Song { get; private set; }
    public SscChart? Chart { get; private set; }
    public IReadOnlyList<PlayableNote> Notes => _notes;
    public IReadOnlyList<HoldTick> HoldTicks => _holdTicks;
    public IReadOnlyDictionary<HitJudgment, int> Counts => _counts;
    public double CurrentTimeSeconds { get; private set; }
    public bool IsPlaying { get; private set; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public int MissCombo { get; private set; }
    public int Score { get; private set; }
    public string Grade { get; private set; } = "D";
    public string Plate { get; private set; } = "";
    public string LastJudgmentText { get; private set; } = "READY";
    public bool FullCombo => _counts[HitJudgment.Bad] == 0 && _counts[HitJudgment.Miss] == 0;

    /// <summary>
    /// Controls which judgement-timing-window preset is used. Can be changed before or after Load().
    /// Defaults to Standard.
    /// </summary>
    public JudgmentDifficulty JudgmentDifficulty { get; set; } = JudgmentDifficulty.Standard;

    /// <summary>Total scoreable events: chord groups + hold ticks.</summary>
    public int TotalNoteCount => _chordGroupCount + _holdTicks.Count;

    public double AccuracyPercent => PhoenixScoring.MaxScore == 0 ? 0d : Score / (double)PhoenixScoring.MaxScore * 100d;
    public double SongDurationSeconds => (Chart?.LastNoteTimeSeconds ?? 0d) + 2.5d;

    private int _chordGroupCount;

    // -------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------

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

        LinkHoldNotes();
        GenerateHoldTicks(song);
        ComputeChordGroupCount();
        ResetSession();
    }

    // -------------------------------------------------------------------------
    // Chord group count
    // -------------------------------------------------------------------------

    private void ComputeChordGroupCount()
    {
        var scoreable = _notes
            .Where(n => n.Type == NoteType.Tap || n.Type == NoteType.HoldStart)
            .OrderBy(n => n.TimeSeconds)
            .ToList();

        _chordGroupCount = 0;
        var i = 0;
        while (i < scoreable.Count)
        {
            _chordGroupCount++;
            var groupTime = scoreable[i].TimeSeconds;
            while (i < scoreable.Count &&
                   scoreable[i].TimeSeconds - groupTime <= ChordWindowSeconds)
            {
                i++;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Hold tick generation
    // -------------------------------------------------------------------------

    private void GenerateHoldTicks(SscSong song)
    {
        _holdTicks = [];

        var bpmChanges = song.BpmChanges;
        var tickCounts = song.TickCounts;

        if (bpmChanges.Count == 0) return;

        foreach (var head in _notes.Where(n => n.Type == NoteType.HoldStart && n.HoldPartner != null))
        {
            var tail = head.HoldPartner!;
            var headTime = head.TimeSeconds;
            var tailTime = tail.TimeSeconds;

            if (tailTime <= headTime) continue;

            var currentBeat = head.Beat;
            var currentTime = headTime;

            while (currentTime < tailTime)
            {
                var ticksPerBeat = GetTicksPerBeat(currentBeat, tickCounts);
                if (ticksPerBeat <= 0) break;

                var bpm = GetBpmAt(currentBeat, bpmChanges);
                if (bpm <= 0) break;

                var secondsPerTick = 60.0 / bpm / ticksPerBeat;
                if (secondsPerTick <= 0) break;

                var nextTime = currentTime + secondsPerTick;
                var nextBeat = currentBeat + 1.0 / ticksPerBeat;

                if (nextTime < tailTime - 0.001)
                    _holdTicks.Add(new HoldTick { Lane = head.Lane, TimeSeconds = nextTime });

                currentTime = nextTime;
                currentBeat = nextBeat;
            }
        }

        _holdTicks.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    private static int GetTicksPerBeat(double beat, IReadOnlyList<TickCount> tickCounts)
    {
        var active = 4;
        foreach (var tc in tickCounts)
        {
            if (tc.Beat <= beat + 0.0001) active = tc.TicksPerBeat;
            else break;
        }
        return active;
    }

    private static double GetBpmAt(double beat, IReadOnlyList<BpmChange> bpmChanges)
    {
        var bpm = bpmChanges[0].Bpm;
        foreach (var bc in bpmChanges)
        {
            if (bc.Beat <= beat + 0.0001) bpm = bc.Bpm;
            else break;
        }
        return bpm;
    }

    // -------------------------------------------------------------------------
    // Hold note linking
    // -------------------------------------------------------------------------

    private void LinkHoldNotes()
    {
        var notesByLane = _notes.GroupBy(n => n.Lane)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.TimeSeconds).ToList());

        foreach (var laneNotes in notesByLane.Values)
        {
            for (var i = 0; i < laneNotes.Count; i++)
            {
                var note = laneNotes[i];
                if (note.Type != NoteType.HoldStart) continue;

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

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public void Start()
    {
        if (Chart is null) return;
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
            _lanePressed[i] = false;
        }
    }

    // -------------------------------------------------------------------------
    // Update loop
    // -------------------------------------------------------------------------

    public void Update(double elapsedSeconds)
    {
        CurrentTimeSeconds = elapsedSeconds;
        if (!IsPlaying || Chart is null) return;

        var badWindow = PhoenixScoring.GetBadWindow(this.JudgmentDifficulty);

        // --- Expire pending chords whose bad window has passed ---
        for (var ci = _pendingChords.Count - 1; ci >= 0; ci--)
        {
            var chord = _pendingChords[ci];
            if (!chord.IsExpired(elapsedSeconds, badWindow)) continue;

            // Mark every note (pressed or not) as consumed+missed and end any active holds
            foreach (var n in chord.Notes)
            {
                if (!n.Consumed)
                {
                    n.Consumed = true;
                    n.Missed = true;
                    if (n.Type == NoteType.HoldStart)
                        _laneHoldActive[n.Lane] = false;
                }
            }

            RegisterJudgment(HitJudgment.Miss);
            _pendingChords.RemoveAt(ci);
        }

        // --- Regular note processing (with chord grouping for auto-miss) ---
        var pendingNotes = _pendingChords.SelectMany(c => c.Notes).ToHashSet();

        // Group notes that should be treated as chords for auto-miss
        var missedNotes = _notes
            .Where(n => !n.Consumed && !n.Missed && !pendingNotes.Contains(n))
            .ToList();

        var processedThisFrame = new HashSet<PlayableNote>();

        foreach (var note in missedNotes)
        {
            if (processedThisFrame.Contains(note)) continue;

            var delta = elapsedSeconds - note.TimeSeconds;

            // Pre-press: button already held when a hold head arrives
            if (note.Type == NoteType.HoldStart && !_laneHoldActive[note.Lane] && _lanePressed[note.Lane])
            {
                // Only accept pre-press within the acceptable window
                if (delta >= -PreHoldAcceptanceSeconds && delta <= badWindow)
                {
                    System.Diagnostics.Debug.WriteLine($"🎯 PRE-PRESS CHECK: Lane {note.Lane} at {elapsedSeconds:F3}s, note time: {note.TimeSeconds:F3}s, delta: {delta:F3}s, _lanePressed: {_lanePressed[note.Lane]}");

                    // Check if this hold-start is part of a chord
                    var chordMembers = missedNotes
                        .Where(n => !processedThisFrame.Contains(n) &&
                                    n.Type == NoteType.HoldStart &&
                                    Math.Abs(n.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                        .ToList();

                    if (chordMembers.Count == 1)
                    {
                        // Solo hold-start — activate immediately
                        System.Diagnostics.Debug.WriteLine($"  ✅ Activating solo hold via pre-press");
                        ActivateHold(note);
                        RegisterJudgment(HitJudgment.Perfect);
                        processedThisFrame.Add(note);
                    }
                    else
                    {
                        // Multi-hold chord — only consume if ALL lanes are pressed
                        var allPressed = chordMembers.All(m => _lanePressed[m.Lane]);

                        if (allPressed)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ✅ Activating chord hold via pre-press");
                            foreach (var member in chordMembers)
                            {
                                ActivateHold(member);
                                processedThisFrame.Add(member);
                            }
                            RegisterJudgment(HitJudgment.Perfect);
                        }
                        // If not all pressed, let them continue scrolling; they'll form a PendingChord
                        // when the first lane is hit, or auto-miss if all are ignored
                    }

                    continue;
                }
            }

            // Handle hold-end notes for active holds
            if (note.Type == NoteType.HoldEnd)
            {
                if (_laneHoldActive[note.Lane])
                {
                    // Hold is active - check if we've reached the judgment point
                    if (delta >= 0)
                    {
                        // At or past the hold-end note - judge based on whether button is still pressed
                        note.Consumed = true;
                        _laneHoldActive[note.Lane] = false;
                        if (note.HoldPartner != null) note.HoldPartner.IsHoldActive = false;

                        // Hold notes are binary: Perfect if held, Miss if not
                        var judgment = _lanePressed[note.Lane] ? HitJudgment.Perfect : HitJudgment.Miss;

                        RegisterJudgment(judgment);
                        processedThisFrame.Add(note);
                    }
                }
                else
                {
                    // Hold is not active
                    if (delta > badWindow)
                    {
                        // Hold was never activated (player missed the hold start) - auto-consume the tail
                        note.Consumed = true;
                        note.Missed = true;
                        processedThisFrame.Add(note);
                        // Don't register a judgment - the miss was already counted on the hold start
                    }
                }

                continue; // Skip chord grouping for hold-end notes
            }

            // Auto-miss: tap/hold-start note has passed the bad window (LATE timing window)
            // Only process notes that are truly past their timing window
            if (delta > badWindow && (note.Type == NoteType.Tap || note.Type == NoteType.HoldStart))
            {
                // Find all simultaneous notes (chord) that also need to be missed
                var chordMembers = missedNotes
                    .Where(n => !processedThisFrame.Contains(n) &&
                                (n.Type == NoteType.Tap || n.Type == NoteType.HoldStart) &&
                                Math.Abs(n.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                    .ToList();

                // Mark all chord members as consumed and missed
                foreach (var member in chordMembers)
                {
                    member.Consumed = true;
                    member.Missed = true;

                    if (member.Type == NoteType.HoldStart)
                        _laneHoldActive[member.Lane] = false;

                    processedThisFrame.Add(member);
                }

                // Register only ONE miss for the entire chord
                RegisterJudgment(HitJudgment.Miss);
            }
        }

        // --- Hold tick evaluation ---
        // Check both if button is pressed AND if the hold is actually active
        foreach (var tick in _holdTicks.Where(t => !t.Scored))
        {
            var delta = elapsedSeconds - tick.TimeSeconds;
            if (delta < -TickWindowSeconds) break;

            tick.Scored = true;
            // Only give Perfect if the hold is active AND the button is pressed
            var isHoldingCorrectly = _laneHoldActive[tick.Lane] && _lanePressed[tick.Lane];

            System.Diagnostics.Debug.WriteLine($"✓ HOLD TICK: Lane {tick.Lane} at {elapsedSeconds:F3}s, Active={_laneHoldActive[tick.Lane]}, Pressed={_lanePressed[tick.Lane]}, Result={(isHoldingCorrectly ? "PERFECT" : "MISS")}");

            RegisterJudgment(isHoldingCorrectly ? HitJudgment.Perfect : HitJudgment.Miss);
        }

        // Check if game should end - exclude HoldBody notes as they're visual only
        var scoreableNotes = _notes.Where(n => n.Type != NoteType.HoldBody).ToList();

        if (elapsedSeconds >= SongDurationSeconds && scoreableNotes.All(note => note.Consumed))
        {
            System.Diagnostics.Debug.WriteLine($"🎮 Song ending - elapsedSeconds: {elapsedSeconds:F2}, SongDurationSeconds: {SongDurationSeconds:F2}");
            System.Diagnostics.Debug.WriteLine($"   All scoreable notes consumed: {scoreableNotes.Count(n => n.Consumed)}/{scoreableNotes.Count}");
            IsPlaying = false;
            LastJudgmentText = $"FINAL {Grade}";
        }
        else if (elapsedSeconds >= SongDurationSeconds)
        {
            // Debug: show which notes aren't consumed yet
            var unconsumed = scoreableNotes.Where(n => !n.Consumed).ToList();
            if (unconsumed.Count > 0 && unconsumed.Count <= 10)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Song time reached but {unconsumed.Count} scoreable notes not consumed:");
                foreach (var n in unconsumed)
                {
                    System.Diagnostics.Debug.WriteLine($"   Lane {n.Lane}, Type {n.Type}, Time {n.TimeSeconds:F2}s, Missed: {n.Missed}");
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Input handlers
    // -------------------------------------------------------------------------

    public void HandleLaneHit(int lane)
    {
        System.Diagnostics.Debug.WriteLine($"🔵 BUTTON PRESS: Lane {lane} at time {CurrentTimeSeconds:F3}s");
        _laneFlashTimes[lane] = CurrentTimeSeconds;
        _lanePressed[lane] = true;

        if (!IsPlaying || Chart is null) return;

        if (_laneHoldActive[lane])
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠️ Lane {lane} already has an active hold - ignoring press");
            return;
        }

        // --- Check if this press contributes to a pending chord ---
        var pendingChord = _pendingChords
            .FirstOrDefault(c => c.Notes.Any(n => n.Lane == lane) && !c.PressedLanes.Contains(lane));

        if (pendingChord != null)
        {
            pendingChord.PressedLanes.Add(lane);
            _laneFlashTimes[lane] = CurrentTimeSeconds;

            if (pendingChord.IsComplete)
            {
                var worstJudgment = pendingChord.Notes
                    .Select(n => PhoenixScoring.GetJudgment(CurrentTimeSeconds - n.TimeSeconds, this.JudgmentDifficulty))
                    .OrderByDescending(j => j)
                    .First();

                foreach (var n in pendingChord.Notes)
                {
                    n.Consumed = true;
                    if (n.Type == NoteType.HoldStart) ActivateHold(n);
                    _laneFlashTimes[n.Lane] = CurrentTimeSeconds;
                }

                RegisterJudgment(worstJudgment);
                _pendingChords.Remove(pendingChord);
            }

            return;
        }

        // --- Find the best candidate on this lane ---
        var candidate = _notes
            .Where(n => !n.Consumed && n.Lane == lane &&
                        (n.Type == NoteType.Tap || n.Type == NoteType.HoldStart))
            .OrderBy(n => Math.Abs(n.TimeSeconds - CurrentTimeSeconds))
            .FirstOrDefault();

        if (candidate is null) return;

        var delta = CurrentTimeSeconds - candidate.TimeSeconds;
        var judgment = PhoenixScoring.GetJudgment(delta, this.JudgmentDifficulty);
        if (judgment == HitJudgment.Miss) return;

        // Hold-start notes pressed EARLY must not be consumed here.
        // Activating them immediately causes the hold body to snap from its
        // current scroll position up to the receptor — visually teleporting.
        // Instead, keep the button flagged as held (_lanePressed[lane] = true above)
        // so the Update() pre-press path catches the note naturally as it arrives
        // at the receptor within PreHoldAcceptanceSeconds.
        if (candidate.Type == NoteType.HoldStart && delta < 0)
        {
            System.Diagnostics.Debug.WriteLine($"  📌 Early press on hold note at {candidate.TimeSeconds:F3}s (delta: {delta:F3}s) - waiting for pre-press window");
            return;
        }

        // Check for chord siblings
        var chordMembers = _notes
            .Where(n => !n.Consumed &&
                        (n.Type == NoteType.Tap || n.Type == NoteType.HoldStart) &&
                        Math.Abs(n.TimeSeconds - candidate.TimeSeconds) <= ChordWindowSeconds)
            .ToList();

        if (chordMembers.Count == 1)
        {
            // Solo note — consume and judge immediately
            candidate.Consumed = true;
            if (candidate.Type == NoteType.HoldStart)
            {
                System.Diagnostics.Debug.WriteLine($"  🎯 Activating hold via HandleLaneHit at {CurrentTimeSeconds:F3}s");
                ActivateHold(candidate);
            }
            RegisterJudgment(judgment);
        }
        else
        {
            // Multi-note chord — register the chord and wait for the remaining lanes.
            var chord = new PendingChord();
            chord.Notes.AddRange(chordMembers);
            chord.PressedLanes.Add(lane);
            _laneFlashTimes[lane] = CurrentTimeSeconds;
            _pendingChords.Add(chord);
        }
    }

    public void HandleLaneRelease(int lane)
    {
        System.Diagnostics.Debug.WriteLine($"🔴 BUTTON RELEASE: Lane {lane} at time {CurrentTimeSeconds:F3}s (was pressed: {_lanePressed[lane]}, hold active: {_laneHoldActive[lane]})");
        _lanePressed[lane] = false;
    }

    public double GetLaneFlashAge(int lane) => CurrentTimeSeconds - _laneFlashTimes[lane];

    public bool IsLaneHoldActive(int lane)
        => lane >= 0 && lane < _laneHoldActive.Length && _laneHoldActive[lane];

    /// <summary>
    /// Returns the most recent <see cref="HitJudgment"/> registered on <paramref name="lane"/>,
    /// or <see cref="HitJudgment.Miss"/> if the flash window (250 ms) has already expired.
    /// This ensures the star-burst effect only appears on the frame window immediately
    /// after a real note hit — a second press on an empty lane reads Miss and shows nothing.
    /// </summary>
    public HitJudgment GetLaneLastJudgment(int lane)
    {
        if (lane < 0 || lane >= _laneLastJudgment.Length)
            return HitJudgment.Miss;

        // Expire the stored judgment once the drawable's burst window has passed.
        // 0.25 s matches DrawStarBurst's 250 ms fade duration.
        const double burstWindowSeconds = 0.25d;
        if (CurrentTimeSeconds - _laneFlashTimes[lane] > burstWindowSeconds)
            _laneLastJudgment[lane] = HitJudgment.Miss;

        return _laneLastJudgment[lane];
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Activates a hold-start note and its partner.</summary>
    private void ActivateHold(PlayableNote note)
    {
        note.Consumed = true;
        note.IsHoldActive = true;
        _laneHoldActive[note.Lane] = true;
        if (note.HoldPartner != null) note.HoldPartner.IsHoldActive = true;
    }

    // -------------------------------------------------------------------------
    // Scoring
    // -------------------------------------------------------------------------

    private void RegisterJudgment(HitJudgment judgment)
    {
        _counts[judgment]++;

        if (judgment == HitJudgment.Miss)
        {
            Combo = 0;
            MissCombo++;
        }
        else
        {
            MissCombo = 0;
            if (!PhoenixScoring.BreaksCombo(judgment))
            {
                Combo++;
                MaxCombo = Math.Max(MaxCombo, Combo);
            }
            else
            {
                Combo = 0;
            }
        }

        // Record judgment on every lane that flashed within the last frame so the
        // drawable can render per-lane effects (e.g. star burst for Perfect/Great).
        for (var i = 0; i < _laneFlashTimes.Length; i++)
        {
            if (CurrentTimeSeconds - _laneFlashTimes[i] < 0.05d)
                _laneLastJudgment[i] = judgment;
        }

        Score = PhoenixScoring.CalculateScore(_counts, TotalNoteCount, MaxCombo);
        Grade = PhoenixScoring.CalculateGrade(Score);
        Plate = PhoenixScoring.CalculatePlate(_counts, TotalNoteCount);
        LastJudgmentText = judgment.ToString().ToUpperInvariant();
    }

    // -------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------

    private void ResetSession()
    {
        foreach (var judgment in _counts.Keys.ToList())
            _counts[judgment] = 0;

        foreach (var note in _notes)
        {
            note.Consumed = false;
            note.Missed = false;
            note.IsHoldActive = false;
        }

        foreach (var tick in _holdTicks)
            tick.Scored = false;

        _pendingChords.Clear();

        for (var lane = 0; lane < _laneFlashTimes.Length; lane++)
        {
            _laneFlashTimes[lane] = -10d;
            _laneHoldActive[lane] = false;
            _lanePressed[lane] = false;
            _laneLastJudgment[lane] = HitJudgment.Miss;
        }

        CurrentTimeSeconds = 0d;
        Combo = 0;
        MaxCombo = 0;
        MissCombo = 0;
        Score = 0;
        Grade = "D";
        Plate = "";
        LastJudgmentText = Chart is null ? "READY" : "SELECT SONG";
        IsPlaying = false;
    }
}
