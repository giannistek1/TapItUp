using CommunityToolkit.Maui.Views;
using PumpMaui.Game;
using System.Diagnostics;

namespace PumpMaui;

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
    private SscSong? _song;
    private SscChart? _chart;
    private bool _isGameLoaded;

    public string SongDataJson { get; set; } = "";

    public GamePage()
    {
        InitializeComponent();

        _noteFieldDrawable = new NoteFieldDrawable(_engine);
        NoteFieldView.Drawable = _noteFieldDrawable;

        // Build all pads
        BuildPad(PortraitPad);
        BuildPad(LandscapeLeftPad);
        BuildPad(LandscapeRightPad);

        SizeChanged += OnPageSizeChanged;

        // Add MediaElement event handlers for debugging
        SongMediaElement.MediaOpened += (s, e) =>
            System.Diagnostics.Debug.WriteLine("🎵 MediaElement: Media opened successfully");

        SongMediaElement.MediaFailed += (s, e) =>
            System.Diagnostics.Debug.WriteLine($"❌ MediaElement: Media failed - {e.ErrorMessage}");

        SongMediaElement.MediaEnded += (s, e) =>
            System.Diagnostics.Debug.WriteLine("🎵 MediaElement: Media playback ended");

        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), OnFrame);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!string.IsNullOrEmpty(SongDataJson) && !_isGameLoaded)
        {
            await LoadSongFromData();
        }

        // Ensure keyboard focus
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => FocusKeyCatcher());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopGame();
    }

    private async Task LoadSongFromData()
    {
        try
        {
            var gameData = System.Text.Json.JsonSerializer.Deserialize<GamePageData>(
                Uri.UnescapeDataString(SongDataJson));

            if (gameData is null) return;

            System.Diagnostics.Debug.WriteLine($"🎮 Loading game data:");
            System.Diagnostics.Debug.WriteLine($"   Song: {gameData.SongTitle}");
            System.Diagnostics.Debug.WriteLine($"   Source: {gameData.SongSourcePath}");
            System.Diagnostics.Debug.WriteLine($"   Music: {gameData.SongMusicPath}");

            // Load from embedded resources or external file
            if (string.IsNullOrEmpty(gameData.SongSourcePath) || gameData.SongSourcePath.StartsWith("Songs/"))
            {
                // Load from bundled/embedded songs
                var songPath = gameData.SongSourcePath ?? "phoenix_demo.ssc";
                System.Diagnostics.Debug.WriteLine($"📄 Loading embedded song: {songPath}");

                await using var stream = await FileSystem.OpenAppPackageFileAsync(songPath);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                _song = SscParser.Parse(content, songPath);
            }
            else
            {
                // Load from external file
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
            _engine.Load(_song, _chart);

            // Update both portrait and landscape titles
            TitleLabel.Text = _song.Title;
            LandscapeTitleLabel.Text = _song.Title;

            BackgroundPreview.Source = TryCreateBackground(_song);
            LandscapeBackgroundPreview.Source = BackgroundPreview.Source;

            RefreshHud();
            NoteFieldView.Invalidate();

            System.Diagnostics.Debug.WriteLine($"✅ Song loaded: {_song.Title}");
            System.Diagnostics.Debug.WriteLine($"   Music path: {_song.MusicPath}");
            System.Diagnostics.Debug.WriteLine($"   Source path: {_song.SourcePath}");

            _isGameLoaded = true;
            StartGame();
            FocusKeyCatcher();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Game loading error: {ex.Message}");
            await DisplayAlert("Error loading game", ex.Message, "OK");
            await Shell.Current.GoToAsync("..");
        }
    }

    private void StartGame()
    {
        _engine.Start();
        PlaySongAudio();
        _playbackStartOffsetSeconds = Math.Min(0d,
            (_engine.Chart?.Notes.FirstOrDefault()?.TimeSeconds ?? 0d) - 1.2d);
        _engine.Update(_playbackStartOffsetSeconds);
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

    private bool OnFrame()
    {
        if (!IsVisible || !_isGameLoaded) return true;

        var elapsedSeconds = _playbackTimer.IsRunning
            ? _playbackTimer.Elapsed.TotalSeconds + _playbackStartOffsetSeconds
            : _engine.CurrentTimeSeconds;

        _engine.Update(elapsedSeconds);
        RefreshHud();

        // Invalidate both note field views
        NoteFieldView.Invalidate();
        LandscapeNoteFieldView?.Invalidate();

        if (!_engine.IsPlaying && _playbackTimer.IsRunning)
        {
            _playbackTimer.Stop();
            Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(".."));
        }

        return true;
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        StopGame();
        await Shell.Current.GoToAsync("..");
    }

    private async void PlaySongAudio()
    {
        SongMediaElement.Stop();
        SongMediaElement.Source = null;

        if (_song is null || string.IsNullOrWhiteSpace(_song.MusicPath)) return;

        try
        {
            // Check if this is an embedded song (starts with Songs/)
            if (!string.IsNullOrWhiteSpace(_song.SourcePath) && _song.SourcePath.StartsWith("Songs/"))
            {
                // For embedded MAUI assets, we need to copy the file to a temporary location first
                var baseDir = Path.GetDirectoryName(_song.SourcePath);
                if (baseDir is not null)
                {
                    var embeddedAudioPath = Path.Combine(baseDir, _song.MusicPath.Replace('/', Path.DirectorySeparatorChar))
                        .Replace('\\', '/'); // Normalize to forward slashes

                    System.Diagnostics.Debug.WriteLine($"🎵 Trying to load embedded audio: {embeddedAudioPath}");

                    try
                    {
                        // Copy embedded asset to temporary file
                        await using var sourceStream = await FileSystem.OpenAppPackageFileAsync(embeddedAudioPath);

                        // Create temporary file
                        var tempDir = Path.Combine(FileSystem.Current.CacheDirectory, "audio");
                        Directory.CreateDirectory(tempDir);
                        var tempFile = Path.Combine(tempDir, Path.GetFileName(_song.MusicPath));

                        // Copy to temp file if it doesn't exist or is outdated
                        if (!File.Exists(tempFile))
                        {
                            System.Diagnostics.Debug.WriteLine($"📁 Copying audio to temp file: {tempFile}");
                            await using var targetStream = File.Create(tempFile);
                            await sourceStream.CopyToAsync(targetStream);
                        }

                        // Load from temporary file
                        System.Diagnostics.Debug.WriteLine($"🎵 Loading audio from temp file: {tempFile}");
                        SongMediaElement.Source = MediaSource.FromFile(tempFile);
                        SongMediaElement.Play();
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"❌ Failed to copy embedded audio: {ex.Message}");

                        // Fallback: try direct URI approaches
                        await TryAlternativeAudioLoading(embeddedAudioPath);
                        return;
                    }
                }
            }

            // Handle external files (from file picker)
            if (!string.IsNullOrWhiteSpace(_song.SourcePath))
            {
                var baseDir = Path.GetDirectoryName(_song.SourcePath);
                if (baseDir is not null)
                {
                    var candidate = Path.Combine(baseDir, _song.MusicPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        System.Diagnostics.Debug.WriteLine($"🎵 Loading external audio: {candidate}");
                        SongMediaElement.Source = MediaSource.FromFile(candidate);
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
        }
    }

    private async Task TryAlternativeAudioLoading(string embeddedAudioPath)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("🔄 Trying alternative audio loading methods...");

            // Try different URI formats that might work on different platforms
            var uriFormats = new[]
            {
                $"ms-appx:///{embeddedAudioPath}",
                $"ms-appdata:///local/{embeddedAudioPath}",
                $"file:///android_asset/{embeddedAudioPath}", // Android
                embeddedAudioPath, // Direct path
                $"/{embeddedAudioPath}" // Path with leading slash
            };

            foreach (var uriFormat in uriFormats)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"🎵 Trying URI format: {uriFormat}");
                    SongMediaElement.Source = MediaSource.FromUri(uriFormat);
                    SongMediaElement.Play();

                    // Wait a moment to see if it works
                    await Task.Delay(500);

                    if (SongMediaElement.CurrentState != CommunityToolkit.Maui.Core.MediaElementState.Failed)
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ Audio loaded successfully with URI: {uriFormat}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ URI format failed {uriFormat}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("❌ All alternative audio loading methods failed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Alternative audio loading error: {ex.Message}");
        }
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        var isLandscape = Width > Height;

        // Toggle between portrait and landscape layouts
        PortraitLayout.IsVisible = !isLandscape;
        LandscapeLayout.IsVisible = isLandscape;

        if (isLandscape)
        {
            // Set up landscape-specific drawable if not already done
            if (LandscapeNoteFieldView.Drawable == null)
            {
                _landscapeNoteFieldDrawable = new NoteFieldDrawable(_engine);
                LandscapeNoteFieldView.Drawable = _landscapeNoteFieldDrawable;
            }

            // Sync background
            LandscapeBackgroundPreview.Source = BackgroundPreview.Source;
        }

        // Refocus after layout changes
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () => FocusKeyCatcher());
    }

    private void RefreshHud()
    {
        var scoreText = _engine.Score.ToString("D7");
        var gradeText = _engine.Grade;
        var accuracyText = $"{_engine.AccuracyPercent:0.00}%";
        var judgmentText = _engine.LastJudgmentText;
        var comboText = $"{_engine.Combo} COMBO";
        var countsText = $"PERFECT {_engine.Counts[HitJudgment.Perfect]} • GREAT {_engine.Counts[HitJudgment.Great]} • GOOD {_engine.Counts[HitJudgment.Good]} • BAD {_engine.Counts[HitJudgment.Bad]} • MISS {_engine.Counts[HitJudgment.Miss]}";

        // Update portrait HUD
        ScoreLabel.Text = scoreText;
        GradeLabel.Text = gradeText;
        AccuracyLabel.Text = accuracyText;
        JudgmentLabel.Text = judgmentText;
        ComboLabel.Text = comboText;
        CountsLabel.Text = countsText;

        // Update landscape HUD
        LandscapeScoreLabel.Text = scoreText;
        LandscapeGradeLabel.Text = gradeText;
        LandscapeAccuracyLabel.Text = accuracyText;
        LandscapeJudgmentLabel.Text = judgmentText;
        LandscapeComboLabel.Text = comboText;
        LandscapeCountsLabel.Text = countsText;

        if (_engine.Chart is not null)
        {
            var metaText = $"{_engine.Chart.Difficulty} {_engine.Chart.Meter} • {_engine.Chart.Notes.Count} notes • Max combo {_engine.MaxCombo}";
            MetaLabel.Text = metaText;
            LandscapeMetaLabel.Text = metaText;
        }
    }

    private void BuildPad(Grid grid)
    {
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();

        // Check if this is a landscape pad (smaller buttons)
        var isLandscapePad = grid == LandscapeLeftPad || grid == LandscapeRightPad;
        var buttonSizeMultiplier = isLandscapePad ? 0.7f : 1.0f;

        // Make all columns equal width for square buttons
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        // ALL pads get the same 5 buttons in diamond formation
        // Players can use either pad to hit any lane
        AddPadButton(grid, lane: 1, row: 0, column: 0, buttonSizeMultiplier); // Top Left
        AddPadButton(grid, lane: 3, row: 0, column: 2, buttonSizeMultiplier); // Top Right
        AddPadButton(grid, lane: 2, row: 1, column: 1, buttonSizeMultiplier); // Center
        AddPadButton(grid, lane: 0, row: 2, column: 0, buttonSizeMultiplier); // Bottom Left
        AddPadButton(grid, lane: 4, row: 2, column: 2, buttonSizeMultiplier); // Bottom Right
    }

    private void AddPadButton(Grid grid, int lane, int row, int column, float sizeMultiplier = 1.0f)
    {
        var isCenter = lane == 2;

        // Responsive button sizing
        var baseSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? (isCenter ? 55 : 75)  // Smaller for mobile
            : (isCenter ? 70 : 90); // Original size for desktop

        var buttonSize = (int)(baseSize * sizeMultiplier);

        var baseFontSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? (isCenter ? 28 : 35)  // Smaller fonts for mobile
            : (isCenter ? 32 : 40); // Original fonts for desktop

        var fontSize = (int)(baseFontSize * sizeMultiplier);

        var button = new Button
        {
            Text = LaneGlyphs[lane],
            FontSize = fontSize,
            FontAttributes = FontAttributes.Bold,
            BackgroundColor = LaneColors[lane].WithAlpha(0.92f),
            TextColor = Colors.Black,
            CornerRadius = 8,
            Margin = new Thickness(4 * sizeMultiplier),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = buttonSize,
            HeightRequest = buttonSize,
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(LaneColors[lane]),
                Opacity = 0.65f,
                Offset = new Point(0, 2),
                Radius = 12 * sizeMultiplier
            }
        };

        // All buttons are functional - players can use either pad to hit any lane
        button.Pressed += (_, _) => HandleLaneInput(lane);
        button.Released += (_, _) => _engine.HandleLaneRelease(lane);

        grid.Add(button, column, row);
    }

    private void HandleLaneInput(int lane)
    {
        _engine.HandleLaneHit(lane);
        RefreshHud();
        NoteFieldView.Invalidate();
        LandscapeNoteFieldView?.Invalidate();

        // Maintain focus on key catcher
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

            var combined = Path.Combine(baseDirectory,
                song.BackgroundPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(combined) ? ImageSource.FromFile(combined) : null;
        }
        catch
        {
            return null;
        }
    }

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
            '1' => 0, // Bottom left
            '7' => 1, // Top left  
            '5' => 2, // Center
            '9' => 3, // Top right
            '3' => 4, // Bottom right
            // Also support WASD and arrow keys
            'A' => 1, // Top left
            'S' => 0, // Bottom left
            'D' => 2, // Center
            'F' => 3, // Top right
            'G' => 4, // Bottom right
            _ => null
        };

        if (lane is int l)
        {
            HandleLaneInput(l);
        }

        // Clear and maintain focus
        KeyCatcher.Text = string.Empty;
    }

    private void KeyCatcher_Unfocused(object sender, FocusEventArgs e)
    {
        // Immediately refocus when losing focus
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

                // For Windows, also handle direct key events
                if (win.Content is Microsoft.UI.Xaml.FrameworkElement content)
                {
                    content.KeyDown -= Content_KeyDown;
                    content.KeyDown += Content_KeyDown;
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
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => FocusKeyCatcher());
        }
    }

    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_isGameLoaded || !_engine.IsPlaying) return;

        int? lane = e.Key switch
        {
            Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 => 0, // Bottom left
            Windows.System.VirtualKey.Number7 or Windows.System.VirtualKey.NumberPad7 => 1, // Top left
            Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 => 2, // Center
            Windows.System.VirtualKey.Number9 or Windows.System.VirtualKey.NumberPad9 => 3, // Top right
            Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 => 4, // Bottom right
            // WASD support
            Windows.System.VirtualKey.A => 1, // Top left
            Windows.System.VirtualKey.S => 0, // Bottom left
            Windows.System.VirtualKey.D => 2, // Center
            Windows.System.VirtualKey.F => 3, // Top right
            Windows.System.VirtualKey.G => 4, // Bottom right
            _ => null
        };

        if (lane is int l)
        {
            HandleLaneInput(l);
            e.Handled = true;
        }
    }
#endif

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
}