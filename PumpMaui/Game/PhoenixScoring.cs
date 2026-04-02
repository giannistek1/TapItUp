namespace PumpMaui.Game;

/// <summary>Selects between the two available timing-window presets.</summary>
public enum JudgmentDifficulty
{
    Standard,
    Hard
}

public static class PhoenixScoring
{
    // -------------------------------------------------------------------------
    // Timing windows
    //
    // delta = currentTime - noteTime
    //   negative  →  player pressed EARLY
    //   positive  →  player pressed LATE
    //
    // Standard:
    //   Early: Perfect ≤ 83.3 ms | Great 83.3–124.9 | Good 124.9–166.5 | Bad 166.5–208.1 | Miss > 208.1
    //   Late:  Perfect ≤ 124.9 ms | Great 124.9–166.5 | Good 166.5–208.1 | Bad 208.1–249.7 | Miss > 249.7
    //
    // Hard:
    //   Early: Perfect ≤ 25.0 ms | Great 25.0–66.6 | Good 66.6–108.2 | Bad 108.2–149.8 | Miss > 149.8
    //   Late:  Perfect ≤ 66.6 ms | Great 66.6–108.2 | Good 108.2–149.8 | Bad 149.8–191.4 | Miss > 191.4
    // -------------------------------------------------------------------------

    private readonly record struct TimingWindows(
        double EarlyPerfect,
        double EarlyGreat,
        double EarlyGood,
        double EarlyBad,
        double LatePerfect,
        double LateGreat,
        double LateGood,
        double LateBad);

    private static readonly TimingWindows StandardWindows = new(
        EarlyPerfect: 0.0833d,
        EarlyGreat: 0.1249d,
        EarlyGood: 0.1665d,
        EarlyBad: 0.2081d,
        LatePerfect: 0.1249d,
        LateGreat: 0.1665d,
        LateGood: 0.2081d,
        LateBad: 0.2497d);

    private static readonly TimingWindows HardWindows = new(
        EarlyPerfect: 0.0250d,
        EarlyGreat: 0.0666d,
        EarlyGood: 0.1082d,
        EarlyBad: 0.1498d,
        LatePerfect: 0.0666d,
        LateGreat: 0.1082d,
        LateGood: 0.1498d,
        LateBad: 0.1914d);

    // -------------------------------------------------------------------------
    // Score constants
    //   Accuracy pool : 995,000 pts (99.5 %)
    //   Combo pool    :   5,000 pts ( 0.5 %)
    // -------------------------------------------------------------------------

    public const int MaxScore = 1_000_000;
    public const int AccuracyMaxScore = 995_000;
    public const int ComboMaxScore = 5_000;

    // -------------------------------------------------------------------------
    // Judgment weights (accuracy pool)
    //   Perfect = 100 %, Great = 60 %, Good = 20 %, Bad = 10 %, Miss = 0 %
    // -------------------------------------------------------------------------

    public static double GetWeight(HitJudgment judgment) => judgment switch
    {
        HitJudgment.Perfect => 1.00d,
        HitJudgment.Great => 0.60d,
        HitJudgment.Good => 0.20d,
        HitJudgment.Bad => 0.10d,
        _ => 0.00d
    };

    // -------------------------------------------------------------------------
    // Outer late boundary — used by the engine's auto-miss pass.
    // Returns the LateBad value for the selected difficulty.
    // -------------------------------------------------------------------------

    public static double GetBadWindow(JudgmentDifficulty difficulty)
        => difficulty == JudgmentDifficulty.Hard
            ? HardWindows.LateBad
            : StandardWindows.LateBad;

    // -------------------------------------------------------------------------
    // Asymmetric judgment evaluation
    //   delta < 0  →  early press
    //   delta ≥ 0  →  late press (delta = 0 is a perfect)
    // -------------------------------------------------------------------------

    public static HitJudgment GetJudgment(double deltaSeconds,
        JudgmentDifficulty difficulty = JudgmentDifficulty.Standard)
    {
        var w = difficulty == JudgmentDifficulty.Hard ? HardWindows : StandardWindows;

        if (deltaSeconds <= 0)
        {
            var early = -deltaSeconds;
            if (early <= w.EarlyPerfect) return HitJudgment.Perfect;
            if (early <= w.EarlyGreat) return HitJudgment.Great;
            if (early <= w.EarlyGood) return HitJudgment.Good;
            if (early <= w.EarlyBad) return HitJudgment.Bad;
            return HitJudgment.Miss;
        }
        else
        {
            if (deltaSeconds <= w.LatePerfect) return HitJudgment.Perfect;
            if (deltaSeconds <= w.LateGreat) return HitJudgment.Great;
            if (deltaSeconds <= w.LateGood) return HitJudgment.Good;
            if (deltaSeconds <= w.LateBad) return HitJudgment.Bad;
            return HitJudgment.Miss;
        }
    }

    public static bool BreaksCombo(HitJudgment judgment)
        => judgment is HitJudgment.Bad or HitJudgment.Miss;

    // -------------------------------------------------------------------------
    // Score calculation
    // -------------------------------------------------------------------------

    public static int CalculateScore(
        IReadOnlyDictionary<HitJudgment, int> counts,
        int noteCount,
        int maxCombo)
    {
        if (noteCount <= 0) return 0;

        var weightedSum = counts.Sum(pair => pair.Value * GetWeight(pair.Key));
        var accuracyScore = weightedSum / noteCount * AccuracyMaxScore;
        var comboScore = maxCombo >= noteCount ? ComboMaxScore : 0;

        return Math.Min(MaxScore, (int)Math.Round(accuracyScore + comboScore, MidpointRounding.AwayFromZero));
    }

    // -------------------------------------------------------------------------
    // Grade thresholds
    // -------------------------------------------------------------------------

    public static string CalculateGrade(int score) => score switch
    {
        >= 995_000 => "SSS+",
        >= 990_000 => "SSS",
        >= 985_000 => "SS+",
        >= 980_000 => "SS",
        >= 975_000 => "S+",
        >= 970_000 => "S",
        >= 960_000 => "AAA+",
        >= 950_000 => "AAA",
        >= 925_000 => "AA+",
        >= 900_000 => "AA",
        >= 825_000 => "A+",
        >= 750_000 => "A",
        >= 700_000 => "B",
        >= 600_000 => "C",
        >= 450_000 => "D",
        _ => "F"
    };

    // -------------------------------------------------------------------------
    // Plate
    // -------------------------------------------------------------------------

    public static string CalculatePlate(IReadOnlyDictionary<HitJudgment, int> counts, int noteCount)
    {
        var perfectCount = counts[HitJudgment.Perfect];
        var goodCount = counts[HitJudgment.Good];
        var badCount = counts[HitJudgment.Bad];
        var missCount = counts[HitJudgment.Miss];

        if (perfectCount == noteCount) return "Perfect Game";
        if (goodCount == 0 && badCount == 0 && missCount == 0) return "Ultimate Game";
        if (badCount == 0 && missCount == 0) return "Extreme Game";
        if (missCount == 0) return "SuperB Game";
        if (missCount <= 5) return "Marvelous Game";
        if (missCount <= 10) return "Talented Game";
        if (missCount <= 20) return "Fair Game";
        return "Rough Game";
    }

    // -------------------------------------------------------------------------
    // Colors
    // -------------------------------------------------------------------------

    public static Color GetGradeColor(string grade) => grade switch
    {
        "SSS+" or "SSS" => Color.FromArgb("#87CEEB"),
        "SS+" or "SS" or "S+" or "S" => Color.FromArgb("#FFD700"),
        "AAA+" or "AAA" => Color.FromArgb("#C0C0C0"),
        "AA+" or "AA" or "A+" or "A" => Color.FromArgb("#CD7F32"),
        "B" => Color.FromArgb("#4169E1"),
        "C" => Color.FromArgb("#32CD32"),
        "D" => Color.FromArgb("#9ACD32"),
        "F" => Color.FromArgb("#228B22"),
        _ => Color.FromArgb("#FFFFFF")
    };

    public static Color GetPlateColor(string plate) => plate switch
    {
        "Perfect Game" or "Ultimate Game" => Color.FromArgb("#87CEEB"),
        "Extreme Game" or "SuperB Game" => Color.FromArgb("#FFD700"),
        "Marvelous Game" or "Talented Game" => Color.FromArgb("#C0C0C0"),
        "Fair Game" or "Rough Game" => Color.FromArgb("#CD7F32"),
        _ => Color.FromArgb("#FFFFFF")
    };
}
