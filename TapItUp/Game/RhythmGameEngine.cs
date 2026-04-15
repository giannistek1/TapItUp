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

    // 10 lanes to support pump-double (lanes 0-4 = left pad, lanes 5-9 = right pad)
    private readonly double[] _laneFlashTimes = [-10d, -10d, -10d, -10d, -10d, -10d, -10d, -10d, -10d, -10d];
    private readonly bool[] _laneHoldActive = [false, false, false, false, false, false, false, false, false, false];
    private readonly bool[] _lanePressed = [false, false, false, false, false, false, false, false, false, false];

    // Tracks the most recent judgment on each lane for per-lane visual feedback.
    private readonly HitJudgment[] _laneLastJudgment =
    [
        HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss,
        HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss, HitJudgment.Miss
    ];

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
    public string LastJudgmentText { get; private set; } = "READY";
    /// <summary>
    /// Incremented on every call to <see cref="RegisterJudgment"/>.
    /// The UI compares this to detect new hits even when the judgment text is identical.
    /// </summary>
    public int JudgmentSequence { get; private set; }

    public bool FullCombo => _counts[HitJudgment.Bad] == 0 && _counts[HitJudgment.Miss] == 0;

    /// <summary>
    /// True when the loaded chart is a pump-double or dance-double chart (10 lanes).
    /// </summary>
    public bool IsDoubleChart { get; private set; }

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

    // ── Live BPM ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The BPM active at <see cref="CurrentTimeSeconds"/>.
    /// Updated every <see cref="Update"/> call; used by the UI for beat-pulse.
    /// </summary>
    public double CurrentBpm { get; private set; } = 120d;

    // -------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------

    public void Load(SscSong song, SscChart chart)
    {
        Song = song;
        Chart = chart;
        IsDoubleChart =
            chart.StepType.Equals("pump-double", StringComparison.OrdinalIgnoreCase) ||
            chart.StepType.Equals("dance-double", StringComparison.OrdinalIgnoreCase);

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
        // Build pending set without SelectMany/ToHashSet allocation
        // by checking inline during the loop below.

        for (var ni = 0; ni < _notes.Count; ni++)
        {
            var note = _notes[ni];
            if (note.Consumed || note.Missed) continue;

            // Skip notes already tracked in a pending chord
            var inPendingChord = false;
            for (var ci = 0; ci < _pendingChords.Count; ci++)
            {
                var chord = _pendingChords[ci];
                for (var cni = 0; cni < chord.Notes.Count; cni++)
                {
                    if (chord.Notes[cni] == note) { inPendingChord = true; break; }
                }
                if (inPendingChord) break;
            }
            if (inPendingChord) continue;

            var delta = elapsedSeconds - note.TimeSeconds;

            // Pre-press: button already held when a hold head arrives
            if (note.Type == NoteType.HoldStart && !_laneHoldActive[note.Lane] && _lanePressed[note.Lane])
            {
                if (delta >= -PreHoldAcceptanceSeconds && delta <= badWindow)
                {
                    // Check for chord siblings (hold-starts near the same time)
                    var chordCount = 0;
                    for (var si = 0; si < _notes.Count; si++)
                    {
                        var sn = _notes[si];
                        if (!sn.Consumed && !sn.Missed &&
                            sn.Type == NoteType.HoldStart &&
                            Math.Abs(sn.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                            chordCount++;
                    }

                    if (chordCount == 1)
                    {
                        ActivateHold(note);
                        RegisterJudgment(HitJudgment.Perfect);
                    }
                    else
                    {
                        // Only activate if all chord lanes are pressed
                        var allPressed = true;
                        for (var si = 0; si < _notes.Count; si++)
                        {
                            var sn = _notes[si];
                            if (!sn.Consumed && !sn.Missed &&
                                sn.Type == NoteType.HoldStart &&
                                Math.Abs(sn.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds &&
                                !_lanePressed[sn.Lane])
                            {
                                allPressed = false;
                                break;
                            }
                        }

                        if (allPressed)
                        {
                            for (var si = 0; si < _notes.Count; si++)
                            {
                                var sn = _notes[si];
                                if (!sn.Consumed && !sn.Missed &&
                                    sn.Type == NoteType.HoldStart &&
                                    Math.Abs(sn.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                                    ActivateHold(sn);
                            }
                            RegisterJudgment(HitJudgment.Perfect);
                        }
                    }

                    continue;
                }
            }

            // Handle hold-end notes for active holds
            if (note.Type == NoteType.HoldEnd)
            {
                if (_laneHoldActive[note.Lane])
                {
                    if (delta >= 0)
                    {
                        note.Consumed = true;
                        _laneHoldActive[note.Lane] = false;
                        if (note.HoldPartner != null) note.HoldPartner.IsHoldActive = false;

                        var judgment = _lanePressed[note.Lane] ? HitJudgment.Perfect : HitJudgment.Miss;
                        RegisterJudgment(judgment);
                    }
                }
                else
                {
                    if (delta > badWindow)
                    {
                        note.Consumed = true;
                        note.Missed = true;
                    }
                }

                continue;
            }

            // Auto-miss: tap/hold-start note has passed the bad window
            if (delta > badWindow && (note.Type == NoteType.Tap || note.Type == NoteType.HoldStart))
            {
                // Count chord members first to register only one miss per chord
                var firstInChord = true;
                for (var si = 0; si < ni; si++)
                {
                    var sn = _notes[si];
                    if ((sn.Consumed || sn.Missed) &&
                        (sn.Type == NoteType.Tap || sn.Type == NoteType.HoldStart) &&
                        Math.Abs(sn.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                    {
                        firstInChord = false;
                        break;
                    }
                }

                note.Consumed = true;
                note.Missed = true;

                if (note.Type == NoteType.HoldStart)
                    _laneHoldActive[note.Lane] = false;

                // Mark all chord siblings in the same pass
                for (var si = ni + 1; si < _notes.Count; si++)
                {
                    var sn = _notes[si];
                    if (!sn.Consumed && !sn.Missed &&
                        (sn.Type == NoteType.Tap || sn.Type == NoteType.HoldStart) &&
                        Math.Abs(sn.TimeSeconds - note.TimeSeconds) <= ChordWindowSeconds)
                    {
                        sn.Consumed = true;
                        sn.Missed = true;
                        if (sn.Type == NoteType.HoldStart)
                            _laneHoldActive[sn.Lane] = false;
                    }
                }

                if (firstInChord)
                    RegisterJudgment(HitJudgment.Miss);
            }
        }

        // --- Hold tick evaluation ---
        for (var ti = 0; ti < _holdTicks.Count; ti++)
        {
            var tick = _holdTicks[ti];
            if (tick.Scored) continue;

            var delta = elapsedSeconds - tick.TimeSeconds;
            if (delta < -TickWindowSeconds) break;

            tick.Scored = true;
            var isHoldingCorrectly = _laneHoldActive[tick.Lane] && _lanePressed[tick.Lane];
            RegisterJudgment(isHoldingCorrectly ? HitJudgment.Perfect : HitJudgment.Miss);
        }

        // Check if game should end — avoid LINQ on every frame
        if (elapsedSeconds >= SongDurationSeconds)
        {
            var allConsumed = true;
            for (var ni = 0; ni < _notes.Count; ni++)
            {
                var n = _notes[ni];
                if (n.Type != NoteType.HoldBody && !n.Consumed)
                {
                    allConsumed = false;
                    break;
                }
            }

            if (allConsumed)
            {
                IsPlaying = false;
                LastJudgmentText = $"FINAL {Grade}";
            }
        }

        CurrentBpm = Song is null ? 120d : GetBpmAt(
            SecondsToBeatApprox(elapsedSeconds, Song.BpmChanges),
            Song.BpmChanges);
    }

    public void HandleLaneHit(int lane)
    {
        _laneFlashTimes[lane] = CurrentTimeSeconds;
        _lanePressed[lane] = true;

        if (!IsPlaying || Chart is null) return;

        if (_laneHoldActive[lane])
            return;

        // --- Check if this press contributes to a pending chord ---
        PendingChord? pendingChord = null;
        for (var ci = 0; ci < _pendingChords.Count; ci++)
        {
            var c = _pendingChords[ci];
            if (c.PressedLanes.Contains(lane)) continue;
            var hasLane = false;
            for (var ni = 0; ni < c.Notes.Count; ni++)
            {
                if (c.Notes[ni].Lane == lane) { hasLane = true; break; }
            }
            if (hasLane) { pendingChord = c; break; }
        }

        if (pendingChord != null)
        {
            pendingChord.PressedLanes.Add(lane);
            _laneFlashTimes[lane] = CurrentTimeSeconds;

            if (pendingChord.IsComplete)
            {
                var worstJudgment = HitJudgment.Perfect;
                for (var ni = 0; ni < pendingChord.Notes.Count; ni++)
                {
                    var j = PhoenixScoring.GetJudgment(
                        CurrentTimeSeconds - pendingChord.Notes[ni].TimeSeconds,
                        JudgmentDifficulty);
                    if (j > worstJudgment) worstJudgment = j;
                }

                for (var ni = 0; ni < pendingChord.Notes.Count; ni++)
                {
                    var n = pendingChord.Notes[ni];
                    n.Consumed = true;
                    if (n.Type == NoteType.HoldStart) ActivateHold(n);
                    _laneFlashTimes[n.Lane] = CurrentTimeSeconds;
                }

                RegisterJudgment(worstJudgment);
                _pendingChords.Remove(pendingChord);
            }

            return;
        }

        // --- Find the best candidate on this lane (no LINQ) ---
        PlayableNote? candidate = null;
        var bestDelta = double.MaxValue;
        for (var ni = 0; ni < _notes.Count; ni++)
        {
            var n = _notes[ni];
            if (n.Consumed || n.Lane != lane ||
                (n.Type != NoteType.Tap && n.Type != NoteType.HoldStart))
                continue;
            var absDelta = Math.Abs(n.TimeSeconds - CurrentTimeSeconds);
            if (absDelta < bestDelta) { bestDelta = absDelta; candidate = n; }
        }

        if (candidate is null) return;

        var delta = CurrentTimeSeconds - candidate.TimeSeconds;
        var judgment = PhoenixScoring.GetJudgment(delta, JudgmentDifficulty);
        if (judgment == HitJudgment.Miss) return;

        if (candidate.Type == NoteType.HoldStart && delta < 0)
            return;

        // Check for chord siblings (no LINQ, no ToList allocation)
        var chordMemberCount = 0;
        for (var ni = 0; ni < _notes.Count; ni++)
        {
            var n = _notes[ni];
            if (!n.Consumed &&
                (n.Type == NoteType.Tap || n.Type == NoteType.HoldStart) &&
                Math.Abs(n.TimeSeconds - candidate.TimeSeconds) <= ChordWindowSeconds)
                chordMemberCount++;
        }

        if (chordMemberCount == 1)
        {
            candidate.Consumed = true;
            if (candidate.Type == NoteType.HoldStart)
                ActivateHold(candidate);
            RegisterJudgment(judgment);
        }
        else
        {
            var chord = new PendingChord();
            for (var ni = 0; ni < _notes.Count; ni++)
            {
                var n = _notes[ni];
                if (!n.Consumed &&
                    (n.Type == NoteType.Tap || n.Type == NoteType.HoldStart) &&
                    Math.Abs(n.TimeSeconds - candidate.TimeSeconds) <= ChordWindowSeconds)
                    chord.Notes.Add(n);
            }
            chord.PressedLanes.Add(lane);
            _laneFlashTimes[lane] = CurrentTimeSeconds;
            _pendingChords.Add(chord);
        }
    }

    public void HandleLaneRelease(int lane)
    {
        _lanePressed[lane] = false;
    }

    public double GetLaneFlashAge(int lane) => CurrentTimeSeconds - _laneFlashTimes[lane];

    public bool IsLaneHoldActive(int lane)
        => lane >= 0 && lane < _laneHoldActive.Length && _laneHoldActive[lane];

    /// <summary>
    /// Returns the most recent <see cref="HitJudgment"/> registered on <paramref name="lane"/>,

    /// or <see cref="HitJudgment.Miss"/> if the flash window (250 ms) has already expired.
    /// </summary>
    public HitJudgment GetLaneLastJudgment(int lane)
    {
        if (lane < 0 || lane >= _laneLastJudgment.Length)
            return HitJudgment.Miss;

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

    // Running weighted sum for incremental score calculation — avoids per-hit dictionary enumeration
    private double _weightedSum;
    private bool _scoreDirty = false;

    /// <summary>
    /// Final score (0–1,000,000). Computed lazily — only recalculated when
    /// accessed after a judgment has been registered since the last read.
    /// Safe to read every frame from the results screen; zero-cost during gameplay.
    /// </summary>
    public int Score
    {
        get
        {
            if (_scoreDirty)
            {
                _cachedScore = PhoenixScoring.CalculateScoreIncremental(_weightedSum, TotalNoteCount, MaxCombo);
                _cachedGrade = PhoenixScoring.CalculateGrade(_cachedScore);
                _cachedPlate = PhoenixScoring.CalculatePlate(_counts, TotalNoteCount);
                _scoreDirty = false;
            }
            return _cachedScore;
        }
        private set => _cachedScore = value;
    }

    private int _cachedScore;

    public string Grade
    {
        get { _ = Score; return _cachedGrade; }
        private set => _cachedGrade = value;
    }

    private string _cachedGrade = "D";

    public string Plate
    {
        get { _ = Score; return _cachedPlate; }
        private set => _cachedPlate = value;
    }

    private string _cachedPlate = "";

    private void RegisterJudgment(HitJudgment judgment)
    {
        _counts[judgment]++;
        JudgmentSequence++;
        _weightedSum += PhoenixScoring.GetWeight(judgment);

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

        for (var i = 0; i < _laneFlashTimes.Length; i++)
        {
            if (CurrentTimeSeconds - _laneFlashTimes[i] < 0.05d)
                _laneLastJudgment[i] = judgment;
        }

        _scoreDirty = true;
        LastJudgmentText = PhoenixScoring.GetJudgmentText(judgment);
    }

    // -------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------

    private void ResetSession()
    {
        foreach (var judgment in _counts.Keys.ToList())
            _counts[judgment] = 0;

        _weightedSum = 0d;
        _scoreDirty = false;
        _cachedScore = 0;
        _cachedGrade = "D";
        _cachedPlate = "";

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
        JudgmentSequence = 0;
        IsPlaying = false;
        LastJudgmentText = Chart is null ? "READY" : "SELECT SONG";
    }

    private static double SecondsToBeatApprox(double seconds, IReadOnlyList<BpmChange> bpmChanges)
    {
        if (seconds <= 0d || bpmChanges.Count == 0) return 0d;

        var beat = 0d;
        var elapsed = 0d;
        var currentBpm = bpmChanges[0].Bpm;
        var lastBeat = 0d;

        foreach (var change in bpmChanges.OrderBy(c => c.Beat))
        {
            var segmentBeats = change.Beat - lastBeat;
            var segmentSeconds = segmentBeats / currentBpm * 60d;
            if (elapsed + segmentSeconds >= seconds) break;

            elapsed += segmentSeconds;
            beat = change.Beat;
            lastBeat = change.Beat;
            currentBpm = change.Bpm;
        }

        beat += (seconds - elapsed) / 60d * currentBpm;
        return beat;
    }
}
