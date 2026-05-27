namespace TapItUp.Game;

public static class GameConstants
{
    // Default AV (ArrowVelocity) — range 300–999.
    // Visual scroll speed = AV / currentBPM, so 300 BPM song at AV 300 = 1× speed.
    public const int DefaultAv = 300;
}
