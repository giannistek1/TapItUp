namespace PumpMaui.Game;

public static class PhoenixScoring
{
    public const double PerfectWindowSeconds = 0.045d;
    public const double GreatWindowSeconds = 0.090d;
    public const double GoodWindowSeconds = 0.135d;
    public const double BadWindowSeconds = 0.180d;
    public const int MaxScore = 1_000_000;

    public static HitJudgment GetJudgment(double deltaSeconds)
    {
        var absoluteDelta = Math.Abs(deltaSeconds);
        if (absoluteDelta <= PerfectWindowSeconds)
        {
            return HitJudgment.Perfect;
        }

        if (absoluteDelta <= GreatWindowSeconds)
        {
            return HitJudgment.Great;
        }

        if (absoluteDelta <= GoodWindowSeconds)
        {
            return HitJudgment.Good;
        }

        if (absoluteDelta <= BadWindowSeconds)
        {
            return HitJudgment.Bad;
        }

        return HitJudgment.Miss;
    }

    public static double GetWeight(HitJudgment judgment)
    {
        return judgment switch
        {
            HitJudgment.Perfect => 1.00d,
            HitJudgment.Great => 0.82d,
            HitJudgment.Good => 0.55d,
            HitJudgment.Bad => 0.20d,
            _ => 0d
        };
    }

    public static bool BreaksCombo(HitJudgment judgment)
    {
        return judgment is HitJudgment.Bad or HitJudgment.Miss;
    }

    public static int CalculateScore(IReadOnlyDictionary<HitJudgment, int> counts, int noteCount)
    {
        if (noteCount <= 0)
        {
            return 0;
        }

        var totalWeight = counts.Sum(pair => pair.Value * GetWeight(pair.Key));
        return (int)Math.Round(totalWeight / noteCount * MaxScore, MidpointRounding.AwayFromZero);
    }

    public static string CalculateGrade(int score, bool fullCombo)
    {
        if (score >= 995000)
        {
            return fullCombo ? "SSS+" : "SSS";
        }

        if (score >= 980000)
        {
            return fullCombo ? "SS+" : "SS";
        }

        if (score >= 950000)
        {
            return "S";
        }

        if (score >= 900000)
        {
            return "A";
        }

        if (score >= 800000)
        {
            return "B";
        }

        if (score >= 700000)
        {
            return "C";
        }

        return "D";
    }
}
