using CommunityToolkit.Maui.Views;
using TapItUp.Game;
using System.Diagnostics;
using System.Text.Json;

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
    private NoteFieldDrawable? _landscapeNoteFieldDrawable; // Add this line
    private readonly Stopwatch _playbackTimer = new();
    private double _playbackStartOffsetSeconds;
    private double _notesDisplayOffsetSeconds = 0.4; // positive = delay notes by this many seconds (tweakable)
    private SscSong? _song;
    private SscChart? _chart;
    private bool _isGameLoaded;

    public string SongDataJson { get; set; } = "";

    public GamePage()
    {
        InitializeComponent();

        _noteFieldDrawable = new NoteFieldDrawable(_engine);
        NoteFieldView.Drawable = _noteFieldDrawable;

        // Initialize the landscape drawable too
        _landscapeNoteFieldDrawable = new NoteFieldDrawable(_engine);
        LandscapeNoteFieldView.Drawable = _landscapeNoteFieldDrawable;

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

        // Back to 60 FPS (16ms) for smooth rhythm game experience
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

    private static readonly HttpClient _http = new();
    private Stream? _audioStream;
    private string? _audioTempFile;
    private async Task LoadSongFromData()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"🎮 Raw SongDataJson: {SongDataJson}");

            // First try to deserialize as the new GameStartData format
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
                // New format from SongSelectPage - we already have the song and chart objects
                _song = gameStartData.Song;
                _chart = gameStartData.Chart;

                // Set the scroll speed from the song select page on BOTH drawables
                System.Diagnostics.Debug.WriteLine($"🎮 Setting scroll speed to: {gameStartData.ScrollSpeed:F1}x");
                _noteFieldDrawable.ScrollSpeedMultiplier = gameStartData.ScrollSpeed;
                if (_landscapeNoteFieldDrawable != null)
                {
                    _landscapeNoteFieldDrawable.ScrollSpeedMultiplier = gameStartData.ScrollSpeed;
                }

                // Set the note skin on BOTH drawables
                System.Diagnostics.Debug.WriteLine($"🎮 Setting note skin to: {gameStartData.NoteSkin}");
                _noteFieldDrawable.NoteSkin = gameStartData.NoteSkin;
                if (_landscapeNoteFieldDrawable != null)
                {
                    _landscapeNoteFieldDrawable.NoteSkin = gameStartData.NoteSkin;
                }
                _engine.JudgmentDifficulty = gameStartData.JudgmentDifficulty;

                System.Diagnostics.Debug.WriteLine($"🎮 Loading game with scroll speed: {gameStartData.ScrollSpeed:F1}x and note skin: {gameStartData.NoteSkin}");
                System.Diagnostics.Debug.WriteLine($"   Song: {_song.Title}");
                System.Diagnostics.Debug.WriteLine($"   Artist: {_song.Artist}");
                System.Diagnostics.Debug.WriteLine($"   Chart: {_chart.Difficulty} {_chart.Meter}");
                System.Diagnostics.Debug.WriteLine($"   Chart has {_chart.Notes.Count} notes");

                // 🎵 LOAD / STREAM AUDIO
                if (!string.IsNullOrWhiteSpace(gameStartData.RemoteAudioUrl))
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"🎵 Loading audio: {gameStartData.RemoteAudioUrl}");
                        if (Uri.IsWellFormedUriString(gameStartData.RemoteAudioUrl, UriKind.Absolute))
                        {
                            // 🌍 STREAM FROM CDN
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

                await LoadSongAndChart();
                return;
            }

            // Fallback to old GamePageData format for compatibility
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

        // Apply display offset so initial engine time is delayed by _notesDisplayOffsetSeconds
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

        _engine.Update(engineTime);

        var hudChanged = previousScore != _engine.Score ||
                         previousCombo != _engine.Combo ||
                         previousMissCombo != _engine.MissCombo ||
                         previousJudgment != _engine.LastJudgmentText;

        var timeChanged = Math.Abs(_engine.CurrentTimeSeconds - previousTimeSeconds) > 0.008;

        if (hudChanged)
            RefreshHud();

        if (timeChanged || hudChanged)
        {
            NoteFieldView.Invalidate();
            LandscapeNoteFieldView?.Invalidate();
        }

        if (!_engine.IsPlaying && _playbackTimer.IsRunning)
        {
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

            var resultsJson = System.Text.Json.JsonSerializer.Serialize(resultsData);
            var encodedResults = Uri.EscapeDataString(resultsJson);

            Dispatcher.Dispatch(async () =>
                await Shell.Current.GoToAsync($"ResultsPage?resultsData={encodedResults}"));
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
        await Task.Delay(50);

        // --- Cached remote audio (CDN) ---
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
            // --- Embedded bundle assets (starts with "Songs/") ---
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
            // --- Android SAF external folder ---
            // Direct file paths are blocked by scoped storage on Android 10+.
            // If the song was loaded via SAF (SongDocumentUri is set), find the audio
            // file inside that document tree using ContentResolver.
            if (!string.IsNullOrWhiteSpace(_song.SongDocumentUri))
            {
                System.Diagnostics.Debug.WriteLine($"🎵 Android SAF path — scanning song folder for audio");
                try
                {
                    var context = Android.App.Application.Context;
                    var treeUri = Android.Net.Uri.Parse(_song.SongDocumentUri);
                    if (treeUri != null)
                    {
                        // List children of the song folder document
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
                                        // Prefer the file matching _song.MusicPath exactly, fall back to first audio
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

                                // Copy to cache so MediaElement can play it via a file:// URI
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

                return; // SAF songs don't have a fallback file path
            }
#endif

            // --- External files loaded via file path (desktop / iOS) ---
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

    /// <summary>
    /// Gets a reliable temporary directory, falling back to alternatives if the default cache directory is invalid
    /// </summary>
    private static string GetReliableTempDirectory()
    {
        try
        {
            // Try the default MAUI cache directory first
            var defaultCacheDir = FileSystem.Current.CacheDirectory;
            System.Diagnostics.Debug.WriteLine($"📁 Default cache directory: {defaultCacheDir}");

            // Check if the directory path looks valid and doesn't contain problematic patterns
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

        // Fallback options
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

        // Last resort - use system temp
        System.Diagnostics.Debug.WriteLine("📁 Using system temp directory as last resort");
        return Path.GetTempPath();
    }

    /// <summary>
    /// Try alternative methods to load embedded audio when the primary method fails
    /// </summary>
    private async Task TryAlternativeAudioLoading(string embeddedAudioPath)
    {
        try
        {
            // Method 1: Try direct ms-appx URI (works on some platforms)
            var msAppxUri = $"ms-appx:///{embeddedAudioPath}";
            System.Diagnostics.Debug.WriteLine($"🔄 Trying ms-appx URI: {msAppxUri}");

            SongMediaElement.Source = MediaSource.FromUri(msAppxUri);
            SongMediaElement.Play();

            // Wait a bit to see if it works
            await Task.Delay(500);
            if (SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Playing ||
                SongMediaElement.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Buffering)
            {
                System.Diagnostics.Debug.WriteLine("✅ ms-appx URI method worked!");
                return;
            }

            // Method 2: Try embedded resource approach
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

            // Set landscape mode flag on both drawables
            _noteFieldDrawable.IsLandscapeMode = true;
            if (_landscapeNoteFieldDrawable != null)
            {
                _landscapeNoteFieldDrawable.IsLandscapeMode = true;
                // Sync the note skin and scroll speed
                _landscapeNoteFieldDrawable.NoteSkin = _noteFieldDrawable.NoteSkin;
                _landscapeNoteFieldDrawable.ScrollSpeedMultiplier = _noteFieldDrawable.ScrollSpeedMultiplier;
            }

            // Set the center area to quarter width of screen in landscape mode
            LandscapeCenterGrid.WidthRequest = Width * 0.25; // 25% of screen width

            // Sync background
            LandscapeBackgroundPreview.Source = BackgroundPreview.Source;
        }
        else
        {
            // Portrait mode
            _noteFieldDrawable.IsLandscapeMode = false;
            if (_landscapeNoteFieldDrawable != null)
            {
                _landscapeNoteFieldDrawable.IsLandscapeMode = false;
            }
        }

        // Refocus after layout changes
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () => FocusKeyCatcher());
    }

    private void RefreshHud()
    {
        var judgmentText = _engine.LastJudgmentText;
        var countsText = $"PERFECT {_engine.Counts[HitJudgment.Perfect]} • GREAT {_engine.Counts[HitJudgment.Great]} • GOOD {_engine.Counts[HitJudgment.Good]} • BAD {_engine.Counts[HitJudgment.Bad]} • MISS {_engine.Counts[HitJudgment.Miss]}";

        PortraitCountsLabel.Text = countsText;
        LandscapeCountsLabel.Text = countsText;

        var shouldShowJudgment = !IsStartupMessage(judgmentText);

        CenterJudgmentLabel.IsVisible = shouldShowJudgment;
        LandscapeCenterJudgmentLabel.IsVisible = shouldShowJudgment;

        if (shouldShowJudgment)
        {
            CenterJudgmentLabel.Text = judgmentText;
            LandscapeCenterJudgmentLabel.Text = judgmentText;

            var judgmentColor = judgmentText switch
            {
                "PERFECT" => Color.FromArgb("#87CEEB"),
                "GREAT" => Color.FromArgb("#00FF00"),
                "GOOD" => Color.FromArgb("#FFFF00"),
                "BAD" => Color.FromArgb("#8A2BE2"),
                "MISS" => Color.FromArgb("#FF0000"),
                _ => Color.FromArgb("#FFE76A")
            };

            CenterJudgmentLabel.TextColor = judgmentColor;
            LandscapeCenterJudgmentLabel.TextColor = judgmentColor;
        }

        // Miss combo takes priority over good combo when >= 4
        var showMissCombo = _engine.MissCombo >= 4;
        var showGoodCombo = !showMissCombo && _engine.Combo >= 4;
        var showCombo = showMissCombo || showGoodCombo;

        var comboNumber = showMissCombo ? _engine.MissCombo : _engine.Combo;
        var comboColor = showMissCombo ? Color.FromArgb("#FF0000") : Colors.White;

        // Portrait
        CenterComboNumberLabel.IsVisible = showCombo;
        CenterComboTextLabel.IsVisible = showCombo;

        // Landscape
        LandscapeCenterComboNumberLabel.IsVisible = showCombo;
        LandscapeCenterComboTextLabel.IsVisible = showCombo;

        if (showCombo)
        {
            CenterComboNumberLabel.Text = comboNumber.ToString();
            CenterComboNumberLabel.TextColor = comboColor;
            CenterComboTextLabel.Text = "COMBO";
            CenterComboTextLabel.TextColor = comboColor;

            LandscapeCenterComboNumberLabel.Text = comboNumber.ToString();
            LandscapeCenterComboNumberLabel.TextColor = comboColor;
            LandscapeCenterComboTextLabel.Text = "COMBO";
            LandscapeCenterComboTextLabel.TextColor = comboColor;
        }
    }

    // Helper method to determine if a judgment text is a startup message that should be hidden
    private static bool IsStartupMessage(string judgmentText)
    {
        return judgmentText switch
        {
            "GO" => true,
            "READY" => true,
            "SELECT SONG" => true,
            _ => false
        };
    }

    private void BuildPad(Grid grid)
    {
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();

        // Check if this is a landscape pad
        var isLandscapePad = grid == LandscapeLeftPad || grid == LandscapeRightPad;

        if (isLandscapePad)
        {
            // For landscape pads, use Auto sizing for tight spacing
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Minimal spacing for tight button layout
            grid.RowSpacing = 4;
            grid.ColumnSpacing = 4;

            // Center the compact grid in its container
            grid.HorizontalOptions = LayoutOptions.Center;
            grid.VerticalOptions = LayoutOptions.Center;
        }
        else
        {
            // Portrait mode - keep original Star sizing
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        // ALL pads get the same 5 buttons in diamond formation
        // Players can use either pad to hit any lane
        AddPadButton(grid, lane: 1, row: 0, column: 0); // Top Left
        AddPadButton(grid, lane: 3, row: 0, column: 2); // Top Right
        AddPadButton(grid, lane: 2, row: 1, column: 1); // Center
        AddPadButton(grid, lane: 0, row: 2, column: 0); // Bottom Left
        AddPadButton(grid, lane: 4, row: 2, column: 2); // Bottom Right
    }

    private void AddPadButton(Grid grid, int lane, int row, int column)
    {
        var isCenter = lane == 2;
        var isPortraitPad = grid == PortraitPad;
        var isLandscapePad = grid == LandscapeLeftPad || grid == LandscapeRightPad;

        if (isLandscapePad)
        {
            // For landscape buttons, create compact square buttons with fixed sizes
            var buttonSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 70 : 70;

            var button = new Button
            {
                Text = string.Empty, // No text for landscape buttons
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = LaneColors[lane].WithAlpha(0.4f),
                TextColor = Colors.Black,
                CornerRadius = 8,
                Margin = new Thickness(0), // Minimal margin for tight spacing
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                WidthRequest = buttonSize,
                HeightRequest = buttonSize,
                Shadow = new Shadow
                {
                    Brush = new SolidColorBrush(LaneColors[lane]),
                    Opacity = 0.3f,
                    Offset = new Point(0, 2),
                    Radius = 12
                }
            };

            // All buttons are functional - players can use either pad to hit any lane
            button.Pressed += (_, _) => HandleLaneInput(lane);
            button.Released += (_, _) => _engine.HandleLaneRelease(lane);

            grid.Add(button, column, row);
            return;
        }

        // Portrait pad logic - all buttons same size now
        var baseSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? 75  // Same size for all buttons on mobile
            : 90; // Same size for all buttons on desktop

        var baseFontSize = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? 35  // Same font size for all buttons on mobile
            : 40; // Same font size for all buttons on desktop

        var portraitButton = new Button
        {
            Text = LaneGlyphs[lane], // Keep text for portrait buttons
            FontSize = baseFontSize,
            FontAttributes = FontAttributes.Bold,
            BackgroundColor = LaneColors[lane].WithAlpha(0.4f), // Transparent for portrait
            TextColor = Colors.White,
            CornerRadius = 8,
            Margin = new Thickness(0), // Keep original portrait margin
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = baseSize,
            HeightRequest = baseSize,
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(LaneColors[lane]),
                Opacity = 0.3f,
                Offset = new Point(0, 2),
                Radius = 12
            }
        };

        // All buttons are functional - players can use either pad to hit any lane
        portraitButton.Pressed += (_, _) => HandleLaneInput(lane);
        portraitButton.Released += (_, _) => _engine.HandleLaneRelease(lane);

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
            NoteFieldView.Invalidate();
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

    private sealed class GameStartData
    {
        public SscSong Song { get; set; } = null!;
        public SscChart Chart { get; set; } = null!;
        public double ScrollSpeed { get; set; } = GameConstants.DefaultScrollSpeed;
        public string NoteSkin { get; set; } = "Prime";
        public string? RemoteAudioUrl { get; set; }
        public JudgmentDifficulty JudgmentDifficulty { get; set; } = JudgmentDifficulty.Standard;
    }

    private sealed class GameResultsData
    {
        public string SongTitle { get; set; } = "";
        public string SongArtist { get; set; } = "";
        public string ChartDifficulty { get; set; } = "";
        public string ChartStepType { get; set; } = ""; // Add this new property
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