using CommunityToolkit.Maui.Views;
using System.Diagnostics;
using System.Text.Json;
using TapItUp.Game;

namespace TapItUp;

[QueryProperty(nameof(SongDataJson), "songData")]
public partial class GamePage : ContentPage
{
    private static readonly string[] LaneGlyphs = ["↙", "↖", "●", "↗", "↘"];
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb("#00C2FF"),
        Color.FromArgb("#FF2D2D"),
        Color.FromArgb("#FFE45E"),
        Color.FromArgb("#FF2D2D"),
        Color.FromArgb("#00C2FF")
    ];

    private readonly RhythmGameEngine _engine = new();
    private readonly NoteFieldDrawable _noteFieldDrawable;
    private NoteFieldDrawable? _landscapeNoteFieldDrawable;
    private readonly Stopwatch _playbackTimer = new();
    private double _playbackStartOffsetSeconds;
    private double _notesDisplayOffsetSeconds = 0;
    private SscSong? _song;
    private SscChart? _chart;
    private bool _isGameLoaded;

    // ── Animation state ──────────────────────────────────────────────────────
    private int _lastAnimatedCombo = -1;
    private string _lastAnimatedJudgment = "";

    // Track which views are currently visible to avoid unnecessary redraws
    private bool _isPortraitMode = true;

    /// <summary>
    /// When false all UI animations (judgment pop, combo punch, screen shake,
    /// beat pulse) are skipped to keep the frame-rate stable on low-end devices.
    /// Defaults to false on Android, true everywhere else.
    /// </summary>
    public bool AnimationsEnabled { get; set; } =
        DeviceInfo.Platform != DevicePlatform.Android;

    // ── Health meter state ───────────────────────────────────────────────────
    private double _health = 0.30d;
    private double _lastBeatTime = 0d;
    private bool _beatPulseActive = false;

    private const double HealthGainPerfect = 0.010d;
    private const double HealthGainGreat = 0.007d;
    private const double HealthGainGood = 0.003d;
    private const double HealthDrainBad = 0.020d;
    private const double HealthDrainMiss = 0.045d;

    private ProgressBar? _portraitHealthBar;
    private ProgressBar? _landscapeHealthBar;

    public string SongDataJson { get; set; } = "";

    public GamePage()
    {
        InitializeComponent();

        _noteFieldDrawable = new NoteFieldDrawable(_engine);
        NoteFieldView.Drawable = _noteFieldDrawable;

        _landscapeNoteFieldDrawable = new NoteFieldDrawable(_engine);
        LandscapeNoteFieldView.Drawable = _landscapeNoteFieldDrawable;

        BuildPad(PortraitPad);
        BuildPad(LandscapeLeftPad);
        BuildPad(LandscapeRightPad);

        SizeChanged += OnPageSizeChanged;

        SongMediaElement.MediaOpened += (s, e) =>
            System.Diagnostics.Debug.WriteLine("🎵 MediaElement: Media opened successfully");

        SongMediaElement.MediaFailed += (s, e) =>
            System.Diagnostics.Debug.WriteLine($"❌ MediaElement: Media failed - {e.ErrorMessage}");

        SongMediaElement.MediaEnded += (s, e) =>
            System.Diagnostics.Debug.WriteLine("🎵 MediaElement: Media playback ended");

        BuildHealthMeters();
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), OnFrame);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!string.IsNullOrEmpty(SongDataJson) && !_isGameLoaded)
        {
            await LoadSongFromData();
        }

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => FocusKeyCatcher());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopGame();
    }

    private static readonly HttpClient _http = new();
    private Stream? _audioStream;
    private string? _audioTempFile;

    private async Task LoadSongFromData()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"🎮 Raw SongDataJson: {SongDataJson}");

            GameStartData? gameStartData = null;
            try
            {
                gameStartData = JsonSerializer.Deserialize<GameStartData>(SongDataJson);
                System.Diagnostics.Debug.WriteLine("✅ Successfully deserialized as GameStartData");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Failed to deserialize as GameStartData: {ex.Message}");
            }

            if (gameStartData != null)
            {
                _song = gameStartData.Song;
                _chart = gameStartData.Chart;

                var startingBpm = gameStartData.Song.BpmChanges.Count > 0
                    ? gameStartData.Song.BpmChanges.OrderBy(b => b.Beat).First().Bpm
                    : 150d;
                var scrollMultiplier = gameStartData.Av / startingBpm;

                System.Diagnostics.Debug.WriteLine($"🎮 AV: {gameStartData.Av}, Starting BPM: {startingBpm:F1}, Scroll multiplier: {scrollMultiplier:F3}");
                _noteFieldDrawable.ScrollSpeedMultiplier = scrollMultiplier;
                if (_landscapeNoteFieldDrawable != null)
                    _landscapeNoteFieldDrawable.ScrollSpeedMultiplier = scrollMultiplier;

                System.Diagnostics.Debug.WriteLine($"🎮 Setting note skin to: {gameStartData.NoteSkin}");
                _noteFieldDrawable.NoteSkin = gameStartData.NoteSkin;
                if (_landscapeNoteFieldDrawable != null)
                    _landscapeNoteFieldDrawable.NoteSkin = gameStartData.NoteSkin;

                _engine.JudgmentDifficulty = gameStartData.JudgmentDifficulty;
                AnimationsEnabled = gameStartData.AnimationsEnabled;

                System.Diagnostics.Debug.WriteLine($"   Song: {_song.Title}");
                System.Diagnostics.Debug.WriteLine($"   Artist: {_song.Artist}");
                System.Diagnostics.Debug.WriteLine($"   Chart: {_chart.Difficulty} {_chart.Meter}");
                System.Diagnostics.Debug.WriteLine($"   Chart has {_chart.Notes.Count} notes");

                if (!string.IsNullOrWhiteSpace(gameStartData.RemoteAudioUrl))
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"🎵 Loading audio: {gameStartData.RemoteAudioUrl}");
                        if (Uri.IsWellFormedUriString(gameStartData.RemoteAudioUrl, UriKind.Absolute))
                        {
                            System.Diagnostics.Debug.WriteLine("🌐 Streaming MP3 from remote URL...");
                            _audioStream = await _http.GetStreamAsync(gameStartData.RemoteAudioUrl);

                            var fileName = Path.GetFileName(new Uri(gameStartData.RemoteAudioUrl).LocalPath);
                            var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);

                            await using (var fileStream = File.Create(tempPath))
                            {
                                await _audioStream.CopyToAsync(fileStream);
                            }

                            System.Diagnostics.Debug.WriteLine($"✅ Audio cached to: {tempPath}");
                            _audioTempFile = tempPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"❌ Failed to load audio: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"🎮 AV: {gameStartData.Av}");
                _noteFieldDrawable.Av = gameStartData.Av;
                if (_landscapeNoteFieldDrawable != null)
                    _landscapeNoteFieldDrawable.Av = gameStartData.Av;

                await LoadSongAndChart();
                return;
            }

            var gameData = JsonSerializer.Deserialize<GamePageData>(SongDataJson);

            if (gameData is null)
            {
                System.Diagnostics.Debug.WriteLine("❌ Failed to deserialize as both GameStartData and GamePageData");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"🎮 Loading legacy game data:");
            System.Diagnostics.Debug.WriteLine($"   Song: {gameData.SongTitle}");
            System.Diagnostics.Debug.WriteLine($"   Source: {gameData.SongSourcePath}");
            System.Diagnostics.Debug.WriteLine($"   Music: {gameData.SongMusicPath}");

            if (string.IsNullOrEmpty(gameData.SongSourcePath) || gameData.SongSourcePath.StartsWith("Songs/"))
            {
                var songPath = gameData.SongSourcePath ?? "phoenix_demo.ssc";
                System.Diagnostics.Debug.WriteLine($"📄 Loading embedded song: {songPath}");

                await using var stream = await FileSystem.OpenAppPackageFileAsync(songPath);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                _song = SscParser.Parse(content, songPath);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"📂 Loading external song: {gameData.SongSourcePath}");
                var content = await File.ReadAllTextAsync(gameData.SongSourcePath);
                _song = SscParser.Parse(content, gameData.SongSourcePath);
            }

            if (_song is null || gameData.ChartIndex < 0 || gameData.ChartIndex >= _song.Charts.Count)
            {
                System.Diagnostics.Debug.WriteLine("❌ Song loading failed or invalid chart index");
                return;
            }

            _chart = _song.Charts[gameData.ChartIndex];
            await LoadSongAndChart();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Game loading error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            await DisplayAlert("Error loading game", ex.Message, "OK");
            await Shell.Current.GoToAsync("..");
        }
    }

    private async Task LoadSongAndChart()
    {
        if (_song == null || _chart == null) return;

        try
        {
            System.Diagnostics.Debug.WriteLine($"🎵 Loading song and chart:");
            System.Diagnostics.Debug.WriteLine($"   Song: {_song.Title} by {_song.Artist}");
            System.Diagnostics.Debug.WriteLine($"   Chart: {_chart.Difficulty} {_chart.Meter}");
            System.Diagnostics.Debug.WriteLine($"   Notes: {_chart.Notes.Count}");

            _engine.Load(_song, _chart);

            BuildPad(LandscapeLeftPad);
            BuildPad(LandscapeRightPad);

            BackgroundPreview.Source = TryCreateBackground(_song);
            LandscapeBackgroundPreview.Source = BackgroundPreview.Source;

            RefreshHud();
            NoteFieldView.Invalidate();

            System.Diagnostics.Debug.WriteLine($"✅ Song loaded successfully");
            System.Diagnostics.Debug.WriteLine($"   Music path: {_song.MusicPath}");
            System.Diagnostics.Debug.WriteLine($"   Source path: {_song.SourcePath}");

            _isGameLoaded = true;
            StartGame();
            FocusKeyCatcher();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Error in LoadSongAndChart: {ex.Message}");
            await DisplayAlert("Error", $"Failed to load song and chart: {ex.Message}", "OK");
        }
    }

    private void StartGame()
    {
        _engine.Start();
        PlaySongAudio();

        _playbackStartOffsetSeconds = Math.Min(0d,
            (_engine.Chart?.Notes.FirstOrDefault()?.TimeSeconds ?? 0d) - 1.2d);

        _engine.Update(_playbackStartOffsetSeconds - _notesDisplayOffsetSeconds);

        _playbackTimer.Restart();
        RefreshHud();
    }

    private void StopGame()
    {
        _engine.Stop();
        SongMediaElement.Stop();
        _playbackTimer.Stop();
        _playbackStartOffsetSeconds = 0d;
    }

    // ── HUD ──────────────────────────────────────────────────────────────────

    private static bool IsStartupMessage(string judgmentText) => judgmentText switch
    {
        "GO" => true,
        "READY" => true,
        "SELECT SONG" => true,
        _ => false
    };

    private void RefreshHud()
    {
        var judgmentText = _engine.LastJudgmentText;
        var countsText = $"PERFECT {_engine.Counts[HitJudgment.Perfect]} • GREAT {_engine.Counts[HitJudgment.Great]} • GOOD {_engine.Counts[HitJudgment.Good]} • BAD {_engine.Counts[HitJudgment.Bad]} • MISS {_engine.Counts[HitJudgment.Miss]}";

        PortraitCountsLabel.Text = countsText;
        LandscapeCountsLabel.Text = countsText;

        var shouldShowJudgment = !IsStartupMessage(judgmentText);

        if (shouldShowJudgment && judgmentText != _lastAnimatedJudgment)
        {
            _lastAnimatedJudgment = judgmentText;

            var judgmentColor = judgmentText switch
            {
                "PERFECT" => Color.FromArgb("#87CEEB"),
                "GREAT" => Color.FromArgb("#00FF00"),
                "GOOD" => Color.FromArgb("#FFFF00"),
                "BAD" => Color.FromArgb("#8A2BE2"),
                "MISS" => Color.FromArgb("#FF0000"),
                _ => Color.FromArgb("#FFE76A")
            };

            // Animate only the visible label to avoid double-work
            var visibleLabel = _isPortraitMode ? CenterJudgmentLabel : LandscapeCenterJudgmentLabel;
            var hiddenLabel = _isPortraitMode ? LandscapeCenterJudgmentLabel : CenterJudgmentLabel;

            visibleLabel.Text = judgmentText;
            visibleLabel.TextColor = judgmentColor;
            visibleLabel.IsVisible = true;

            // Update hidden label without animation
            hiddenLabel.Text = judgmentText;
            hiddenLabel.TextColor = judgmentColor;
            hiddenLabel.IsVisible = true;

            if (AnimationsEnabled)
            {
                _ = AnimateJudgmentAsync(visibleLabel).ContinueWith(_ =>
                {
                    if (_lastAnimatedJudgment == judgmentText)
                        _lastAnimatedJudgment = "";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else
            {
                // No animation: auto-hide after a short fixed delay
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        visibleLabel.IsVisible = false;
                        if (_lastAnimatedJudgment == judgmentText)
                            _lastAnimatedJudgment = "";
                    });
                });
            }

            if (Enum.TryParse<HitJudgment>(judgmentText, ignoreCase: true, out var parsedJudgment))
            {
                ApplyHealthDelta(parsedJudgment);
                UpdateHealthBar();

                if (AnimationsEnabled && parsedJudgment == HitJudgment.Miss)
                    _ = ShakeScreenAsync(intensity: 3d, durationMs: 160);
            }
        }
        else if (!shouldShowJudgment)
        {
            CenterJudgmentLabel.IsVisible = false;
            LandscapeCenterJudgmentLabel.IsVisible = false;
        }

        var showMissCombo = _engine.MissCombo >= 4;
        var showGoodCombo = !showMissCombo && _engine.Combo >= 4;
        var showCombo = showMissCombo || showGoodCombo;

        var comboNumber = showMissCombo ? _engine.MissCombo : _engine.Combo;
        var comboColor = showMissCombo ? Color.FromArgb("#FF0000") : Colors.White;

        CenterComboNumberLabel.IsVisible = showCombo;
        CenterComboTextLabel.IsVisible = showCombo;
        LandscapeCenterComboNumberLabel.IsVisible = showCombo;
        LandscapeCenterComboTextLabel.IsVisible = showCombo;

        if (showCombo)
        {
            var isMilestone = comboNumber % 100 == 0 && comboNumber > 0;
            var comboChanged = comboNumber != _lastAnimatedCombo;
            _lastAnimatedCombo = comboNumber;

            // Only animate visible combo labels
            var (visibleNumLabel, visibleTextLabel, hiddenNumLabel, hiddenTextLabel) = _isPortraitMode
                ? (CenterComboNumberLabel, CenterComboTextLabel, LandscapeCenterComboNumberLabel, LandscapeCenterComboTextLabel)
                : (LandscapeCenterComboNumberLabel, LandscapeCenterComboTextLabel, CenterComboNumberLabel, CenterComboTextLabel);

            visibleNumLabel.Text = comboNumber.ToString();
            visibleNumLabel.TextColor = comboColor;
            visibleTextLabel.Text = "COMBO";
            visibleTextLabel.TextColor = comboColor;

            // Update hidden labels without animation
            hiddenNumLabel.Text = comboNumber.ToString();
            hiddenNumLabel.TextColor = comboColor;
            hiddenTextLabel.Text = "COMBO";
            hiddenTextLabel.TextColor = comboColor;

            if (comboChanged && AnimationsEnabled)
                _ = AnimateComboHitAsync(visibleNumLabel, visibleTextLabel, comboColor, isMilestone);

            if (isMilestone && AnimationsEnabled)
                _ = ShakeScreenAsync(intensity: 2d, durationMs: 140);
        }
        else
        {
            _lastAnimatedCombo = -1;
        }
    }

    // ── Animations ───────────────────────────────────────────────────────────

    /// <summary>
    /// Judgment pop: slight upward float + bounce scale + fast fade out over ~300 ms.
    /// Optimized to complete faster and with fewer intermediate steps.
    /// </summary>
    private static async Task AnimateJudgmentAsync(Label label)
    {
        label.Opacity = 1d;
        label.TranslationY = 0d;
        label.Scale = 0.75d;

        // Phase 1 & 2 combined (~100 ms): snap up + scale punch + bounce back
        await Task.WhenAll(
            label.TranslateTo(0d, -8d, 100, Easing.CubicOut),
            label.ScaleTo(1.10d, 100, Easing.CubicOut));

        // Phase 3 (~200 ms): float upward while fading out
        await Task.WhenAll(
            label.TranslateTo(0d, -22d, 200, Easing.CubicIn),
            label.FadeTo(0d, 200, Easing.CubicIn));

        label.IsVisible = false;
        label.Opacity = 1d;
        label.TranslationY = 0d;
        label.Scale = 1d;
    }

    /// <summary>
    /// Combo hit punch: scale 1 → 1.15 → 1 quickly.
    /// At milestones (multiples of 100) a bigger scale + temporary gold glow is applied.
    /// </summary>
    private static async Task AnimateComboHitAsync(Label numberLabel, Label textLabel, Color baseColor, bool isMilestone)
    {
        var peakScale = isMilestone ? 1.35d : 1.15d;
        var punchMs = (uint)(isMilestone ? 90 : 60);
        var snapbackMs = (uint)(isMilestone ? 110 : 80);
        var glowColor = Color.FromArgb("#FFE45E");

        if (isMilestone)
        {
            numberLabel.TextColor = glowColor;
            textLabel.TextColor = glowColor;
        }

        await Task.WhenAll(
            numberLabel.ScaleTo(peakScale, punchMs, Easing.CubicOut),
            textLabel.ScaleTo(peakScale * 0.85d, punchMs, Easing.CubicOut));

        await Task.WhenAll(
            numberLabel.ScaleTo(1d, snapbackMs, Easing.SpringOut),
            textLabel.ScaleTo(1d, snapbackMs, Easing.SpringOut));

        if (isMilestone)
        {
            numberLabel.TextColor = baseColor;
            textLabel.TextColor = baseColor;
        }
    }

    private bool _isShaking = false;

    /// <summary>
    /// Subtle arcade screen nudge. A single soft left-right oscillation that
    /// decays quickly. Much gentler than a full earthquake-style shake.
    /// </summary>
    private async Task ShakeScreenAsync(double intensity = 3d, int durationMs = 180)
    {
        if (_isShaking) return;
        _isShaking = true;

        var target = MainGrid;
        var stepMs = (uint)28;
        var steps = durationMs / (int)stepMs;

        for (var i = 0; i < steps; i++)
        {
            // Sine wave: oscillates once, decays linearly
            var progress = (double)i / steps;
            var decay = 1d - progress;
            var offset = intensity * decay * Math.Sin(progress * Math.PI * 2d);
            await target.TranslateTo(offset, 0d, stepMs, Easing.Linear);
        }

        target.TranslationX = 0d;
        _isShaking = false;
    }

    // ── Health meter ────────────────────────────────────────────────────────
    private BoxView? _portraitHealthMask;
    private BoxView? _landscapeHealthMask;

    private void BuildHealthMeters()
    {
        _portraitHealthBar = null;
        _landscapeHealthBar = null;

        PortraitHealthContainer.Content = CreateRainbowHealthBar(out _portraitHealthMask);
        LandscapeHealthContainer.Content = CreateRainbowHealthBar(out _landscapeHealthMask);

        UpdateHealthBar();
    }

    /// <summary>
    /// Builds a rainbow gradient bar with a dark right-side mask that reveals
    /// the gradient left-to-right as health increases.
    /// </summary>
    private static Grid CreateRainbowHealthBar(out BoxView mask)
    {
        var rainbow = new BoxView
        {
            HeightRequest = 10,
            HorizontalOptions = LayoutOptions.Fill,
            CornerRadius = 5,
            Opacity = 0.95d,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                [
                    new GradientStop { Color = Color.FromArgb("#FF2D2D"), Offset = 0.00f },
                    new GradientStop { Color = Color.FromArgb("#FF8C00"), Offset = 0.17f },
                    new GradientStop { Color = Color.FromArgb("#FFE45E"), Offset = 0.34f },
                    new GradientStop { Color = Color.FromArgb("#00FF88"), Offset = 0.50f },
                    new GradientStop { Color = Color.FromArgb("#00C2FF"), Offset = 0.67f },
                    new GradientStop { Color = Color.FromArgb("#4169E1"), Offset = 0.83f },
                    new GradientStop { Color = Color.FromArgb("#9B30FF"), Offset = 1.00f },
                ]
            }
        };

        mask = new BoxView
        {
            HeightRequest = 10,
            HorizontalOptions = LayoutOptions.End,
            CornerRadius = new CornerRadius(0, 5, 0, 5),
            BackgroundColor = Color.FromArgb("#CC090212"),
        };

        var track = new BoxView
        {
            HeightRequest = 10,
            HorizontalOptions = LayoutOptions.Fill,
            CornerRadius = 5,
            BackgroundColor = Color.FromArgb("#1A1A2E"),
        };

        var container = new Grid
        {
            HeightRequest = 10,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(8, 0),
        };

        container.Add(track);
        container.Add(rainbow);
        container.Add(mask);

        return container;
    }

    private void ApplyHealthDelta(HitJudgment judgment)
    {
        _health += judgment switch
        {
            HitJudgment.Perfect => HealthGainPerfect,
            HitJudgment.Great => HealthGainGreat,
            HitJudgment.Good => HealthGainGood,
            HitJudgment.Bad => -HealthDrainBad,
            HitJudgment.Miss => -HealthDrainMiss,
            _ => 0d
        };

        _health = Math.Clamp(_health, 0d, 1d);
    }

    private void UpdateHealthBar()
    {
        SetMaskWidth(_portraitHealthMask, PortraitHealthContainer);
        SetMaskWidth(_landscapeHealthMask, LandscapeHealthContainer);
    }

    private void SetMaskWidth(BoxView? maskView, ContentView container)
    {
        if (maskView == null) return;

        var containerWidth = container.Width;
        if (containerWidth <= 0)
        {
            container.SizeChanged += (_, _) => SetMaskWidth(maskView, container);
            return;
        }

        var innerWidth = Math.Max(0, containerWidth - 16);
        var emptyFraction = 1d - _health;
        maskView.WidthRequest = innerWidth * emptyFraction;
    }

    private void TickBeatPulse(double currentTimeSeconds)
    {
        var bpm = _engine.CurrentBpm;
        if (bpm <= 0d || !_engine.IsPlaying) return;

        var secondsPerBeat = 60d / bpm;
        var nextBeat = _lastBeatTime + secondsPerBeat;

        if (currentTimeSeconds >= nextBeat)
        {
            _lastBeatTime = currentTimeSeconds;

            if (!_beatPulseActive)
                _ = PulseBeatAsync();
        }
    }

    private async Task PulseBeatAsync()
    {
        _beatPulseActive = true;

        if (AnimationsEnabled)
        {
            var containers = new View[] { PortraitHealthContainer, LandscapeHealthContainer };

            foreach (var c in containers)
                _ = c.ScaleTo(1.05d, 55, Easing.CubicOut);

            await Task.Delay(55);

            foreach (var c in containers)
                _ = c.ScaleTo(1.00d, 90, Easing.SpringOut);

            await Task.Delay(90);
        }
        else
        {
            await Task.Delay(145);
        }

        _beatPulseActive = false;
    }

    // ── Game loop ────────────────────────────────────────────────────────────

    private bool OnFrame()
    {
        if (!IsVisible || !_isGameLoaded) return true;

        var elapsedSeconds = _playbackTimer.IsRunning
            ? _playbackTimer.Elapsed.TotalSeconds + _playbackStartOffsetSeconds
            : _engine.CurrentTimeSeconds;

        var engineTime = elapsedSeconds - _notesDisplayOffsetSeconds;
        if (engineTime < 0d) engineTime = 0d;

        var previousScore = _engine.Score;
        var previousCombo = _engine.Combo;
        var previousMissCombo = _engine.MissCombo;
        var previousJudgment = _engine.LastJudgmentText;
        var previousTimeSeconds = _engine.CurrentTimeSeconds;
        var wasPlaying = _engine.IsPlaying;

        _engine.Update(engineTime);

        TickBeatPulse(engineTime);

        var hudChanged = previousScore != _engine.Score ||
                         previousCombo != _engine.Combo ||
                         previousMissCombo != _engine.MissCombo ||
                         previousJudgment != _engine.LastJudgmentText;

        var timeChanged = Math.Abs(_engine.CurrentTimeSeconds - previousTimeSeconds) > 0.008;

        if (hudChanged)
            RefreshHud();

        // Only invalidate the currently visible view
        if (timeChanged || hudChanged)
        {
            if (_isPortraitMode)
                NoteFieldView.Invalidate();
            else
                LandscapeNoteFieldView?.Invalidate();
        }

        if (wasPlaying && !_engine.IsPlaying)
        {
            System.Diagnostics.Debug.WriteLine("🎮 Game ended - navigating to results");
            System.Diagnostics.Debug.WriteLine($"   Final Score: {_engine.Score}");
            System.Diagnostics.Debug.WriteLine($"   Final Grade: {_engine.Grade}");

            _playbackTimer.Stop();

            var resultsData = new GameResultsData
            {
                SongTitle = _song?.Title ?? "Unknown Song",
                SongArtist = _song?.Artist ?? "Unknown Artist",
                ChartDifficulty = _chart?.Difficulty ?? "Unknown",
                ChartStepType = _chart?.StepType ?? "",
                ChartMeter = _chart?.Meter ?? 0,
                Score = _engine.Score,
                Grade = _engine.Grade,
                Plate = _engine.Plate,
                Accuracy = _engine.AccuracyPercent,
                MaxCombo = _engine.MaxCombo,
                PerfectCount = _engine.Counts[HitJudgment.Perfect],
                GreatCount = _engine.Counts[HitJudgment.Great],
                GoodCount = _engine.Counts[HitJudgment.Good],
                BadCount = _engine.Counts[HitJudgment.Bad],
                MissCount = _engine.Counts[HitJudgment.Miss]
            };

            var resultsJson = JsonSerializer.Serialize(resultsData);
            var encodedResults = Uri.EscapeDataString(resultsJson);

            System.Diagnostics.Debug.WriteLine($"🎮 Navigating to ResultsPage with data length: {encodedResults.Length}");

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("🎮 Starting navigation...");
                    await Shell.Current.GoToAsync($"ResultsPage?resultsData={encodedResults}");
                    System.Diagnostics.Debug.WriteLine("🎮 Navigation completed");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ Navigation failed: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
                }
            });
        }

        return true;
    }

    // ── Audio ────────────────────────────────────────────────────────────────

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        StopGame();
        await Shell.Current.GoToAsync("..");
    }

    private async void PlaySongAudio()
    {
        SongMediaElement.Stop();
        SongMediaElement.Source = null;
        await Task.Delay(50);

        if (!string.IsNullOrEmpty(_audioTempFile) && File.Exists(_audioTempFile))
        {
            System.Diagnostics.Debug.WriteLine($"🎵 Using cached remote audio: {_audioTempFile}");
            SongMediaElement.Source = MediaSource.FromUri(new Uri(_audioTempFile).AbsoluteUri);
            SongMediaElement.Play();
            return;
        }

        if (_song is null || string.IsNullOrWhiteSpace(_song.MusicPath)) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(_song.SourcePath) && _song.SourcePath.StartsWith("Songs/"))
            {
                var baseDir = Path.GetDirectoryName(_song.SourcePath);
                if (baseDir is not null)
                {
                    var embeddedAudioPath = Path.Combine(baseDir, _song.MusicPath.Replace('/', Path.DirectorySeparatorChar))
                        .Replace('\\', '/');

                    System.Diagnostics.Debug.WriteLine($"🎵 Trying embedded audio: {embeddedAudioPath}");
                    try
                    {
                        await using var sourceStream = await FileSystem.OpenAppPackageFileAsync(embeddedAudioPath);
                        var tempDir = GetReliableTempDirectory();
                        var tempFile = Path.Combine(tempDir,
                            $"{Path.GetFileNameWithoutExtension(_song.MusicPath)}_{DateTime.Now.Ticks}{Path.GetExtension(_song.MusicPath)}");

                        await using var targetStream = File.Create(tempFile);
                        await sourceStream.CopyToAsync(targetStream);

                        if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"🎵 Playing embedded audio from: {tempFile}");
                            SongMediaElement.Source = MediaSource.FromUri(new Uri(tempFile).AbsoluteUri);
                            SongMediaElement.Play();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"❌ Failed to copy embedded audio: {ex.Message}");
                    }

                    await TryAlternativeAudioLoading(embeddedAudioPath);
                    return;
                }
            }

#if ANDROID
            if (!string.IsNullOrWhiteSpace(_song.SongDocumentUri))
            {
                System.Diagnostics.Debug.WriteLine($"🎵 Android SAF path — scanning song folder for audio");
                try
                {
                    var context = Android.App.Application.Context;
                    var treeUri = Android.Net.Uri.Parse(_song.SongDocumentUri);
                    if (treeUri != null)
                    {
                        var songDocId = Android.Provider.DocumentsContract.GetDocumentId(treeUri)
                                        ?? Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);

                        if (songDocId != null)
                        {
                            var childrenUri = Android.Provider.DocumentsContract
                                .BuildChildDocumentsUriUsingTree(treeUri, songDocId);

                            var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { ".mp3", ".ogg", ".wav", ".flac", ".m4a" };

                            Android.Database.ICursor? cursor = null;
                            string? audioDocId = null;
                            string? audioFileName = null;
                            try
                            {
                                cursor = context.ContentResolver!.Query(
                                    childrenUri!,
                                    [
                                        Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                                        Android.Provider.DocumentsContract.Document.ColumnDisplayName,
                                    ],
                                    null, null, null);

                                while (cursor?.MoveToNext() == true)
                                {
                                    var docId = cursor.GetString(0);
                                    var name = cursor.GetString(1);
                                    if (docId != null && name != null &&
                                        audioExtensions.Contains(Path.GetExtension(name)))
                                    {
                                        if (audioDocId == null ||
                                            name.Equals(Path.GetFileName(_song.MusicPath), StringComparison.OrdinalIgnoreCase))
                                        {
                                            audioDocId = docId;
                                            audioFileName = name;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                cursor?.Close();
                            }

                            if (audioDocId != null)
                            {
                                var audioUri = Android.Provider.DocumentsContract
                                    .BuildDocumentUriUsingTree(treeUri, audioDocId);

                                System.Diagnostics.Debug.WriteLine($"🎵 Found SAF audio: {audioFileName} ({audioDocId})");

                                var tempDir = GetReliableTempDirectory();
                                var tempFile = Path.Combine(tempDir,
                                    $"{Path.GetFileNameWithoutExtension(audioFileName!)}_{DateTime.Now.Ticks}{Path.GetExtension(audioFileName)}");

                                await using (var inStream = context.ContentResolver!.OpenInputStream(audioUri!))
                                await using (var outStream = File.Create(tempFile))
                                {
                                    await inStream!.CopyToAsync(outStream);
                                }

                                if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 0)
                                {
                                    System.Diagnostics.Debug.WriteLine($"🎵 Playing SAF audio from cache: {tempFile}");
                                    SongMediaElement.Source = MediaSource.FromUri(new Uri(tempFile).AbsoluteUri);
                                    SongMediaElement.Play();
                                    return;
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"❌ No audio file found in SAF song folder");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ SAF audio loading failed: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"   {ex.StackTrace}");
                }

                return;
            }
#endif

            if (!string.IsNullOrWhiteSpace(_song.SourcePath))
            {
                var baseDir = Path.GetDirectoryName(_song.SourcePath);
                if (baseDir is not null)
                {
                    var candidate = Path.Combine(baseDir, _song.MusicPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        System.Diagnostics.Debug.WriteLine($"🎵 Playing external audio: {candidate}");
                        SongMediaElement.Source = MediaSource.FromUri(new Uri(candidate).AbsoluteUri);
                        SongMediaElement.Play();
                        return;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"❌ Could not find audio file: {_song.MusicPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Audio loading error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   {ex.StackTrace}");
        }
    }

    private static string GetReliableTempDirectory()
    {
        try
        {
            var defaultCacheDir = FileSystem.Current.CacheDirectory;
            System.Diagnostics.Debug.WriteLine($"📁 Default cache directory: {defaultCacheDir}");

            if (!defaultCacheDir.Contains("User Name", StringComparison.OrdinalIgnoreCase) &&
                !defaultCacheDir.Contains("UserName", StringComparison.OrdinalIgnoreCase))
            {
                var audioDir = Path.Combine(defaultCacheDir, "audio");
                Directory.CreateDirectory(audioDir);
                return audioDir;
            }

            System.Diagnostics.Debug.WriteLine("⚠️ Default cache directory contains placeholder username, using fallback");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Failed to access default cache directory: {ex.Message}");
        }

        var fallbackDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapItUp", "audio"),
            Path.Combine(Path.GetTempPath(), "TapItUp", "audio"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TapItUp", "audio")
        };

        foreach (var dir in fallbackDirs)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"📁 Trying fallback directory: {dir}");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Failed to create fallback directory {dir}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine("📁 Using system temp directory as last resort");
        return Path.GetTempPath();
    }

    private async Task TryAlternativeAudioLoading(string embeddedAudioPath)
    {
        try
        {
            var msAppxUri = $"ms-appx:///{embeddedAudioPath}";
            System.Diagnostics.Debug.WriteLine($"🔄 Trying ms-appx URI: {msAppxUri}");

            SongMediaElement.Source = MediaSource.FromUri(msAppxUri);
            SongMediaElement.Play();

            await Task.Delay(500);
            if (SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Playing ||
                SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Buffering)
            {
                System.Diagnostics.Debug.WriteLine("✅ ms-appx URI method worked!");
                return;
            }

            System.Diagnostics.Debug.WriteLine("🔄 Trying embedded resource method...");
            SongMediaElement.Source = MediaSource.FromResource(embeddedAudioPath);
            SongMediaElement.Play();

            await Task.Delay(500);
            if (SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Playing ||
                SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Buffering)
            {
                System.Diagnostics.Debug.WriteLine("✅ Embedded resource method worked!");
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Alternative audio loading failed: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine("❌ All audio loading methods failed");
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        var isLandscape = Width > Height;
        _isPortraitMode = !isLandscape;

        PortraitLayout.IsVisible = !isLandscape;
        LandscapeLayout.IsVisible = isLandscape;

        if (isLandscape)
        {
            if (LandscapeNoteFieldView.Drawable == null)
            {
                _landscapeNoteFieldDrawable = new NoteFieldDrawable(_engine);
                LandscapeNoteFieldView.Drawable = _landscapeNoteFieldDrawable;
            }

            _noteFieldDrawable.IsLandscapeMode = true;
            if (_landscapeNoteFieldDrawable != null)
            {
                _landscapeNoteFieldDrawable.IsLandscapeMode = true;
                _landscapeNoteFieldDrawable.NoteSkin = _noteFieldDrawable.NoteSkin;
                _landscapeNoteFieldDrawable.ScrollSpeedMultiplier = _noteFieldDrawable.ScrollSpeedMultiplier;
                _landscapeNoteFieldDrawable.Av = _noteFieldDrawable.Av;
            }

            LandscapeCenterGrid.WidthRequest = Width * 0.25;
            LandscapeBackgroundPreview.Source = BackgroundPreview.Source;
        }
        else
        {
            _noteFieldDrawable.IsLandscapeMode = false;
            if (_landscapeNoteFieldDrawable != null)
                _landscapeNoteFieldDrawable.IsLandscapeMode = false;
        }

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () => FocusKeyCatcher());
    }

    // ── Pad building ─────────────────────────────────────────────────────────

    private void BuildPad(Grid grid)
    {
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();

        var isLandscapePad = grid == LandscapeLeftPad || grid == LandscapeRightPad;

        if (isLandscapePad)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            grid.RowSpacing = 4;
            grid.ColumnSpacing = 4;
            grid.HorizontalOptions = LayoutOptions.Center;
            grid.VerticalOptions = LayoutOptions.End;
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        var isRightPad = grid == LandscapeRightPad;
        var isDouble = _engine.IsDoubleChart;
        var laneOffset = isRightPad && isDouble ? 5 : 0;

        AddPadButton(grid, lane: laneOffset + 1, row: 0, column: 0);
        AddPadButton(grid, lane: laneOffset + 3, row: 0, column: 2);
        AddPadButton(grid, lane: laneOffset + 2, row: 1, column: 1);
        AddPadButton(grid, lane: laneOffset + 0, row: 2, column: 0);
        AddPadButton(grid, lane: laneOffset + 4, row: 2, column: 2);
    }

    private void AddPadButton(Grid grid, int lane, int row, int column)
    {
        var isLandscapePad = grid == LandscapeLeftPad || grid == LandscapeRightPad;
        var laneColorIndex = lane % 5;

        if (isLandscapePad)
        {
            var button = new Button
            {
                Text = string.Empty,
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = LaneColors[laneColorIndex].WithAlpha(0.4f),
                TextColor = Colors.Black,
                CornerRadius = 2,
                Margin = new Thickness(0),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.End,
                WidthRequest = 74,
                HeightRequest = 74
            };

            button.Pressed += (_, _) => { System.Diagnostics.Debug.WriteLine($"🎮 UI Button Pressed event fired for lane {lane}"); HandleLaneInput(lane); };
            button.Released += (_, _) => { System.Diagnostics.Debug.WriteLine($"🎮 UI Button Released event fired for lane {lane}"); _engine.HandleLaneRelease(lane); };

            grid.Add(button, column, row);
            return;
        }

        var baseSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 75 : 90;
        var baseFontSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 35 : 40;

        var portraitButton = new Button
        {
            Text = LaneGlyphs[laneColorIndex],
            FontSize = baseFontSize,
            FontAttributes = FontAttributes.Bold,
            BackgroundColor = LaneColors[laneColorIndex].WithAlpha(0.4f),
            TextColor = Colors.White,
            CornerRadius = 2,
            Margin = new Thickness(0),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = baseSize,
            HeightRequest = baseSize
        };

        portraitButton.Pressed += (_, _) => { System.Diagnostics.Debug.WriteLine($"🎮 UI Button Pressed event fired for lane {lane}"); HandleLaneInput(lane); };
        portraitButton.Released += (_, _) => { System.Diagnostics.Debug.WriteLine($"🎮 UI Button Released event fired for lane {lane}"); _engine.HandleLaneRelease(lane); };

        grid.Add(portraitButton, column, row);
    }

    private void HandleLaneInput(int lane)
    {
        var previousScore = _engine.Score;
        var previousCombo = _engine.Combo;
        var previousMissCombo = _engine.MissCombo;
        var previousJudgment = _engine.LastJudgmentText;

        _engine.HandleLaneHit(lane);

        var somethingChanged = previousScore != _engine.Score ||
                               previousCombo != _engine.Combo ||
                               previousMissCombo != _engine.MissCombo ||
                               previousJudgment != _engine.LastJudgmentText;

        if (somethingChanged)
        {
            RefreshHud();
            // Only invalidate the currently visible view
            if (_isPortraitMode)
                NoteFieldView.Invalidate();
            else
                LandscapeNoteFieldView?.Invalidate();
        }

        FocusKeyCatcher();
    }

    private static ImageSource? TryCreateBackground(SscSong song)
    {
        if (string.IsNullOrWhiteSpace(song.SourcePath) || string.IsNullOrWhiteSpace(song.BackgroundPath))
            return null;

        try
        {
            var baseDirectory = Path.GetDirectoryName(song.SourcePath);
            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

            var combined = Path.Combine(baseDirectory, song.BackgroundPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(combined) ? ImageSource.FromFile(combined) : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Keyboard / focus ─────────────────────────────────────────────────────

    private void FocusKeyCatcher()
    {
        try
        {
            KeyCatcher.Text = string.Empty;
            KeyCatcher.Focus();
        }
        catch
        {
            // Ignore focus errors
        }
    }

    private void KeyCatcher_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isGameLoaded || string.IsNullOrEmpty(e.NewTextValue)) return;

        var key = char.ToUpper(e.NewTextValue[^1]);
        int? lane = key switch
        {
            '1' => 0,
            '7' => 1,
            '5' => 2,
            '9' => 3,
            '3' => 4,
            'A' => 1,
            'S' => 0,
            'D' => 2,
            'F' => 3,
            'G' => 4,
            _ => null
        };

        if (lane is int l)
            HandleLaneInput(l);

        KeyCatcher.Text = string.Empty;
    }

    private void KeyCatcher_Unfocused(object sender, FocusEventArgs e)
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(10), () => FocusKeyCatcher());
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if WINDOWS
        try
        {
            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win)
            {
                win.Activated -= Window_Activated;
                win.Activated += Window_Activated;

                if (win.Content is Microsoft.UI.Xaml.FrameworkElement content)
                {
                    content.KeyDown -= Content_KeyDown;
                    content.KeyDown += Content_KeyDown;
                    content.KeyUp -= Content_KeyUp;
                    content.KeyUp += Content_KeyUp;
                }
            }
        }
        catch
        {
            // Ignore Windows-specific setup errors
        }
#endif
    }

#if WINDOWS
    private void Window_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => FocusKeyCatcher());
    }

    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_isGameLoaded || !_engine.IsPlaying) return;

        int? lane = e.Key switch
        {
            Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 => 0,
            Windows.System.VirtualKey.Number7 or Windows.System.VirtualKey.NumberPad7 => 1,
            Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 => 2,
            Windows.System.VirtualKey.Number9 or Windows.System.VirtualKey.NumberPad9 => 3,
            Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 => 4,
            Windows.System.VirtualKey.A => 1,
            Windows.System.VirtualKey.S => 0,
            Windows.System.VirtualKey.D => 2,
            Windows.System.VirtualKey.F => 3,
            Windows.System.VirtualKey.G => 4,
            _ => null
        };

        if (lane is int l) { HandleLaneInput(l); e.Handled = true; }
    }

    private void Content_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_isGameLoaded || !_engine.IsPlaying) return;

        int? lane = e.Key switch
        {
            Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 => 0,
            Windows.System.VirtualKey.Number7 or Windows.System.VirtualKey.NumberPad7 => 1,
            Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 => 2,
            Windows.System.VirtualKey.Number9 or Windows.System.VirtualKey.NumberPad9 => 3,
            Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 => 4,
            Windows.System.VirtualKey.A => 1,
            Windows.System.VirtualKey.S => 0,
            Windows.System.VirtualKey.D => 2,
            Windows.System.VirtualKey.F => 3,
            Windows.System.VirtualKey.G => 4,
            _ => null
        };

        if (lane is int l)
        {
            e.Handled = true;
        }
    }
#endif

    // ── Inner types ──────────────────────────────────────────────────────────

    private sealed class GamePageData
    {
        public string SongTitle { get; set; } = "";
        public string SongArtist { get; set; } = "";
        public string? SongSourcePath { get; set; }
        public string SongMusicPath { get; set; } = "";
        public string SongBackgroundPath { get; set; } = "";
        public double SongOffsetSeconds { get; set; }
        public int ChartIndex { get; set; }
    }

    private sealed class GameStartData
    {
        public SscSong Song { get; set; } = null!;
        public SscChart Chart { get; set; } = null!;
        public int Av { get; set; } = GameConstants.DefaultAv;
        public string NoteSkin { get; set; } = "Prime";
        public string? RemoteAudioUrl { get; set; }
        public JudgmentDifficulty JudgmentDifficulty { get; set; } = JudgmentDifficulty.Standard;
        public bool AnimationsEnabled { get; set; } = DeviceInfo.Platform != DevicePlatform.Android;
    }

    private sealed class GameResultsData
    {
        public string SongTitle { get; set; } = "";
        public string SongArtist { get; set; } = "";
        public string ChartDifficulty { get; set; } = "";
        public string ChartStepType { get; set; } = "";
        public int ChartMeter { get; set; }
        public int Score { get; set; }
        public string Grade { get; set; } = "";
        public string Plate { get; set; } = "";
        public double Accuracy { get; set; }
        public int MaxCombo { get; set; }
        public int PerfectCount { get; set; }
        public int GreatCount { get; set; }
        public int GoodCount { get; set; }
        public int BadCount { get; set; }
        public int MissCount { get; set; }
    }
}