namespace TapItUp.Game;

/// <summary>
/// Represents a group of notes that fall within <see cref="RhythmGameEngine.ChordWindowSeconds"/>
/// of each other and must all be pressed to count as a single hit.
/// </summary>
internal sealed class PendingChord
{
    public List<PlayableNote> Notes { get; } = [];
    public HashSet<int> PressedLanes { get; } = [];

    /// <summary>The earliest note time in the chord — used as the timing reference.</summary>
    public double ReferenceTimeSeconds => Notes.Min(n => n.TimeSeconds);

    /// <summary>True when every lane in the chord has been pressed.</summary>
    public bool IsComplete => Notes.All(n => PressedLanes.Contains(n.Lane));

    /// <summary>True when <paramref name="currentTime"/> has passed the bad window.</summary>
    public bool IsExpired(double currentTime, double badWindow)
        => currentTime - ReferenceTimeSeconds > badWindow;
}

public sealed class RhythmGameEngine
{
    // ── Timing constants ──────────────────────────────────────────────────────

    /// <summary>Notes within this window are grouped into a single chord.</summary>
    private const double ChordWindowSeconds = 0.020d;

    /// <summary>How early a button can be pre-held before a HoldStart note arrives.</summary>
    private const double PreHoldAcceptanceSeconds = 0.05d;

    /// <summary>Scoring window for hold ticks.</summary>
    private const double TickWindowSeconds = 0.075d;

    /// <summary>Total lane count — 5 for single, 10 for double.</summary>
    private const int MaxLanes = 10;

    // ── Per-lane state ────────────────────────────────────────────────────────

    private readonly double[] _laneFlashTimes = Enumerable.Repeat(-10d, MaxLanes).ToArray();
    private readonly bool[] _laneHoldActive = new bool[MaxLanes];
    private readonly bool[] _lanePressed = new bool[MaxLanes];
    private readonly HitJudgment[] _laneLastJudgment = Enumerable.Repeat(HitJudgment.Miss, MaxLanes).ToArray();

    // Per-lane sorted note lists with head indices for O(1) candidate lookup.
    private readonly List<PlayableNote>[] _laneNotes = Enumerable
        .Range(0, MaxLanes)
        .Select(_ => new List<PlayableNote>(64))
        .ToArray();
    private readonly int[] _laneNoteIndex = new int[MaxLanes];

    // ── Note and tick state ───────────────────────────────────────────────────

    private List<PlayableNote> _notes = [];
    private List<HoldTick> _holdTicks = [];
    private int _globalNoteIndex;
    private int _chordGroupCount;

    // ── Chord tracking ────────────────────────────────────────────────────────

    private readonly List<PendingChord> _pendingChords = [];
    private readonly HashSet<PlayableNote> _pendingChordNoteSet = [];

    // ── Scoring ───────────────────────────────────────────────────────────────

    private readonly Dictionary<HitJudgment, int> _counts = Enum
        .GetValues<HitJudgment>()
        .ToDictionary(j => j, _ => 0);

    private double _weightedSum;
    private bool _scoreDirty;
    private int _cachedScore;
    private string _cachedGrade = "D";
    private string _cachedPlate = string.Empty;

    // ── BPM ───────────────────────────────────────────────────────────────────

    private List<BpmChange> _sortedBpmChanges = [];

    // ── Public properties ─────────────────────────────────────────────────────

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
    public bool IsDoubleChart { get; private set; }

    public JudgmentDifficulty JudgmentDifficulty { get; set; } = JudgmentDifficulty.Standard;

    /// <summary>Monotonically incremented on every judgment so the UI can distinguish consecutive identical hits.</summary>
    public int JudgmentSequence { get; private set; }
    public string LastJudgmentText { get; private set; } = "READY";

    /// <summary>The BPM active at <see cref="CurrentTimeSeconds"/>. Updated every <see cref="Update"/> call.</summary>
    public double CurrentBpm { get; private set; } = 120d;

    public bool FullCombo => _counts[HitJudgment.Bad] == 0 && _counts[HitJudgment.Miss] == 0;
    public int TotalNoteCount => _chordGroupCount + _holdTicks.Count;
    public double SongDurationSeconds => (Chart?.LastNoteTimeSeconds ?? 0d) + 2.5d;

    public double AccuracyPercent =>
        PhoenixScoring.MaxScore == 0 ? 0d : Score / (double)PhoenixScoring.MaxScore * 100d;

    /// <summary>
    /// Score from 0–1,000,000. Lazily recalculated only when a judgment has been
    /// registered since the last read — safe to poll every frame.
    /// </summary>
    public int Score
    {
        get
        {
            if (!_scoreDirty) return _cachedScore;

            _cachedScore = PhoenixScoring.CalculateScoreIncremental(_weightedSum, TotalNoteCount, MaxCombo);
            _cachedGrade = PhoenixScoring.CalculateGrade(_cachedScore);
            _cachedPlate = PhoenixScoring.CalculatePlate(_counts, TotalNoteCount);
            _scoreDirty = false;
            return _cachedScore;
        }
    }

    // Accessing Score populates the cached grade/plate — intentional.
    public string Grade { get { _ = Score; return _cachedGrade; } }
    public string Plate { get { _ = Score; return _cachedPlate; } }

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

        _sortedBpmChanges = [.. song.BpmChanges.OrderBy(c => c.Beat)];

        _notes = [.. chart.Notes
            .Select(n => new PlayableNote
            {
                Lane        = n.Lane,
                Beat        = n.Beat,
                TimeSeconds = n.TimeSeconds,
                Type        = n.Type
            })
            .OrderBy(n => n.TimeSeconds)];

        LinkHoldNotes();
        GenerateHoldTicks(song.TickCounts);
        ComputeChordGroupCount();
        BuildLaneIndex();
        ResetSession();
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
        Array.Clear(_laneHoldActive, 0, MaxLanes);
        Array.Clear(_lanePressed, 0, MaxLanes);
    }

    // -------------------------------------------------------------------------
    // Update loop
    // -------------------------------------------------------------------------

    public void Update(double elapsedSeconds)
    {
        CurrentTimeSeconds = elapsedSeconds;
        if (!IsPlaying || Chart is null) return;

        var badWindow = PhoenixScoring.GetBadWindow(JudgmentDifficulty);

        ExpireOldChords(elapsedSeconds, badWindow);
        ProcessNotes(elapsedSeconds, badWindow);
        ProcessHoldTicks(elapsedSeconds);
        CheckSongEnd(elapsedSeconds);

        CurrentBpm = _sortedBpmChanges.Count == 0
            ? 120d
            : GetBpmAt(SecondsToBeatApprox(elapsedSeconds, _sortedBpmChanges), _sortedBpmChanges);
    }

    private void ExpireOldChords(double elapsedSeconds, double badWindow)
    {
        for (var ci = _pendingChords.Count - 1; ci >= 0; ci--)
        {
            var chord = _pendingChords[ci];
            if (!chord.IsExpired(elapsedSeconds, badWindow)) continue;

            foreach (var n in chord.Notes)
            {
                if (n.Consumed) continue;
                n.Consumed = true;
                n.Missed = true;
                if (n.Type == NoteType.HoldStart) _laneHoldActive[n.Lane] = false;
            }

            RegisterJudgment(HitJudgment.Miss);
            RemovePendingChord(chord);
        }
    }

    /// <summary>
    /// Auto-activates a HoldStart note when the lane is already held down and the note
    /// falls within the pre-hold acceptance window.
    /// </summary>
    private bool TryAutoActivatePreHeldHold(PlayableNote note, double delta, int noteIndex, double badWindow)
    {
        if (note.Type != NoteType.HoldStart) return false;
        if (_laneHoldActive[note.Lane]) return false;
        if (!_lanePressed[note.Lane]) return false;
        if (delta < -PreHoldAcceptanceSeconds) return false;
        if (delta > badWindow) return false;

        var chordEnd = GetChordWindowEndIndex(noteIndex, note.TimeSeconds);
        var holdCount = CountActiveHoldStarts(noteIndex, chordEnd);

        if (holdCount == 1)
        {
            ActivateHold(note);
            RegisterJudgment(HitJudgment.Perfect);
            return true;
        }

        if (AllHoldsPressedInRange(noteIndex, chordEnd))
        {
            ActivateHoldsInRange(noteIndex, chordEnd);
            RegisterJudgment(HitJudgment.Perfect);
            return true;
        }

        return false;
    }

    private void ProcessNotes(double elapsedSeconds, double badWindow)
    {
        AdvanceGlobalNoteIndex();

        for (var ni = _globalNoteIndex; ni < _notes.Count; ni++)
        {
            var note = _notes[ni];

            if (note.Type == NoteType.HoldBody || note.Consumed || note.Missed) continue;
            if (note.TimeSeconds - elapsedSeconds > badWindow) break;
            if (_pendingChordNoteSet.Contains(note)) continue;

            var delta = elapsedSeconds - note.TimeSeconds;

            if (TryAutoActivatePreHeldHold(note, delta, ni, badWindow)) continue;  // pass badWindow
            if (TryProcessHoldEnd(note, delta, badWindow)) continue;

            if (delta > badWindow && note.Type is NoteType.Tap or NoteType.HoldStart)
                MissChordGroup(ni);
        }

        AdvanceGlobalNoteIndex();
    }

    /// <summary>
    /// Scores or dismisses a HoldEnd note. Returns true when the note was handled.
    /// </summary>
    private bool TryProcessHoldEnd(PlayableNote note, double delta, double badWindow)
    {
        if (note.Type != NoteType.HoldEnd) return false;

        if (_laneHoldActive[note.Lane])
        {
            if (delta >= 0d)
            {
                note.Consumed = true;
                _laneHoldActive[note.Lane] = false;
                if (note.HoldPartner != null)
                    note.HoldPartner.IsHoldActive = false;

                RegisterJudgment(_lanePressed[note.Lane] ? HitJudgment.Perfect : HitJudgment.Miss);
            }
        }
        else if (delta > badWindow)
        {
            note.Consumed = true;
            note.Missed = true;
        }

        return true;
    }

    /// <summary>Marks every note in a chord group as missed and registers one Miss judgment.</summary>
    private void MissChordGroup(int startIndex)
    {
        var end = GetChordWindowEndIndex(startIndex, _notes[startIndex].TimeSeconds);
        var missedAny = false;

        for (var si = startIndex; si < end; si++)
        {
            var sn = _notes[si];
            if (sn.Consumed || sn.Missed || sn.Type is not (NoteType.Tap or NoteType.HoldStart)) continue;

            sn.Consumed = true;
            sn.Missed = true;
            if (sn.Type == NoteType.HoldStart) _laneHoldActive[sn.Lane] = false;

            missedAny = true;
        }

        if (missedAny) RegisterJudgment(HitJudgment.Miss);
    }

    private void ProcessHoldTicks(double elapsedSeconds)
    {
        foreach (var tick in _holdTicks)
        {
            if (tick.Scored) continue;

            var delta = elapsedSeconds - tick.TimeSeconds;
            if (delta < -TickWindowSeconds) break;

            tick.Scored = true;
            RegisterJudgment(_laneHoldActive[tick.Lane] && _lanePressed[tick.Lane]
                ? HitJudgment.Perfect
                : HitJudgment.Miss);
        }
    }

    private void CheckSongEnd(double elapsedSeconds)
    {
        if (elapsedSeconds < SongDurationSeconds) return;
        if (_notes.Any(n => n.Type != NoteType.HoldBody && !n.Consumed)) return;

        IsPlaying = false;
        LastJudgmentText = $"FINAL {Grade}";
    }

    // -------------------------------------------------------------------------
    // Input handling
    // -------------------------------------------------------------------------

    public void HandleLaneHit(int lane)
    {
        _laneFlashTimes[lane] = CurrentTimeSeconds;
        _lanePressed[lane] = true;

        if (!IsPlaying || Chart is null || _laneHoldActive[lane]) return;

        if (TryContributeToPendingChord(lane)) return;

        var candidate = FindBestCandidate(lane);
        if (candidate is null) return;

        var delta = CurrentTimeSeconds - candidate.TimeSeconds;
        var judgment = PhoenixScoring.GetJudgment(delta, JudgmentDifficulty);

        if (judgment == HitJudgment.Miss) return;
        if (candidate.Type == NoteType.HoldStart && delta < 0d) return;

        if (CountChordMembers(candidate) == 1)
            HitSingleNote(candidate, judgment);
        else
            StartPendingChord(candidate, lane);
    }

    public void HandleLaneRelease(int lane) => _lanePressed[lane] = false;

    private bool TryContributeToPendingChord(int lane)
    {
        PendingChord? chord = null;
        for (var ci = 0; ci < _pendingChords.Count; ci++)
        {
            var c = _pendingChords[ci];
            if (c.PressedLanes.Contains(lane)) continue;

            for (var ni = 0; ni < c.Notes.Count; ni++)
            {
                if (c.Notes[ni].Lane != lane) continue;
                chord = c;
                break;
            }

            if (chord is not null) break;
        }

        if (chord is null) return false;

        chord.PressedLanes.Add(lane);
        _laneFlashTimes[lane] = CurrentTimeSeconds;

        if (!chord.IsComplete) return true;

        var worst = HitJudgment.Perfect;
        for (var ni = 0; ni < chord.Notes.Count; ni++)
        {
            var j = PhoenixScoring.GetJudgment(CurrentTimeSeconds - chord.Notes[ni].TimeSeconds, JudgmentDifficulty);
            if (j > worst) worst = j;
        }

        foreach (var n in chord.Notes)
        {
            n.Consumed = true;
            if (n.Type == NoteType.HoldStart) ActivateHold(n);
            _laneFlashTimes[n.Lane] = CurrentTimeSeconds;
        }

        RegisterJudgment(worst);
        RemovePendingChord(chord);
        return true;
    }

    /// <summary>Finds the closest unhit note in the given lane within the bad window.</summary>
    private PlayableNote? FindBestCandidate(int lane)
    {
        AdvanceLaneIndex(lane);

        var badWindow = PhoenixScoring.GetBadWindow(JudgmentDifficulty);
        var list = _laneNotes[lane];
        var startIdx = _laneNoteIndex[lane];
        PlayableNote? best = null;
        var bestDelta = double.MaxValue;

        for (var ni = startIdx; ni < list.Count; ni++)
        {
            var n = list[ni];
            if (n.Consumed || n.Missed) continue;
            if (n.TimeSeconds - CurrentTimeSeconds > badWindow) break;

            var absDelta = Math.Abs(n.TimeSeconds - CurrentTimeSeconds);
            if (absDelta < bestDelta) { bestDelta = absDelta; best = n; }
        }

        return best;
    }

    private void HitSingleNote(PlayableNote note, HitJudgment judgment)
    {
        note.Consumed = true;
        if (note.Type == NoteType.HoldStart) ActivateHold(note);
        RegisterJudgment(judgment);
    }

    private void StartPendingChord(PlayableNote reference, int triggerLane)
    {
        var chord = new PendingChord();
        var laneCount = IsDoubleChart ? 10 : 5;

        for (var l = 0; l < laneCount; l++)
        {
            AdvanceLaneIndex(l);
            var list = _laneNotes[l];
            var idx = _laneNoteIndex[l];

            for (var ni = idx; ni < list.Count; ni++)
            {
                var n = list[ni];
                if (n.Consumed || n.Missed) continue;
                if (n.TimeSeconds - reference.TimeSeconds > ChordWindowSeconds) break;
                chord.Notes.Add(n);
            }
        }

        chord.PressedLanes.Add(triggerLane);
        _laneFlashTimes[triggerLane] = CurrentTimeSeconds;
        _pendingChords.Add(chord);
        foreach (var n in chord.Notes) _pendingChordNoteSet.Add(n);
    }

    // -------------------------------------------------------------------------
    // Public accessors
    // -------------------------------------------------------------------------

    public double GetLaneFlashAge(int lane) => CurrentTimeSeconds - _laneFlashTimes[lane];
    public bool IsLaneHoldActive(int lane) => (uint)lane < MaxLanes && _laneHoldActive[lane];

    /// <summary>
    /// Returns the last judgment on a lane, or <see cref="HitJudgment.Miss"/>
    /// if the 250 ms burst window has elapsed.
    /// </summary>
    public HitJudgment GetLaneLastJudgment(int lane)
    {
        if ((uint)lane >= MaxLanes) return HitJudgment.Miss;

        if (CurrentTimeSeconds - _laneFlashTimes[lane] > 0.25d)
            _laneLastJudgment[lane] = HitJudgment.Miss;

        return _laneLastJudgment[lane];
    }

    // -------------------------------------------------------------------------
    // Scoring
    // -------------------------------------------------------------------------

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
            Combo = PhoenixScoring.BreaksCombo(judgment) ? 0 : Combo + 1;
            MaxCombo = Math.Max(MaxCombo, Combo);
        }

        for (var i = 0; i < MaxLanes; i++)
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
        foreach (var key in _counts.Keys.ToList()) _counts[key] = 0;

        _weightedSum = 0d;
        _scoreDirty = false;
        _cachedScore = 0;
        _cachedGrade = "D";
        _cachedPlate = string.Empty;

        foreach (var note in _notes)
        {
            note.Consumed = false;
            note.Missed = false;
            note.IsHoldActive = false;
        }

        foreach (var tick in _holdTicks) tick.Scored = false;

        _pendingChords.Clear();
        _pendingChordNoteSet.Clear();
        _globalNoteIndex = 0;

        for (var i = 0; i < MaxLanes; i++)
        {
            _laneFlashTimes[i] = -10d;
            _laneHoldActive[i] = false;
            _lanePressed[i] = false;
            _laneLastJudgment[i] = HitJudgment.Miss;
            _laneNoteIndex[i] = 0;
        }

        CurrentTimeSeconds = 0d;
        Combo = 0;
        MaxCombo = 0;
        MissCombo = 0;
        JudgmentSequence = 0;
        IsPlaying = false;
        LastJudgmentText = Chart is null ? "READY" : "SELECT SONG";
    }

    // -------------------------------------------------------------------------
    // Load-time setup
    // -------------------------------------------------------------------------

    private void BuildLaneIndex()
    {
        for (var i = 0; i < MaxLanes; i++) _laneNotes[i].Clear();

        foreach (var n in _notes.Where(n => n.Type is NoteType.Tap or NoteType.HoldStart))
            _laneNotes[n.Lane].Add(n);

        for (var i = 0; i < MaxLanes; i++)
            _laneNotes[i].Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    /// <summary>Counts distinct chord groups for the scoring denominator.</summary>
    private void ComputeChordGroupCount()
    {
        var scoreable = _notes
            .Where(n => n.Type is NoteType.Tap or NoteType.HoldStart)
            .OrderBy(n => n.TimeSeconds)
            .ToList();

        _chordGroupCount = 0;
        var i = 0;
        while (i < scoreable.Count)
        {
            _chordGroupCount++;
            var groupTime = scoreable[i].TimeSeconds;
            while (i < scoreable.Count && scoreable[i].TimeSeconds - groupTime <= ChordWindowSeconds)
                i++;
        }
    }

    private void LinkHoldNotes()
    {
        foreach (var laneGroup in _notes.GroupBy(n => n.Lane))
        {
            var laneNotes = laneGroup.OrderBy(n => n.TimeSeconds).ToList();

            for (var i = 0; i < laneNotes.Count; i++)
            {
                if (laneNotes[i].Type != NoteType.HoldStart) continue;

                var end = laneNotes.Skip(i + 1).FirstOrDefault(n => n.Type == NoteType.HoldEnd);
                if (end is null) continue;

                laneNotes[i].HoldPartner = end;
                end.HoldPartner = laneNotes[i];
            }
        }
    }

    private void GenerateHoldTicks(IReadOnlyList<TickCount> tickCounts)
    {
        _holdTicks = [];
        if (_sortedBpmChanges.Count == 0) return;

        foreach (var head in _notes.Where(n => n.Type == NoteType.HoldStart && n.HoldPartner != null))
        {
            var tail = head.HoldPartner!;
            var currentBeat = head.Beat;
            var currentTime = head.TimeSeconds;

            while (currentTime < tail.TimeSeconds)
            {
                var ticksPerBeat = GetTicksPerBeat(currentBeat, tickCounts);
                var bpm = GetBpmAt(currentBeat, _sortedBpmChanges);

                if (ticksPerBeat <= 0 || bpm <= 0) break;

                var secondsPerTick = 60.0 / bpm / ticksPerBeat;
                if (secondsPerTick <= 0) break;

                var nextTime = currentTime + secondsPerTick;
                var nextBeat = currentBeat + 1.0 / ticksPerBeat;

                if (nextTime < tail.TimeSeconds - 0.001d)
                    _holdTicks.Add(new HoldTick { Lane = head.Lane, TimeSeconds = nextTime });

                currentTime = nextTime;
                currentBeat = nextBeat;
            }
        }

        _holdTicks.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    // -------------------------------------------------------------------------
    // Index helpers
    // -------------------------------------------------------------------------

    private void AdvanceLaneIndex(int lane)
    {
        var list = _laneNotes[lane];
        var idx = _laneNoteIndex[lane];
        while (idx < list.Count && (list[idx].Consumed || list[idx].Missed)) idx++;
        _laneNoteIndex[lane] = idx;
    }

    private void AdvanceGlobalNoteIndex()
    {
        while (_globalNoteIndex < _notes.Count)
        {
            var n = _notes[_globalNoteIndex];
            if (n.Type == NoteType.HoldBody || n.Consumed || n.Missed)
                _globalNoteIndex++;
            else
                break;
        }
    }

    private int GetChordWindowEndIndex(int startIndex, double referenceTime)
    {
        var end = startIndex;
        while (end < _notes.Count && _notes[end].TimeSeconds - referenceTime <= ChordWindowSeconds)
            end++;
        return end;
    }

    // -------------------------------------------------------------------------
    // Note range helpers
    // -------------------------------------------------------------------------

    private void ActivateHold(PlayableNote note)
    {
        note.Consumed = true;
        note.IsHoldActive = true;
        _laneHoldActive[note.Lane] = true;
        if (note.HoldPartner != null) note.HoldPartner.IsHoldActive = true;
    }

    private void RemovePendingChord(PendingChord chord)
    {
        foreach (var n in chord.Notes) _pendingChordNoteSet.Remove(n);
        _pendingChords.Remove(chord);
    }

    private int CountChordMembers(PlayableNote reference)
    {
        var laneCount = IsDoubleChart ? 10 : 5;
        var count = 0;

        for (var l = 0; l < laneCount; l++)
        {
            AdvanceLaneIndex(l);
            var list = _laneNotes[l];
            var idx = _laneNoteIndex[l];

            for (var ni = idx; ni < list.Count; ni++)
            {
                var n = list[ni];
                if (n.Consumed || n.Missed) continue;
                if (n.TimeSeconds - reference.TimeSeconds > ChordWindowSeconds) break;
                count++;
            }
        }

        return count;
    }

    private int CountActiveHoldStarts(int startIndex, int endIndex)
    {
        var count = 0;
        for (var i = startIndex; i < endIndex; i++)
        {
            var n = _notes[i];
            if (!n.Consumed && !n.Missed && n.Type == NoteType.HoldStart) count++;
        }
        return count;
    }

    private bool AllHoldsPressedInRange(int startIndex, int endIndex)
    {
        for (var i = startIndex; i < endIndex; i++)
        {
            var n = _notes[i];
            if (!n.Consumed && !n.Missed && n.Type == NoteType.HoldStart && !_lanePressed[n.Lane])
                return false;
        }
        return true;
    }

    private void ActivateHoldsInRange(int startIndex, int endIndex)
    {
        for (var i = startIndex; i < endIndex; i++)
        {
            var n = _notes[i];
            if (!n.Consumed && !n.Missed && n.Type == NoteType.HoldStart)
                ActivateHold(n);
        }
    }

    // -------------------------------------------------------------------------
    // BPM / timing utilities
    // -------------------------------------------------------------------------

    private static int GetTicksPerBeat(double beat, IReadOnlyList<TickCount> tickCounts)
    {
        var active = 4;
        foreach (var tc in tickCounts)
        {
            if (tc.Beat <= beat + 0.0001d) active = tc.TicksPerBeat;
            else break;
        }
        return active;
    }

    private static double GetBpmAt(double beat, IReadOnlyList<BpmChange> bpmChanges)
    {
        var bpm = bpmChanges[0].Bpm;
        foreach (var bc in bpmChanges)
        {
            if (bc.Beat <= beat + 0.0001d) bpm = bc.Bpm;
            else break;
        }
        return bpm;
    }

    private static double SecondsToBeatApprox(double seconds, IReadOnlyList<BpmChange> bpmChanges)
    {
        if (seconds <= 0d || bpmChanges.Count == 0) return 0d;

        var beat = 0d;
        var elapsed = 0d;
        var currentBpm = bpmChanges[0].Bpm;
        var lastBeat = 0d;

        foreach (var change in bpmChanges)
        {
            var segmentSeconds = (change.Beat - lastBeat) / currentBpm * 60d;
            if (elapsed + segmentSeconds >= seconds) break;

            elapsed += segmentSeconds;
            beat = change.Beat;
            lastBeat = change.Beat;
            currentBpm = change.Bpm;
        }

        return beat + (seconds - elapsed) / 60d * currentBpm;
    }
}