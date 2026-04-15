using Microsoft.Maui.Graphics;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace TapItUp.Game;

public sealed class NoteFieldDrawable : IDrawable
{
    private float _noteScale = 0.9f;

    private static readonly Color[] LaneColors = [
        Color.FromArgb("#00C2FF"), // Blue (bottom left)
        Color.FromArgb("#FF2D2D"), // Red (top left)
        Color.FromArgb("#FFE45E"), // Yellow (center)
        Color.FromArgb("#FF2D2D"), // Red (top right)
        Color.FromArgb("#00C2FF")  // Blue (bottom right)
    ];
    private readonly RhythmGameEngine _engine;

    // Image cache for different skins
    private static IImage? _centerPrime, _downleftPrime, _upleftPrime;
    private static IImage? _centerFiestaEx, _downleftFiestaEx, _upleftFiestaEx;
    private static IImage? _centerNxa, _downleftNxa, _upleftNxa;
    private static IImage? _centerOld, _downleftOld, _upleftOld;

    // Grayscale receptor images (center + others) for skins
    private static IImage? _grayPrime, _graycPrime;
    private static IImage? _grayFiestaEx, _graycFiestaEx;
    private static IImage? _grayNxa, _graycNxa;
    private static IImage? _grayOld, _graycOld;

    // Fixed race condition: use Task to ensure proper initialization
    private static Task? _loadingTask;
    private static bool _imagesLoaded = false;

    // Reusable per-frame note lists — avoids per-frame LINQ allocations
    private readonly List<PlayableNote> _visibleHolds = new(64);
    private readonly List<PlayableNote> _visibleNotes = new(128);
    private readonly float[] _laneWidths = new float[10]; // reused every Draw call

    // Debug flag for note borders (default: false = no borders)
    public bool ShowNoteBorders { get; set; } = false;

    // Note skin property
    public string NoteSkin { get; set; } = "Prime";

    /// <summary>
    /// Arrow Velocity (300–999). The visible scroll window is 720 / Av seconds,
    /// matching real Pump It Up scroll timing (AV 180 ≈ 4 s, AV 270 ≈ 2.67 s, AV 470 ≈ 1.53 s).
    /// </summary>
    public int Av { get; set; } = GameConstants.DefaultAv;

    public NoteFieldDrawable(RhythmGameEngine engine)
    {
        _engine = engine;
        // Fix race condition: ensure loading task starts only once
        if (_loadingTask == null)
        {
            _loadingTask = LoadImagesAsync();
        }
    }

    public double ScrollSpeedMultiplier { get; set; } = GameConstants.DefaultAv / 150d; // default AV 300 at 150 BPM
    public bool IsLandscapeMode { get; set; } = false;

    private static async Task LoadImagesAsync()
    {
        if (_imagesLoaded) return;

        try
        {
            System.Diagnostics.Debug.WriteLine("🖼️ Starting image loading for all note skins...");

            // Load Prime skin images
            _centerPrime = await LoadMauiAsset("center_prime.png");
            _downleftPrime = await LoadMauiAsset("downleft_prime.png");
            _upleftPrime = await LoadMauiAsset("upleft_prime.png");

            // Load FiestaEx skin images
            _centerFiestaEx = await LoadMauiAsset("center_fiestaex.png");
            _downleftFiestaEx = await LoadMauiAsset("downleft_fiestaex.png");
            _upleftFiestaEx = await LoadMauiAsset("upleft_fiestaex.png");

            // Load NXA skin images
            _centerNxa = await LoadMauiAsset("center_nxa.png");
            _downleftNxa = await LoadMauiAsset("downleft_nxa.png");
            _upleftNxa = await LoadMauiAsset("upleft_nxa.png");

            // Load old skin images
            _centerOld = await LoadMauiAsset("center_old.png");
            _downleftOld = await LoadMauiAsset("downleft_old.png");
            _upleftOld = await LoadMauiAsset("upleft_old.png");

            // Load grayscale receptor images (prime + variants)
            _grayPrime = await LoadMauiAsset("gray_prime.png");
            _graycPrime = await LoadMauiAsset("grayc_prime.png");

            _grayFiestaEx = await LoadMauiAsset("gray_fiestaex.png");
            _graycFiestaEx = await LoadMauiAsset("grayc_fiestaex.png");

            _grayNxa = await LoadMauiAsset("gray_nxa.png");
            _graycNxa = await LoadMauiAsset("grayc_nxa.png");

            _grayOld = await LoadMauiAsset("gray_old.png");
            _graycOld = await LoadMauiAsset("grayc_old.png");

            System.Diagnostics.Debug.WriteLine($"🖼️ Image loading summary - Prime: Center={_centerPrime != null}, Blue={_downleftPrime != null}, Red={_upleftPrime != null}");
            System.Diagnostics.Debug.WriteLine($"🖼️ Image loading summary - Fiestaex: Center={_centerFiestaEx != null}, Blue={_downleftFiestaEx != null}, Red={_upleftFiestaEx != null}");
            System.Diagnostics.Debug.WriteLine($"🖼️ Image loading summary - NXA: Center={_centerNxa != null}, Blue={_downleftNxa != null}, Red={_upleftNxa != null}");
            System.Diagnostics.Debug.WriteLine($"🖼️ Image loading summary - Old: Center={_centerOld != null}, Blue={_downleftOld != null}, Red={_upleftOld != null}");
            System.Diagnostics.Debug.WriteLine($"🖼️ Gray images - prime={_grayPrime != null}, grayc={_graycPrime != null}, fiestaex={_grayFiestaEx != null}, grayc_fiestaex={_graycFiestaEx != null}");

            _imagesLoaded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to load note images: {ex.Message}");
            _imagesLoaded = true;
        }
    }

    // Helper method to get the correct images based on selected skin
    private IImage? GetCenterImage()
    {
        return NoteSkin.ToLower() switch
        {
            "fiestaex" => _centerFiestaEx,
            "old" => _centerOld,
            "nxa" => _centerNxa,
            _ => _centerPrime // Default to Prime
        };
    }

    private IImage? GetBlueArrowImage()
    {
        return NoteSkin.ToLower() switch
        {
            "fiestaex" => _downleftFiestaEx,
            "old" => _downleftOld,
            "nxa" => _downleftNxa,
            _ => _downleftPrime // Default to Prime
        };
    }

    private IImage? GetRedArrowImage()
    {
        return NoteSkin.ToLower() switch
        {
            "fiestaex" => _upleftFiestaEx,
            "old" => _upleftOld,
            "nxa" => _upleftNxa,
            _ => _upleftPrime // Default to Prime
        };
    }

    // New helpers for grayscale receptor images
    private IImage? GetGrayImage()
    {
        return NoteSkin.ToLower() switch
        {
            "fiestaex" => _grayFiestaEx,
            "old" => _grayOld,
            "nxa" => _grayNxa,
            _ => _grayPrime
        };
    }

    private IImage? GetGrayCenterImage()
    {
        return NoteSkin.ToLower() switch
        {
            "fiestaex" => _graycFiestaEx,
            "old" => _graycOld,
            "nxa" => _graycNxa,
            _ => _graycPrime
        };
    }

    private static async Task<IImage?> LoadMauiAsset(string filename)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"🔍 Attempting to load: {filename}");

            Stream? stream = null;
            string? successfulName = null;

            // Generate all possible filename variations that MAUI might create
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            var extension = Path.GetExtension(filename);

            var possibleNames = new List<string>
            {
                filename,                                           // yellow_center.png
                filename.Replace("_", "-"),                        // yellow-center.png
                $"{filenameWithoutExtension}.scale-100{extension}", // yellow_center.scale-100.png
                $"{filenameWithoutExtension.Replace("_", "-")}.scale-100{extension}", // yellow-center.scale-100.png
                $"{filenameWithoutExtension}.scale-200{extension}", // yellow_center.scale-200.png
                $"{filenameWithoutExtension}.scale-150{extension}", // yellow_center.scale-150.png
                $"{filenameWithoutExtension}.scale-125{extension}", // yellow_center.scale-125.png
            };

            // Method 1: Try FileSystem.OpenAppPackageFileAsync with all possible names
            foreach (var name in possibleNames)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"  📦 Trying FileSystem.OpenAppPackageFileAsync({name})");
                    stream = await FileSystem.OpenAppPackageFileAsync(name);
                    successfulName = name;
                    System.Diagnostics.Debug.WriteLine($"  ✅ Success with FileSystem.OpenAppPackageFileAsync: {name}");
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"  ❌ Failed {name}: {ex.Message}");
                }
            }

            // Method 2: If FileSystem approach fails, try embedded resources
            if (stream == null)
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                    // Try multiple resource name formats
                    var resourceNames = new List<string>();
                    foreach (var name in possibleNames)
                    {
                        resourceNames.Add($"TapItUp.Resources.Raw.{name}");
                        resourceNames.Add($"Resources.Raw.{name}");
                        resourceNames.Add($"Raw.{name}");
                        resourceNames.Add(name);
                    }

                    foreach (var resourceName in resourceNames)
                    {
                        System.Diagnostics.Debug.WriteLine($"  📦 Trying embedded resource: {resourceName}");
                        stream = assembly.GetManifestResourceStream(resourceName);

                        if (stream != null)
                        {
                            successfulName = resourceName;
                            System.Diagnostics.Debug.WriteLine($"  ✅ Success with embedded resource: {resourceName}");
                            break;
                        }
                    }

                    // If still no stream, list all available resources for debugging
                    if (stream == null)
                    {
                        System.Diagnostics.Debug.WriteLine("  📋 Available embedded resources:");
                        var allResourceNames = assembly.GetManifestResourceNames();
                        foreach (var name in allResourceNames)
                        {
                            if (name.Contains("Image") || name.Contains(".png") || name.Contains(".jpg") || name.Contains("gray") || name.Contains("yellow") || name.Contains("blue") || name.Contains("red"))
                            {
                                System.Diagnostics.Debug.WriteLine($"    - {name}");
                            }
                        }
                    }
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"  ❌ Embedded resource approach failed: {ex2.Message}");
                }
            }

            if (stream != null)
            {
                System.Diagnostics.Debug.WriteLine($"  🎯 Stream obtained from {successfulName}");

                using (stream)
                {
                    // Fix Android stream.Length issue: use MemoryStream + CopyToAsync
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    var bytes = ms.ToArray();

                    System.Diagnostics.Debug.WriteLine($"  📏 Copied {bytes.Length} bytes to memory stream");

                    var imageLoadingService = GetImageLoadingService();
                    if (imageLoadingService != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"  🖼️ Creating image from bytes using {imageLoadingService.GetType().Name}");
                        var image = imageLoadingService.FromBytes(bytes);
                        System.Diagnostics.Debug.WriteLine($"  ✅ Image created successfully: {image != null}");
                        return image;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  ❌ No image loading service available");
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"  ❌ No stream could be obtained for {filename} or any of its variants");
            }

            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to load image {filename}: {ex.Message}");
            return null;
        }
    }

    private static IImageLoadingService? GetImageLoadingService()
    {
        try
        {
            // Try to get the service from the application's service provider
            var serviceProvider = Application.Current?.Handler?.MauiContext?.Services;
            var service = serviceProvider?.GetService(typeof(IImageLoadingService)) as IImageLoadingService;

            if (service != null)
            {
                System.Diagnostics.Debug.WriteLine($"  🔧 Got IImageLoadingService from DI: {service.GetType().Name}");
                return service;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  ❌ Failed to get service from DI: {ex.Message}");
        }

        // If service lookup fails, try platform-specific implementations
        try
        {
            System.Diagnostics.Debug.WriteLine($"  🔧 Trying platform-specific image loading service");
#if WINDOWS
            var platformService = new Microsoft.Maui.Graphics.Win2D.W2DImageLoadingService();
            System.Diagnostics.Debug.WriteLine($"  ✅ Created W2DImageLoadingService");
            return platformService;
#elif ANDROID
            var platformService = new Microsoft.Maui.Graphics.Platform.PlatformImageLoadingService();
            System.Diagnostics.Debug.WriteLine($"  ✅ Created Android PlatformImageLoadingService");
            return platformService;
#elif IOS || MACCATALYST
            var platformService = new Microsoft.Maui.Graphics.Platform.PlatformImageLoadingService();
            System.Diagnostics.Debug.WriteLine($"  ✅ Created iOS PlatformImageLoadingService");
            return platformService;
#else
            System.Diagnostics.Debug.WriteLine($"  ❌ No platform-specific service available");
            return null;
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  ❌ Failed to create platform-specific service: {ex.Message}");
            return null;
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.FillColor = Color.FromArgb("#090212");
        canvas.FillRectangle(dirtyRect);

        var isDouble = _engine.IsDoubleChart;
        var laneCount = isDouble ? 10 : 5;

        var topMargin = IsLandscapeMode ? 8f : 24f;
        var bottomMargin = IsLandscapeMode ? 12f : 26f;
        var receptorY = IsLandscapeMode ? 50f : 92f;
        var laneGap = 0f;

        float unit = dirtyRect.Width / laneCount;
        for (var i = 0; i < laneCount; i++)
            _laneWidths[i] = unit;

        var fieldBottom = dirtyRect.Height - bottomMargin;

        var songSpeedMultiplier = GetActiveSongSpeedMultiplier();
        var scrollWindowSeconds = 720.0 / Av / songSpeedMultiplier;

        DrawLaneBackgrounds(canvas, dirtyRect, _laneWidths, laneGap, receptorY, fieldBottom, laneCount);
        DrawHoldBodies(canvas, _laneWidths, laneGap, receptorY, fieldBottom, laneCount, scrollWindowSeconds);
        DrawReceptors(canvas, _laneWidths, laneGap, receptorY, laneCount);
        DrawNotes(canvas, _laneWidths, laneGap, receptorY, fieldBottom, laneCount, scrollWindowSeconds);
        DrawFrame(canvas, dirtyRect, receptorY, topMargin);
        canvas.RestoreState();
    }

    /// <summary>
    /// Returns the #SPEEDS multiplier active at the current engine time.
    /// Falls back to 1.0 when no #SPEEDS tag is present.
    /// </summary>
    private double GetActiveSongSpeedMultiplier()
    {
        // #SPEEDS is a per-chart tag in .ssc files — read from the loaded chart.
        var speedChanges = _engine.Chart?.SpeedChanges;
        if (speedChanges == null || speedChanges.Count == 0)
            return 1.0;

        var bpmChanges = _engine.Song?.BpmChanges;
        if (bpmChanges == null || bpmChanges.Count == 0)
            return 1.0;

        var currentBeat = SecondsToBeat(_engine.CurrentTimeSeconds, bpmChanges);

        var active = speedChanges[0].Multiplier;
        foreach (var sc in speedChanges)
        {
            if (sc.Beat <= currentBeat + 0.0001)
                active = sc.Multiplier;
            else
                break;
        }

        return active;
    }

    /// <summary>
    /// Converts an elapsed-time value (seconds) back to a beat number using
    /// the song's BPM change list. Inverse of BeatToSeconds in SscParser.
    /// </summary>
    private static double SecondsToBeat(double seconds, IReadOnlyList<BpmChange> bpmChanges)
    {
        if (seconds <= 0d) return 0d;

        var beat = 0d;
        var currentBpm = bpmChanges[0].Bpm;
        var lastBeat = 0d;
        var elapsed = 0d;

        // Iterate without allocating — bpmChanges is already ordered at load time
        for (var i = 0; i < bpmChanges.Count; i++)
        {
            var change = bpmChanges[i];
            var beatsInSegment = change.Beat - lastBeat;
            var secondsInSegment = beatsInSegment / currentBpm * 60d;

            if (elapsed + secondsInSegment >= seconds)
                break;

            elapsed += secondsInSegment;
            beat = change.Beat;
            lastBeat = change.Beat;
            currentBpm = change.Bpm;
        }

        beat += (seconds - elapsed) / 60d * currentBpm;
        return beat;
    }

    private void DrawLaneBackgrounds(ICanvas canvas, RectF dirtyRect, float[] actualWidths, float laneGap, float receptorY, float fieldBottom, int laneCount)
    {
        float x = laneGap;
        for (var lane = 0; lane < laneCount; lane++)
        {
            float width = actualWidths[lane];

            canvas.FillColor = Color.FromArgb("#0B0710");
            canvas.FillRoundedRectangle(x, 18f, width, fieldBottom - 6f, 18f);

            canvas.FillColor = Color.FromArgb("#0B0710").WithAlpha(0.04f);
            canvas.FillRoundedRectangle(x, receptorY - 16f, width, fieldBottom - receptorY + 18f, 18f);

            x += width + laneGap;
        }

        // Subtle separator line across field (kept neutral and very faint)
        canvas.StrokeColor = Colors.White.WithAlpha(0.06f);
        canvas.StrokeSize = 1f;
        canvas.DrawLine(0f, receptorY + 22f, dirtyRect.Width, receptorY + 22f);
    }

    private void DrawHoldBodies(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY, float fieldBottom, int laneCount, double scrollWindowSeconds)
    {
        float x = laneGap;
        var travelHeight = fieldBottom - receptorY - 18f;
        var badWindow = PhoenixScoring.GetBadWindow(JudgmentDifficulty.Standard);
        var notes = _engine.Notes;

        for (var lane = 0; lane < laneCount; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;
            var laneColorIndex = lane % 5;

            // Build visible holds without LINQ allocations
            _visibleHolds.Clear();
            for (var i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.Lane == lane && n.Type == NoteType.HoldStart)
                    _visibleHolds.Add(n);
            }

            for (var hi = 0; hi < _visibleHolds.Count; hi++)
            {
                var holdStart = _visibleHolds[hi];
                if (holdStart.HoldPartner == null) continue;

                var holdEnd = holdStart.HoldPartner;
                var startDelta = holdStart.TimeSeconds - _engine.CurrentTimeSeconds;
                var endDelta = holdEnd.TimeSeconds - _engine.CurrentTimeSeconds;

                bool isActiveHold = holdStart.Consumed && holdStart.IsHoldActive && !holdEnd.Consumed;
                bool isUpcomingHold = !holdStart.Consumed;

                if (!isActiveHold && !isUpcomingHold) continue;
                if (endDelta < -badWindow) continue;

                var startNormalized = (float)(startDelta / scrollWindowSeconds);
                var endNormalized = (float)(endDelta / scrollWindowSeconds);

                float visibleStartY, visibleEndY;

                if (isActiveHold)
                {
                    visibleStartY = receptorY;
                    var endY = receptorY + endNormalized * travelHeight;
                    visibleEndY = Math.Min(endY, fieldBottom - 18f);
                }
                else
                {
                    var startY = receptorY + startNormalized * travelHeight;
                    var endY = receptorY + endNormalized * travelHeight;
                    visibleStartY = Math.Max(startY, receptorY - 16f);
                    visibleEndY = Math.Min(endY, fieldBottom - 18f);
                }

                if (visibleStartY >= visibleEndY) continue;

                var holdWidth = IsLandscapeMode ? width * 0.60f : width * 0.65f;
                var isHoldCurrentlyActive = _engine.IsLaneHoldActive(lane);

                Color bodyColor;
                if (isActiveHold && isHoldCurrentlyActive)
                {
                    var pulseIntensity = 0.8f + 0.2f * MathF.Sin((float)_engine.CurrentTimeSeconds * 8f);
                    bodyColor = LaneColors[laneColorIndex].WithAlpha(pulseIntensity);
                }
                else if (isActiveHold)
                {
                    bodyColor = LaneColors[laneColorIndex].WithAlpha(0.4f);
                }
                else
                {
                    bodyColor = LaneColors[laneColorIndex].WithAlpha(0.6f);
                }

                canvas.SaveState();
                canvas.FillColor = bodyColor;
                canvas.FillRoundedRectangle(
                    centerX - holdWidth / 2f,
                    visibleStartY,
                    holdWidth,
                    visibleEndY - visibleStartY,
                    4f);

                var edgeAlpha = isActiveHold && isHoldCurrentlyActive ? 1.0f : 0.8f;
                canvas.StrokeColor = LaneColors[laneColorIndex].WithAlpha(edgeAlpha);
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

    private void DrawNotes(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY, float fieldBottom, int laneCount, double scrollWindowSeconds)
    {
        float x = laneGap;
        var travelHeight = fieldBottom - receptorY - 18f;
        var badWindow = PhoenixScoring.GetBadWindow(_engine.JudgmentDifficulty);
        var notes = _engine.Notes;

        for (var lane = 0; lane < laneCount; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;
            var laneShapeIndex = lane % 5;

            // Build visible notes without LINQ allocations
            _visibleNotes.Clear();
            for (var i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.Lane == lane && !n.Consumed)
                    _visibleNotes.Add(n);
            }

            for (var ni = 0; ni < _visibleNotes.Count; ni++)
            {
                var note = _visibleNotes[ni];
                var deltaSeconds = note.TimeSeconds - _engine.CurrentTimeSeconds;

                if (deltaSeconds < -badWindow || deltaSeconds > scrollWindowSeconds)
                    continue;

                var normalized = (float)(deltaSeconds / scrollWindowSeconds);
                var y = receptorY + normalized * travelHeight;

                float size;
                if (IsLandscapeMode)
                    size = MathF.Min(width * _noteScale, 35f);
                else
                    size = MathF.Min(width * _noteScale, 44f);

                canvas.SaveState();
                canvas.Translate(centerX, y);

                switch (note.Type)
                {
                    case NoteType.Tap:
                        DrawNoteShape(canvas, laneShapeIndex, size, LaneColors[laneShapeIndex]);
                        break;

                    case NoteType.HoldStart:
                        DrawNoteShape(canvas, laneShapeIndex, size * 1.1f, LaneColors[laneShapeIndex]);
                        if (ShowNoteBorders)
                        {
                            canvas.StrokeColor = Colors.White;
                            canvas.StrokeSize = 3f;
                            if (laneShapeIndex == 2)
                                canvas.DrawRectangle(-size * 0.6f, -size * 0.6f, size * 1.2f, size * 1.2f);
                            else
                                DrawDiagonalArrow(canvas, laneShapeIndex, size * 1.2f, strokeOnly: true);
                        }
                        break;

                    case NoteType.HoldEnd:
                        DrawNoteShape(canvas, laneShapeIndex, size, LaneColors[laneShapeIndex].WithAlpha(0.8f));
                        break;

                    case NoteType.HoldBody:
                        canvas.FillColor = LaneColors[laneShapeIndex].WithAlpha(0.6f);
                        canvas.FillEllipse(-size * 0.2f, -size * 0.2f, size * 0.4f, size * 0.4f);
                        break;
                }

                canvas.RestoreState();
            }
            x += width + laneGap;
        }
    }

    private void DrawReceptors(ICanvas canvas, float[] actualWidths, float laneGap, float receptorY, int laneCount)
    {
        float x = laneGap;
        for (var lane = 0; lane < laneCount; lane++)
        {
            float width = actualWidths[lane];
            var centerX = x + width / 2f;
            var flashAge = _engine.GetLaneFlashAge(lane);
            var isHoldActive = _engine.IsLaneHoldActive(lane);
            var glow = flashAge >= 0d && flashAge <= 0.14d ? (float)(1d - flashAge / 0.14d) : 0f;

            if (isHoldActive)
            {
                var pulseGlow = 0.6f + 0.4f * MathF.Sin((float)_engine.CurrentTimeSeconds * 6f);
                glow = Math.Max(glow, pulseGlow);
            }

            float receptorSize;
            if (IsLandscapeMode)
                receptorSize = MathF.Min(width * _noteScale, 38f);
            else
                receptorSize = MathF.Min(width * _noteScale, 44f);

            // Shape/color index is always within the 5-lane pattern
            var laneShapeIndex = lane % 5;

            canvas.SaveState();
            canvas.Translate(centerX, receptorY);

            if (isHoldActive)
            {
                var glowSize = receptorSize * 1.3f;
                canvas.FillColor = Colors.White.WithAlpha(0.06f);
                if (laneShapeIndex == 2)
                    canvas.FillRoundedRectangle(-glowSize / 2f, -glowSize / 2f, glowSize, glowSize, 8f);
                else
                    canvas.FillEllipse(-glowSize / 2f, -glowSize / 2f, glowSize, glowSize);
            }

            var lastJudgment = _engine.GetLaneLastJudgment(lane);
            var isStarworthy = (lastJudgment == HitJudgment.Perfect || lastJudgment == HitJudgment.Great)
                               && flashAge >= 0d && flashAge <= 0.30d;

            if (isStarworthy)
            {
                // ── 5. Scale punch ────────────────────────────────────────────────────
                // Receptor scale: 1.0 → 1.1 → 1.0 over ~120 ms, eased with a sine arch
                const double punchDuration = 0.12d;
                var punchT = (float)Math.Clamp(flashAge / punchDuration, 0d, 1d);
                var scaleFactor = 1.0f + 0.10f * MathF.Sin(punchT * MathF.PI); // arch: 0→peak→0

                canvas.SaveState();
                canvas.Scale(scaleFactor, scaleFactor);
                DrawStarBurst(canvas, laneShapeIndex, receptorSize, flashAge, lastJudgment);
                canvas.RestoreState();
            }

            DrawReceptorShape(canvas, laneShapeIndex, receptorSize, glow, isHoldActive);

            if (glow > 0f)
            {
                var pulse = 0.9f + 0.05f * MathF.Sin((float)_engine.CurrentTimeSeconds * 18f);
                var yellowAlpha = Math.Clamp(glow * 0.5f * pulse, 0f, 1f);

                canvas.SaveState();
                canvas.FillColor = Color.FromArgb("#FFE45E").WithAlpha(yellowAlpha);

                if (laneShapeIndex == 2)
                {
                    var haloSize = receptorSize * 1.2f;
                    canvas.FillRoundedRectangle(-haloSize / 2f, -haloSize / 2f, haloSize, haloSize, 8f);
                }
                else
                {
                    var haloSize = receptorSize * 1.3f;
                    canvas.FillEllipse(-haloSize / 2f, -haloSize / 2f, haloSize, haloSize);
                }

                canvas.RestoreState();
            }

            canvas.RestoreState();
            x += width + laneGap;
        }
    }

    private void DrawReceptorShape(ICanvas canvas, int lane, float size, float glow, bool isHoldActive)
    {
        if (lane == 2)
        {
            var grayCenter = GetGrayCenterImage();
            if (grayCenter != null)
            {
                canvas.Alpha = 0.90f + glow * 0.10f;
                canvas.DrawImage(grayCenter, -size / 2f, -size / 2f, size, size);
                canvas.Alpha = 1f;

                if (ShowNoteBorders)
                {
                    canvas.StrokeColor = Colors.White.WithAlpha(0.55f + glow * 0.25f);
                    canvas.StrokeSize = isHoldActive ? 4f : 3f;
                    canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
                }

                return;
            }

            var centerImage = GetCenterImage();
            if (centerImage != null)
            {
                canvas.Alpha = 0.20f + glow * 0.40f;
                canvas.DrawImage(centerImage, -size / 2f, -size / 2f, size, size);
                canvas.Alpha = 1f;

                if (ShowNoteBorders)
                {
                    canvas.StrokeColor = LaneColors[lane].WithAlpha(0.70f + glow * 0.25f);
                    canvas.StrokeSize = isHoldActive ? 4f : 3f;
                    canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
                }
            }
            else
            {
                canvas.FillColor = Colors.White.WithAlpha(0.18f + glow * 0.4f);
                canvas.FillRectangle(-size / 2f, -size / 2f, size, size);
                if (ShowNoteBorders)
                {
                    canvas.StrokeColor = Colors.White.WithAlpha(0.7f);
                    canvas.StrokeSize = 2f;
                    canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
                }
            }
        }
        else
        {
            DrawGrayReceptor(canvas, lane, size, glow, isHoldActive);
        }
    }

    private void DrawGrayReceptor(ICanvas canvas, int lane, float size, float glow, bool isHoldActive)
    {
        var gray = GetGrayImage();

        float rotation = lane switch
        {
            0 => -90f,
            1 => 0f,
            3 => 90f,
            4 => 180f,
            _ => 0f
        };

        if (gray != null)
        {
            canvas.SaveState();

            if (rotation != 0f)
                canvas.Rotate(rotation);

            canvas.Alpha = 1f;
            canvas.DrawImage(gray, -size / 2f, -size / 2f, size, size);
            canvas.Alpha = 1f;
            canvas.RestoreState();

            if (ShowNoteBorders)
            {
                canvas.StrokeColor = Colors.White.WithAlpha(0.70f + glow * 0.25f);
                canvas.StrokeSize = isHoldActive ? 4f : 3f;
                canvas.DrawEllipse(-size / 2f, -size / 2f, size, size);
            }

            return;
        }

        IImage? image = null;
        float fallbackRotation = 0f;

        switch (lane)
        {
            case 0:
                image = GetBlueArrowImage();
                fallbackRotation = -90f;
                break;
            case 1:
                image = GetRedArrowImage();
                fallbackRotation = 0f;
                break;
            case 3:
                image = GetRedArrowImage();
                fallbackRotation = 90f;
                break;
            case 4:
                image = GetBlueArrowImage();
                fallbackRotation = 180f;
                break;
        }

        if (image != null)
        {
            canvas.SaveState();
            if (fallbackRotation != 0f) canvas.Rotate(fallbackRotation);
            canvas.Alpha = 0.20f + glow * 0.40f;
            canvas.DrawImage(image, -size / 2f, -size / 2f, size, size);
            canvas.Alpha = 1f;
            canvas.RestoreState();

            if (ShowNoteBorders)
            {
                canvas.StrokeColor = Colors.White.WithAlpha(0.70f + glow * 0.25f);
                canvas.StrokeSize = isHoldActive ? 4f : 3f;
                canvas.DrawEllipse(-size / 2f, -size / 2f, size, size);
            }
        }
        else
        {
            canvas.FillColor = LaneColors[lane].WithAlpha(0.20f + glow * 0.40f);

            if (ShowNoteBorders)
            {
                canvas.StrokeColor = Colors.White.WithAlpha(0.70f + glow * 0.25f);
                canvas.StrokeSize = isHoldActive ? 4f : 3f;
            }

            DrawDiagonalArrow(canvas, lane, size);
        }
    }

    private void DrawNoteShape(ICanvas canvas, int lane, float size, Color color)
    {
        if (lane == 2)
        {
            var centerImage = GetCenterImage();
            if (centerImage != null)
            {
                canvas.DrawImage(centerImage, -size / 2f, -size / 2f, size, size);
                if (ShowNoteBorders)
                {
                    canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
                    canvas.StrokeSize = 2f;
                    canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
                }
            }
            else
            {
                canvas.FillColor = color;
                canvas.FillRectangle(-size / 2f, -size / 2f, size, size);
                if (ShowNoteBorders)
                {
                    canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
                    canvas.StrokeSize = 2f;
                    canvas.DrawRectangle(-size / 2f, -size / 2f, size, size);
                }
            }
        }
        else
        {
            DrawArrowNote(canvas, lane, size, color);
        }
    }

    private void DrawArrowNote(ICanvas canvas, int lane, float size, Color color)
    {
        IImage? image = null;
        float rotation = 0f;

        switch (lane)
        {
            case 0: image = GetBlueArrowImage(); break;
            case 1: image = GetRedArrowImage(); break;
            case 3: image = GetRedArrowImage(); rotation = 90f; break;
            case 4: image = GetBlueArrowImage(); rotation = -90f; break;
        }

        if (image != null)
        {
            canvas.SaveState();
            if (rotation != 0f)
                canvas.Rotate(rotation);
            canvas.DrawImage(image, -size / 2f, -size / 2f, size, size);
            canvas.RestoreState();

            if (ShowNoteBorders)
            {
                canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
                canvas.StrokeSize = 2f;
                canvas.DrawEllipse(-size / 2f, -size / 2f, size, size);
            }
        }
        else
        {
            canvas.FillColor = color;
            if (ShowNoteBorders)
            {
                canvas.StrokeColor = Colors.White.WithAlpha(0.80f);
                canvas.StrokeSize = 2f;
            }
            DrawDiagonalArrow(canvas, lane, size);
        }
    }

    private static void DrawDiagonalArrow(ICanvas canvas, int lane, float size, bool strokeOnly = false)
    {
        var path = new PathF();
        var halfSize = size / 2f;

        switch (lane)
        {
            case 0: // ↙ Bottom-left
                path.MoveTo(halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(halfSize * 0.5f, -halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, halfSize * 0.5f);
                path.LineTo(-halfSize * 0.3f, halfSize * 0.8f);
                path.LineTo(-halfSize, halfSize * 0.5f);
                path.LineTo(-halfSize * 0.5f, halfSize);
                path.LineTo(-halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(-halfSize * 0.6f, halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, -halfSize * 0.6f);
                path.Close();
                break;

            case 1: // ↖ Top-left
                path.MoveTo(halfSize * 0.3f, halfSize * 0.8f);
                path.LineTo(halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(halfSize * 0.5f, halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, -halfSize * 0.5f);
                path.LineTo(-halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(-halfSize, -halfSize * 0.5f);
                path.LineTo(-halfSize * 0.5f, -halfSize);
                path.LineTo(-halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(-halfSize * 0.6f, -halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, halfSize * 0.6f);
                path.Close();
                break;

            case 3: // ↗ Top-right
                path.MoveTo(-halfSize * 0.3f, halfSize * 0.8f);
                path.LineTo(-halfSize * 0.8f, halfSize * 0.3f);
                path.LineTo(-halfSize * 0.5f, halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, -halfSize * 0.5f);
                path.LineTo(halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(halfSize, -halfSize * 0.5f);
                path.LineTo(halfSize * 0.5f, -halfSize);
                path.LineTo(halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(halfSize * 0.6f, -halfSize * 0.1f);
                path.LineTo(-halfSize * 0.1f, halfSize * 0.6f);
                path.Close();
                break;

            case 4: // ↘ Bottom-right
                path.MoveTo(-halfSize * 0.3f, -halfSize * 0.8f);
                path.LineTo(-halfSize * 0.8f, -halfSize * 0.3f);
                path.LineTo(-halfSize * 0.5f, -halfSize * 0.1f);
                path.LineTo(halfSize * 0.1f, halfSize * 0.5f);
                path.LineTo(halfSize * 0.3f, halfSize * 0.8f);
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
    }

    private static void DrawStarBurst(ICanvas canvas, int lane, float receptorSize, double flashAge, HitJudgment judgment)
    {
        const double TotalDuration = 0.30d;
        var t = (float)Math.Clamp(flashAge / TotalDuration, 0d, 1d);

        // ── 1. Base flash ────────────────────────────────────────────────────────
        // 0–100 ms: ease-out white overlay that covers the receptor area
        const double flashDuration = 0.10d;
        if (flashAge <= flashDuration)
        {
            var ft = (float)(flashAge / flashDuration);                     // 0→1
            var flashAlpha = Math.Clamp(1.0f - ft * ft, 0f, 0.95f);        // ease-out
            canvas.FillColor = Colors.White.WithAlpha(flashAlpha);
            if (lane == 2)
                canvas.FillRectangle(-receptorSize / 2f, -receptorSize / 2f, receptorSize, receptorSize);
            else
                canvas.FillEllipse(-receptorSize / 2f, -receptorSize / 2f, receptorSize, receptorSize);
        }

        // ── 2. Radial glow ───────────────────────────────────────────────────────
        // Soft white-blue halo expands 1x → 2x receptor size over 250 ms
        const double glowDuration = 0.25d;
        if (flashAge <= glowDuration)
        {
            var gt = (float)(flashAge / glowDuration);                      // 0→1
            var glowAlpha = Math.Clamp((1.0f - gt) * 0.55f, 0f, 1f);
            var glowRadius = receptorSize * (0.5f + gt * 1.0f);            // 0.5x → 1.5x half-radius

            // Slightly blue-tinted outer ring
            canvas.FillColor = Color.FromArgb("#C0DFFF").WithAlpha(glowAlpha * 0.60f);
            canvas.FillEllipse(-glowRadius, -glowRadius, glowRadius * 2f, glowRadius * 2f);

            // Pure white inner core
            var coreRadius = glowRadius * 0.55f;
            canvas.FillColor = Colors.White.WithAlpha(glowAlpha * 0.80f);
            canvas.FillEllipse(-coreRadius, -coreRadius, coreRadius * 2f, coreRadius * 2f);
        }

        // ── 3. Shine sweep ───────────────────────────────────────────────────────
        // Elongated diagonal streak (top-left → bottom-right) over 0–120 ms
        const double shineDuration = 0.12d;
        if (flashAge <= shineDuration)
        {
            var st = (float)(flashAge / shineDuration);                     // 0→1
            var shineAlpha = Math.Clamp(1.0f - st, 0f, 0.85f);

            canvas.SaveState();
            canvas.Rotate(-45f);                                            // align streak diagonally

            // Streak: wide at centre, tapers at ends → draw as filled oval
            var streakLength = receptorSize * (1.2f + st * 0.8f);          // grows as it sweeps
            var streakWidth = receptorSize * 0.28f;

            // Offset the streak so it starts top-left and moves to bottom-right
            var sweepOffset = receptorSize * (st - 0.5f) * 1.4f;

            canvas.FillColor = Colors.White.WithAlpha(shineAlpha);
            canvas.FillEllipse(
                sweepOffset - streakLength / 2f,
                -streakWidth / 2f,
                streakLength,
                streakWidth);

            canvas.RestoreState();
        }

        // ── 4. Particle sparkles ─────────────────────────────────────────────────
        // 8 small particles burst outward from the centre and fade over 300 ms
        const double particleDuration = 0.30d;
        if (flashAge <= particleDuration)
        {
            var pt = (float)(flashAge / particleDuration);
            var particleAlpha = Math.Clamp(1.0f - pt, 0f, 0.90f);
            const int ParticleCount = 8;

            for (var i = 0; i < ParticleCount; i++)
            {
                var angle = i * (MathF.PI * 2f / ParticleCount) + MathF.PI / 8f; // stagger 22.5°
                var distance = receptorSize * (0.55f + pt * 1.10f);              // expand outward
                var px = MathF.Cos(angle) * distance;
                var py = MathF.Sin(angle) * distance;

                var dotRadius = receptorSize * (0.07f - pt * 0.05f);             // shrink as they travel
                dotRadius = Math.Max(dotRadius, 1.5f);

                // Alternate white and pale-blue sparkles for variety
                canvas.FillColor = (i % 2 == 0)
                    ? Colors.White.WithAlpha(particleAlpha)
                    : Color.FromArgb("#B8D8FF").WithAlpha(particleAlpha);

                canvas.FillEllipse(px - dotRadius, py - dotRadius, dotRadius * 2f, dotRadius * 2f);
            }
        }
    }
}
