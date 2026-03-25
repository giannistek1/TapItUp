using Microsoft.Maui.Graphics;

namespace PumpMaui.Game;

public sealed class NoteFieldDrawable : IDrawable
{
    private static readonly Color[] LaneColors = [
        Color.FromArgb("#00C2FF"), // Blue (bottom left)
        Color.FromArgb("#FF2D2D"), // Red (top left)
        Color.FromArgb("#FFE45E"), // Yellow (center)
        Color.FromArgb("#FF2D2D"), // Red (top right)
        Color.FromArgb("#00C2FF")  // Blue (bottom right)
    ];
    private readonly RhythmGameEngine _engine;

    public NoteFieldDrawable(RhythmGameEngine engine)
    {
        _engine = engine;
    }

    public double ScrollWindowSeconds { get; set; } = 2.2d;
    public bool IsLandscapeMode { get; set; } = false;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.FillColor = Color.FromArgb("#090212");
        canvas.FillRectangle(dirtyRect);

        // Adjust sizing for landscape mode
        var topMargin = IsLandscapeMode ? 8f : 24f;
        var bottomMargin = IsLandscapeMode ? 8f : 26f;
        var receptorY = IsLandscapeMode ? 40f : 92f;
        var laneGap = IsLandscapeMode ? 6f : 10f;

        // Smaller lane widths for landscape
        float[] laneWidths = IsLandscapeMode ?
            new[] { 0.8f, 0.8f, 0.8f, 0.8f, 0.8f } :
            new[] { 1f, 1f, 1f, 1f, 1f };

        float total = laneWidths.Sum();
        float unit = (dirtyRect.Width - laneGap * 6f) / total;
        float[] actualWidths = laneWidths.Select(w => w * unit).ToArray();

        var fieldBottom = dirtyRect.Height - bottomMargin;

        DrawLaneBackgrounds(canvas, dirtyRect, actualWidths, laneGap, receptorY, fieldBottom);
        DrawHoldBodies(canvas, actualWidths, laneGap, receptorY, fieldBottom); // Draw holds first
        DrawReceptors(canvas, actualWidths, laneGap, receptorY);
        DrawNotes(canvas, actualWidths, laneGap, receptorY, fieldBottom);
        DrawFrame(canvas, dirtyRect, receptorY, topMargin);
        canvas.RestoreState();
    }

    private void DrawLaneBackgrounds(ICanvas canvas, RectF dirtyRect, float[] actualWidths, float laneGap, float receptorY, float fieldBottom)
    {
        float x = laneGap;
        for (var lane = 0; lane < 5; lane++)
        {
            float width = actualWidths[lane];
            canvas.FillColor = Color.FromArgb(lane % 2 == 0 ? "#17102A" : "#120B22");
            canvas.FillRoundedRectangle(x, 18f, width, fieldBottom - 6f, 18f);

            canvas.StrokeColor = LaneColors[lane].WithAlpha(0.50f);
            canvas.StrokeSize = 2f;
            canvas.DrawRoundedRectangle(x, receptorY - 16f, width, fieldBottom - receptorY + 18f, 18f);

            canvas.FillColor = LaneColors[lane].WithAlpha(0.08f);
            canvas.FillRoundedRectangle(x, receptorY - 16f, width, fieldBottom - receptorY + 18f, 18f);

            x += width + laneGap;
        }

        canvas.StrokeColor = Colors.White.WithAlpha(0.12f);
        canvas.StrokeSize = 2f;
        canvas.DrawLine(0f, receptorY + 22f, dirtyRect.Width, receptorY + 22f);
    }

    private void DrawHoldBodies(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY, float fieldBottom)
    {
        float x = laneGap;
        var travelHeight = fieldBottom - receptorY - 18f;

        for (var lane = 0; lane < 5; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;

            // Find hold pairs for this lane - include both active holds and upcoming holds
            var holdStarts = _engine.Notes.Where(n => n.Lane == lane && n.Type == NoteType.HoldStart).ToList();

            foreach (var holdStart in holdStarts)
            {
                if (holdStart.HoldPartner == null) continue;

                var holdEnd = holdStart.HoldPartner;
                var startDelta = holdStart.TimeSeconds - _engine.CurrentTimeSeconds;
                var endDelta = holdEnd.TimeSeconds - _engine.CurrentTimeSeconds;

                // For active holds (hold start is consumed but hold is still active)
                bool isActiveHold = holdStart.Consumed && holdStart.IsHoldActive && !holdEnd.Consumed;

                // For upcoming holds (hold start not yet consumed)
                bool isUpcomingHold = !holdStart.Consumed;

                // Skip if this hold is completely finished or not relevant
                if (!isActiveHold && !isUpcomingHold) continue;
                if (endDelta < -PhoenixScoring.BadWindowSeconds) continue;

                // Calculate positions
                var startNormalized = (float)(startDelta / ScrollWindowSeconds);
                var endNormalized = (float)(endDelta / ScrollWindowSeconds);

                // For active holds, start drawing from the receptor line (current time)
                float visibleStartY, visibleEndY;

                if (isActiveHold)
                {
                    // Active hold: draw from receptor to hold end
                    visibleStartY = receptorY;
                    var endY = receptorY + endNormalized * travelHeight;
                    visibleEndY = Math.Min(endY, fieldBottom - 18f);
                }
                else
                {
                    // Upcoming hold: draw the full hold body
                    var startY = receptorY + startNormalized * travelHeight;
                    var endY = receptorY + endNormalized * travelHeight;
                    visibleStartY = Math.Max(startY, receptorY - 16f);
                    visibleEndY = Math.Min(endY, fieldBottom - 18f);
                }

                // Skip if hold would be invisible
                if (visibleStartY >= visibleEndY) continue;

                // Draw hold body with enhanced visual for active holds
                var holdWidth = width * 0.4f;
                var isHoldCurrentlyActive = _engine.IsLaneHoldActive(lane);

                // Different colors for active vs upcoming holds
                Color bodyColor;
                if (isActiveHold && isHoldCurrentlyActive)
                {
                    // Bright, pulsing color for currently held notes
                    var pulseIntensity = 0.8f + 0.2f * MathF.Sin((float)_engine.CurrentTimeSeconds * 8f);
                    bodyColor = LaneColors[lane].WithAlpha(pulseIntensity);
                }
                else if (isActiveHold)
                {
                    // Dimmer color for released but not finished holds
                    bodyColor = LaneColors[lane].WithAlpha(0.4f);
                }
                else
                {
                    // Normal color for upcoming holds
                    bodyColor = LaneColors[lane].WithAlpha(0.6f);
                }

                canvas.SaveState();
                canvas.FillColor = bodyColor;
                canvas.FillRoundedRectangle(
                    centerX - holdWidth / 2f,
                    visibleStartY,
                    holdWidth,
                    visibleEndY - visibleStartY,
                    4f);

                // Draw hold edges with stronger outline for active holds
                var edgeAlpha = isActiveHold && isHoldCurrentlyActive ? 1.0f : 0.8f;
                canvas.StrokeColor = LaneColors[lane].WithAlpha(edgeAlpha);
                canvas.StrokeSize = isActiveHold && isHoldCurrentlyActive ? 3f : 2f;
                canvas.DrawRoundedRectangle(
                    centerX - holdWidth / 2f,
                    visibleStartY,
                    holdWidth,
                    visibleEndY - visibleStartY,
                    4f);

                canvas.RestoreState();
            }

            x += width + laneGap;
        }
    }

    private void DrawReceptors(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY)
    {
        float x = laneGap;
        for (var lane = 0; lane < 5; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;
            var flashAge = _engine.GetLaneFlashAge(lane);
            var isHoldActive = _engine.IsLaneHoldActive(lane);
            var glow = flashAge >= 0d && flashAge <= 0.14d ? (float)(1d - flashAge / 0.14d) : 0f;

            // Enhanced glow for active holds with pulsing effect
            if (isHoldActive)
            {
                var pulseGlow = 0.6f + 0.4f * MathF.Sin((float)_engine.CurrentTimeSeconds * 6f);
                glow = Math.Max(glow, pulseGlow);
            }

            var receptorSize = MathF.Min(width * 0.72f, 52f);

            canvas.SaveState();
            canvas.Translate(centerX, receptorY);

            // Add extra glow ring for active holds
            if (isHoldActive)
            {
                var glowSize = receptorSize * 1.3f;
                canvas.FillColor = LaneColors[lane].WithAlpha(0.3f);
                if (lane == 2)
                {
                    canvas.FillRoundedRectangle(-glowSize / 2f, -glowSize / 2f, glowSize, glowSize, 8f);
                }
                else
                {
                    canvas.FillEllipse(-glowSize / 2f, -glowSize / 2f, glowSize, glowSize);
                }
            }

            if (lane == 2)
            {
                // Center: square
                canvas.FillColor = LaneColors[lane].WithAlpha(0.20f + glow * 0.40f);
                canvas.FillRectangle(-receptorSize / 2f, -receptorSize / 2f, receptorSize, receptorSize);
                canvas.StrokeColor = LaneColors[lane].WithAlpha(0.70f + glow * 0.25f);
                canvas.StrokeSize = isHoldActive ? 4f : 3f;
                canvas.DrawRectangle(-receptorSize / 2f, -receptorSize / 2f, receptorSize, receptorSize);
            }
            else
            {
                // Draw diagonal arrows using proper diagonal coordinates
                canvas.FillColor = LaneColors[lane].WithAlpha(0.20f + glow * 0.40f);
                canvas.StrokeColor = Colors.White.WithAlpha(0.70f + glow * 0.25f);
                canvas.StrokeSize = isHoldActive ? 4f : 3f;
                DrawDiagonalArrow(canvas, lane, receptorSize);
            }
            canvas.RestoreState();
            x += width + laneGap;
        }
    }

    private void DrawNotes(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY, float fieldBottom)
    {
        float x = laneGap;
        var travelHeight = fieldBottom - receptorY - 18f;
        for (var lane = 0; lane < 5; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;
            foreach (var note in _engine.Notes.Where(n => n.Lane == lane && !n.Consumed))
            {
                var deltaSeconds = note.TimeSeconds - _engine.CurrentTimeSeconds;
                if (deltaSeconds < -PhoenixScoring.BadWindowSeconds || deltaSeconds > ScrollWindowSeconds)
                    continue;

                var normalized = (float)(deltaSeconds / ScrollWindowSeconds);
                var y = receptorY + normalized * travelHeight;
                var size = MathF.Min(width * 0.62f, 40f);

                canvas.SaveState();
                canvas.Translate(centerX, y);

                // Different rendering based on note type
                switch (note.Type)
                {
                    case NoteType.Tap:
                        DrawNoteShape(canvas, lane, size, LaneColors[lane]);
                        break;

                    case NoteType.HoldStart:
                        // Hold start - brighter and slightly larger
                        DrawNoteShape(canvas, lane, size * 1.1f, LaneColors[lane]);
                        // Add hold indicator
                        canvas.StrokeColor = Colors.White;
                        canvas.StrokeSize = 3f;
                        if (lane == 2)
                        {
                            canvas.DrawRectangle(-size * 0.6f, -size * 0.6f, size * 1.2f, size * 1.2f);
                        }
                        else
                        {
                            DrawDiagonalArrow(canvas, lane, size * 1.2f, strokeOnly: true);
                        }
                        break;

                    case NoteType.HoldEnd:
                        // Hold end - dimmer
                        DrawNoteShape(canvas, lane, size, LaneColors[lane].WithAlpha(0.8f));
                        break;

                    case NoteType.HoldBody:
                        // Hold body - small dot
                        canvas.FillColor = LaneColors[lane].WithAlpha(0.6f);
                        canvas.FillEllipse(-size * 0.2f, -size * 0.2f, size * 0.4f, size * 0.4f);
                        break;
                }

                canvas.RestoreState();
            }
            x += width + laneGap;
        }
    }

    private void DrawNoteShape(ICanvas canvas, int lane, float size, Color color)
    {
        if (lane == 2)
        {
            // Center: square
            canvas.FillColor = color;
            canvas.FillRectangle(-size / 2f, -size / 2f, size, size);
            canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
            canvas.StrokeSize = 2f;
            canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
        }
        else
        {
            // Diagonal arrows
            canvas.FillColor = color;
            canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
            canvas.StrokeSize = 2f;
            DrawDiagonalArrow(canvas, lane, size);
        }
    }

    private static void DrawDiagonalArrow(ICanvas canvas, int lane, float size, bool strokeOnly = false)
    {
        var path = new PathF();
        var halfSize = size / 2f;
        var arrowWidth = halfSize * 0.4f; // Stem thickness

        switch (lane)
        {
            case 0: // ↙ Bottom-left diagonal arrow
                // Stem going from top-right to bottom-left
                path.MoveTo(halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(halfSize * 0.5f, -halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, halfSize * 0.5f);
                path.LineTo(-halfSize * 0.3f, halfSize * 0.8f);
                // Arrow head pointing down-left
                path.LineTo(-halfSize, halfSize * 0.5f);
                path.LineTo(-halfSize * 0.5f, halfSize);
                path.LineTo(-halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(-halfSize * 0.6f, halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, -halfSize * 0.6f);
                path.Close();
                break;

            case 1: // ↖ Top-left diagonal arrow  
                // Stem going from bottom-right to top-left
                path.MoveTo(halfSize * 0.3f, halfSize * 0.8f);
                path.LineTo(halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(halfSize * 0.5f, halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, -halfSize * 0.5f);
                path.LineTo(-halfSize * 0.3f, -halfSize * 0.8f);
                // Arrow head pointing up-left
                path.LineTo(-halfSize, -halfSize * 0.5f);
                path.LineTo(-halfSize * 0.5f, -halfSize);
                path.LineTo(-halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(-halfSize * 0.6f, -halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, halfSize * 0.6f);
                path.Close();
                break;

            case 3: // ↗ Top-right diagonal arrow
                // Stem going from bottom-left to top-right
                path.MoveTo(-halfSize * 0.3f, halfSize * 0.8f);
                path.LineTo(-halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(-halfSize * 0.5f, halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, -halfSize * 0.5f);
                path.LineTo(halfSize * 0.3f, -halfSize * 0.8f);
                // Arrow head pointing up-right
                path.LineTo(halfSize, -halfSize * 0.5f);
                path.LineTo(halfSize * 0.5f, -halfSize);
                path.LineTo(halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(halfSize * 0.6f, -halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, halfSize * 0.6f);
                path.Close();
                break;

            case 4: // ↘ Bottom-right diagonal arrow
                // Stem going from top-left to bottom-right
                path.MoveTo(-halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(-halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(-halfSize * 0.5f, -halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, halfSize * 0.5f);
                path.LineTo(halfSize * 0.3f, halfSize * 0.8f);
                // Arrow head pointing down-right
                path.LineTo(halfSize, halfSize * 0.5f);
                path.LineTo(halfSize * 0.5f, halfSize);
                path.LineTo(halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(halfSize * 0.6f, halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, -halfSize * 0.6f);
                path.Close();
                break;
        }

        if (strokeOnly)
        {
            canvas.DrawPath(path);
        }
        else
        {
            canvas.FillPath(path);
            canvas.DrawPath(path);
        }
    }

    private static void DrawFrame(ICanvas canvas, RectF dirtyRect, float receptorY, float topMargin)
    {
        canvas.StrokeColor = Colors.White.WithAlpha(0.18f);
        canvas.StrokeSize = 3f;
        canvas.DrawRoundedRectangle(4f, topMargin - 8f, dirtyRect.Width - 8f, dirtyRect.Height - topMargin - 8f, 18f);

        canvas.FontColor = Colors.White.WithAlpha(0.90f);
        canvas.FontSize = 14f;
        canvas.DrawString("PHOENIX STYLE NOTE FIELD", 12f, 10f, dirtyRect.Width - 24f, 18f, HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.DrawString("JUDGE", 12f, receptorY - 6f, dirtyRect.Width - 24f, 16f, HorizontalAlignment.Left, VerticalAlignment.Top);
    }
}
