using CommunityToolkit.Maui.Storage;
using TapItUp.Game;
using TapItUp.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TapItUp;

public partial class SongSelectPage : ContentPage
{
    private SscSong? _selectedSong;
    private SscChart? _selectedChart;
    private List<SscChart> _currentSortedCharts = [];

    public ObservableCollection<SongListItem> SongList { get; } = [];
    public ObservableCollection<SongListItem> SearchResults { get; } = [];
    public ObservableCollection<GameSeriesItem> GameSeriesList { get; } = [];

    private Dictionary<string, List<SscSong>> _songsBySeries = [];

    // Full unfiltered list for the currently selected series
    private List<SongListItem> _allSongsInSeries = [];

    // Flat list of every song across all series, used for global search
    private readonly List<SongListItem> _allSongsGlobal = [];

    private bool _hasSelection;
    public bool HasSelection
    {
        get => _hasSelection;
        private set { if (_hasSelection == value) return; _hasSelection = value; OnPropertyChanged(); }
    }


    private string _noteSkin = "Prime";
    public string NoteSkin
    {
        get => _noteSkin;
        set { if (_noteSkin == value) return; _noteSkin = value; OnPropertyChanged(); }
    }

    private bool _isSeriesSelectionVisible = true;
    public bool IsSeriesSelectionVisible
    {
        get => _isSeriesSelectionVisible;
        set
        {
            if (_isSeriesSelectionVisible == value) return;
            _isSeriesSelectionVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSongListVisible));
            OnPropertyChanged(nameof(IsSeriesViewVisible));
        }
    }

    public bool IsSongListVisible => !IsSeriesSelectionVisible && !IsSearchActive;

    // True only when the series grid should actually be shown:
    // user must be on the series screen AND not actively searching
    public bool IsSeriesViewVisible => _isSeriesSelectionVisible && !IsSearchActive;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(IsSeriesViewVisible));
            OnPropertyChanged(nameof(IsSongListVisible));
            ApplyGlobalSearchFilter();
        }
    }

    // True whenever the user has typed something — drives visibility in XAML
    public bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText);

    private JudgmentDifficulty _judgmentDifficulty = JudgmentDifficulty.Standard;
    public string JudgmentDifficultyString
    {
        get => _judgmentDifficulty.ToString();
        set
        {
            var parsed = Enum.TryParse<JudgmentDifficulty>(value, out var result) ? result : JudgmentDifficulty.Standard;
            if (_judgmentDifficulty == parsed) return;
            _judgmentDifficulty = parsed;
            OnPropertyChanged();
        }
    }

    private int _av = GameConstants.DefaultAv;

    public int Av
    {
        get => _av;
        set
        {
            var clamped = Math.Clamp(value, 300, 999);
            if (_av == clamped) return;
            _av = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvText));
        }
    }

    public string AvText => $"AV {_av}";

    private const string AnimationsEnabledKey = "AnimationsEnabled";
    private const string AudioOffsetMsKey = "AudioOffsetMs";
    private const string IsSettingsVisibleKey = "IsSettingsVisible";
    private const string SongSourceTypeKey = "SongSourceType";   // "embedded" | "url" | "folder"
    private const string SongSourceValueKey = "SongSourceValue"; // the URL or folder path

    private bool _animationsEnabled;
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (_animationsEnabled == value) return;
            _animationsEnabled = value;
            OnPropertyChanged();
            Preferences.Default.Set(AnimationsEnabledKey, value);
        }
    }

    /// <summary>
    /// Audio offset in milliseconds. Positive values delay the arrows relative to the audio
    /// (use when audio arrives late, e.g. Bluetooth). Negative values advance the arrows.
    /// Clamped from -0 to 2000 ms.
    /// </summary>
    private int _audioOffsetMs;
    public int AudioOffsetMs
    {
        get => _audioOffsetMs;
        set
        {
            var clamped = Math.Clamp(value, -0, 4000);
            if (_audioOffsetMs == clamped) return;
            _audioOffsetMs = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioOffsetText));
            Preferences.Default.Set(AudioOffsetMsKey, clamped);
        }
    }

    public string AudioOffsetText => _audioOffsetMs == 0
        ? "Offset: 0 ms"
        : _audioOffsetMs > 0 ? $"Offset: +{_audioOffsetMs} ms" : $"Offset: {_audioOffsetMs} ms";

    private bool _isSettingsVisible;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (_isSettingsVisible == value) return;
            _isSettingsVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandsButtonText));
            Preferences.Default.Set(IsSettingsVisibleKey, value);
        }
    }

    public string CommandsButtonText => IsSettingsVisible ? "⚙ Commands ▲" : "⚙ Commands ▼";

    public SongSelectPage()
    {
        InitializeComponent();
        BindingContext = this;

        // Load persisted animation preference; fall back to platform default
        var platformDefault = DeviceInfo.Platform != DevicePlatform.Android;
        AnimationsEnabled = Preferences.Default.Get(AnimationsEnabledKey, platformDefault);
        AudioOffsetMs = Preferences.Default.Get(AudioOffsetMsKey, 0);

        Routing.RegisterRoute("GamePage", typeof(GamePage));
        SizeChanged += OnPageSizeChanged;
    }

    // -------------------------------------------------------------------------
    // Layout / orientation
    // -------------------------------------------------------------------------

    private void OnPageSizeChanged(object sender, EventArgs e)
    {
        var isLandscape = Width > Height;

        PortraitLayout.IsVisible = !isLandscape;
        LandscapeLayout.IsVisible = isLandscape;

        if (isLandscape)
        {
            if (LandscapeSongListView != null)
            {
                LandscapeSongListView.SelectionChanged -= OnSongSelected;
                LandscapeSongListView.SelectedItem = SongListView.SelectedItem;
                LandscapeSongListView.SelectionChanged += OnSongSelected;
            }

            if (HasSelection && _selectedSong != null)
            {
                if (LandscapeSelectedTitleLabel != null) LandscapeSelectedTitleLabel.Text = _selectedSong.Title;
                if (LandscapeSelectedArtistLabel != null) LandscapeSelectedArtistLabel.Text = _selectedSong.Artist;

                if (LandscapeChartPicker != null)
                {
                    LandscapeChartPicker.SelectedIndexChanged -= OnChartChanged;
                    LandscapeChartPicker.Items.Clear();
                    foreach (var item in ChartPicker.Items)
                        LandscapeChartPicker.Items.Add(item);
                    LandscapeChartPicker.SelectedIndex = ChartPicker.SelectedIndex;
                    LandscapeChartPicker.SelectedIndexChanged += OnChartChanged;
                }
            }
        }
        else
        {
            if (LandscapeSongListView?.SelectedItem != null)
            {
                SongListView.SelectionChanged -= OnSongSelected;
                SongListView.SelectedItem = LandscapeSongListView.SelectedItem;
                SongListView.SelectionChanged += OnSongSelected;
            }
        }
    }

    // -------------------------------------------------------------------------
    // App-package (embedded) song loading
    // -------------------------------------------------------------------------

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (SongList.Count == 0 && _allSongsGlobal.Count == 0)
            _ = InitializeSongSourceAsync();
    }

    private async Task InitializeSongSourceAsync()
    {
        await Task.Delay(100); // allow UI to fully attach

        var sourceType = Preferences.Default.Get(SongSourceTypeKey, string.Empty);

        // First run — ask the user how they want to load songs
        if (string.IsNullOrEmpty(sourceType))
        {
            sourceType = await PromptSongSourceAsync();
            if (string.IsNullOrEmpty(sourceType)) return;
        }

        await LoadFromSourceAsync(sourceType, Preferences.Default.Get(SongSourceValueKey, string.Empty));
    }

    /// <summary>
    /// Shows the source selection dialog and persists the user's choice.
    /// Returns the chosen source type, or empty string if cancelled.
    /// </summary>
    private async Task<string> PromptSongSourceAsync()
    {
        var choice = await DisplayActionSheet(
            "Where are your songs?",
            "Cancel",
            null,
            "Load from URL (CDN)",
            "Load from device folder");

        switch (choice)
        {
            case "Load from URL (CDN)":
                var url = await DisplayPromptAsync(
                    "CDN URL",
                    "Enter your songs CDN base URL:",
                    initialValue: "https://pub-fac3ff2c2b384776b2761efc75069033.r2.dev");

                if (string.IsNullOrWhiteSpace(url)) return string.Empty;

                url = url.TrimEnd('/');
                Preferences.Default.Set(SongSourceTypeKey, "url");
                Preferences.Default.Set(SongSourceValueKey, url);
                return "url";

            case "Load from device folder":
                Preferences.Default.Set(SongSourceTypeKey, "folder");
                Preferences.Default.Remove(SongSourceValueKey);
                return "folder";

            default:
                return string.Empty;
        }
    }

    private async Task LoadFromSourceAsync(string sourceType, string sourceValue)
    {
        try
        {
            switch (sourceType)
            {
                case "url":
                    if (string.IsNullOrWhiteSpace(sourceValue))
                    {
                        Preferences.Default.Remove(SongSourceTypeKey);
                        _ = InitializeSongSourceAsync();
                        return;
                    }
                    await LoadFromUrlAsync(sourceValue);
                    break;

                case "folder":
                    await LoadFromSavedFolderOrPickAsync();
                    break;

                default:
                    Preferences.Default.Remove(SongSourceTypeKey);
                    _ = InitializeSongSourceAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SongSelect] LoadFromSourceAsync failed: {ex.GetType().Name}: {ex.Message}");
            await DisplayAlert("Error", $"Failed to load songs:\n\n{ex.Message}", "OK");
        }
    }

    private async Task LoadEmbeddedSongsAsync()
    {
        try
        {
            var songsBySeries = await RemoteSongService.LoadEmbeddedIndexAsync();

            if (songsBySeries.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[SongSelect] LoadEmbeddedIndexAsync returned 0 series.");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var (seriesName, songs) in songsBySeries)
                    foreach (var song in songs)
                        AddSongToSeries(song, seriesName, bannerPath: null);
            });

            System.Diagnostics.Debug.WriteLine($"[SongSelect] Loaded {_allSongsGlobal.Count} songs across {GameSeriesList.Count} series.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SongSelect] LoadEmbeddedSongsAsync failed: {ex.GetType().Name}: {ex.Message}");
            await DisplayAlert("Error", $"Failed to load embedded songs:\n\n{ex.GetType().Name}: {ex.Message}", "OK");
        }
    }

    private async Task LoadFromUrlAsync(string url)
    {
        SetLoadingVisible(true);
        try
        {
            var progress = new Progress<LoadProgress>(p => MainThread.BeginInvokeOnMainThread(() =>
            {
                var text = $"{p.Message} ({p.Current}/{p.Total})";
                LoadingLabelPortrait.Text = text;
                LoadingProgressBarPortrait.Progress = p.Percentage;
                LoadingLabelLandscape.Text = text;
                LoadingProgressBarLandscape.Progress = p.Percentage;
            }));

            var songs = await RemoteSongService.LoadSongsAsync(url, progress);

            // AddSongToSeries modifies ObservableCollections — must run on the main thread on Android
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var song in songs)
                {
                    var series = RemoteSongService.GetSeriesName(song.SourcePath ?? string.Empty);
                    AddSongToSeries(song, series, bannerPath: null);
                }
            });
        }
        finally
        {
            SetLoadingVisible(false);
        }
    }

    private async Task LoadFromSavedFolderOrPickAsync()
    {
        var savedFolder = Preferences.Default.Get(SongSourceValueKey, string.Empty);

        // If we have a saved path and it still exists, use it directly
        if (!string.IsNullOrWhiteSpace(savedFolder))
        {
#if ANDROID
            await LoadSongsFromSafAsync(savedFolder);
#else
            if (Directory.Exists(savedFolder))
            {
                await LoadSongsFromFileSystemAsync(savedFolder);
                return;
            }
#endif
        }

        // Otherwise open the folder picker and save the result
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (result == null || !result.IsSuccessful) return;

            var folderPath = result.Folder.Path;
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            Preferences.Default.Set(SongSourceValueKey, folderPath);

#if ANDROID
            var safUri = folderPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                ? folderPath
                : ConvertPathToSafUri(folderPath);
            await LoadSongsFromSafAsync(safUri);
#else
            await LoadSongsFromFileSystemAsync(folderPath);
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open folder picker: {ex.Message}", "OK");
        }
    }

    // -------------------------------------------------------------------------
    // External folder loading
    // -------------------------------------------------------------------------

    private async void OnOpenFolderClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);

            if (result == null || !result.IsSuccessful) return;

            var folderPath = result.Folder.Path;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                await DisplayAlert("Error", "Could not read the selected folder path.", "OK");
                return;
            }

#if ANDROID
            var safUri = folderPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                ? folderPath
                : ConvertPathToSafUri(folderPath);

            await LoadSongsFromSafAsync(safUri);
#else
            await LoadSongsFromFileSystemAsync(folderPath);
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open folder picker: {ex.Message}", "OK");
        }
    }

#if ANDROID
    /// <summary>
    /// Converts a plain Android filesystem path (e.g. /storage/emulated/0/Songs or
    /// /storage/XXXX-XXXX/Songs for SD card) to a SAF content:// tree URI.
    /// </summary>
    private static string ConvertPathToSafUri(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        const string authority = "com.android.externalstorage.documents";

        if (path.StartsWith("/storage/emulated/0", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path["/storage/emulated/0".Length..].TrimStart('/');
            var docId = string.IsNullOrEmpty(relative) ? "primary:" : $"primary:{relative}";
            return $"content://{authority}/tree/{Uri.EscapeDataString(docId)}";
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("storage", StringComparison.OrdinalIgnoreCase))
        {
            var volumeId = parts[1];
            var relative = parts.Length > 2 ? string.Join("/", parts.Skip(2)) : string.Empty;
            var docId = string.IsNullOrEmpty(relative) ? $"{volumeId}:" : $"{volumeId}:{relative}";
            return $"content://{authority}/tree/{Uri.EscapeDataString(docId)}";
        }

        return path; // fallback — scanner will log if it still fails
    }

    private async Task LoadSongsFromSafAsync(string treeUriString)
    {
        var context = Android.App.Application.Context;
        var loadedCount = 0;

        SetLoadingVisible(true);
        LoadingLabelPortrait.Text = "Scanning folder...";
        LoadingLabelLandscape.Text = "Scanning folder...";

        List<TapItUp.Platforms.Android.ScanResult> scanResults;
        try
        {
            scanResults = await Task.Run(() =>
                TapItUp.Platforms.Android.AndroidSafScanner.Scan(context, treeUriString));
        }
        catch (Exception ex)
        {
            SetLoadingVisible(false);
            await DisplayAlert("Error", $"Failed to scan folder: {ex.Message}", "OK");
            return;
        }

        if (scanResults.Count == 0)
        {
            SetLoadingVisible(false);
            await DisplayAlert("No Songs Found",
                "No .ssc files were found.\n\nExpected structure:\n  Root / Game Series / Song / song.ssc",
                "OK");
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var result in scanResults)
            {
                // Use SongName from the scanner — content:// URIs can't be path-parsed
                var title = result.SongName;
                var dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx >= 0) title = title[(dashIdx + 3)..].Trim();

                var song = new SscSong
                {
                    Title = title,
                    Artist = string.Empty,
                    SourcePath = result.SscUri,
                    MusicPath = string.Empty,
                    BackgroundPath = string.Empty,
                    BpmChanges = [],
                    TickCounts = [],
                    SpeedChanges = [],
                    Charts = []
                };
                song.SongDocumentUri = result.SongDocumentUri;

                ImageSource? bannerOverride = null;
                if (!string.IsNullOrEmpty(result.BannerUri))
                {
                    var capturedUri = result.BannerUri;
                    bannerOverride = ImageSource.FromStream(
                        () => TapItUp.Platforms.Android.AndroidSafScanner.OpenRead(context, capturedUri));
                }

                AddSongToSeries(song, result.SeriesName, bannerPath: null, bannerImageOverride: bannerOverride);
                loadedCount++;
            }
        });

        SetLoadingVisible(false);
        System.Diagnostics.Debug.WriteLine($"[SongSelect] Indexed {loadedCount} songs from SAF folder.");
    }
#endif

    private Task LoadSongsFromFileSystemAsync(string rootPath)
    {
        var loadedCount = 0;
        var seriesNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(rootPath))
            {
                _ = DisplayAlert("Folder Not Found", $"The path could not be accessed:\n{rootPath}", "OK");
                return Task.CompletedTask;
            }

            foreach (var seriesDir in Directory.GetDirectories(rootPath))
            {
                foreach (var songDir in Directory.GetDirectories(seriesDir))
                {
                    var sscFiles = Directory.GetFiles(songDir, "*.ssc");
                    if (sscFiles.Length == 0) continue;

                    var sscPath = sscFiles[0];
                    var seriesName = RemoteSongService.GetSeriesName(sscPath);
                    seriesNames.Add(seriesName);

                    var song = new SscSong
                    {
                        Title = RemoteSongService.GetSongTitleFromPath(sscPath),
                        Artist = string.Empty,
                        SourcePath = sscPath,
                        MusicPath = string.Empty,
                        BackgroundPath = string.Empty,
                        BpmChanges = [],
                        TickCounts = [],
                        SpeedChanges = [],
                        Charts = []
                    };

                    var bannerPath = new[] {
                        Path.Combine(seriesDir, "banner.png"),
                        Path.Combine(seriesDir, "banner.jpg"),
                    }.FirstOrDefault(File.Exists);

                    // Must be on main thread for ObservableCollection on Android
                    MainThread.BeginInvokeOnMainThread(() => AddSongToSeries(song, seriesName, bannerPath));
                    loadedCount++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _ = DisplayAlert("Permission Denied", $"Cannot read:\n{rootPath}", "OK");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _ = DisplayAlert("Error", $"Failed to scan folder: {ex.Message}", "OK");
            return Task.CompletedTask;
        }

        System.Diagnostics.Debug.WriteLine($"[SongSelect] Indexed {loadedCount} songs across {seriesNames.Count} series from folder.");
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Remote URL loading
    // -------------------------------------------------------------------------

    private async void OnLoadRemoteClicked(object sender, EventArgs e)
    {
        var url = await DisplayPromptAsync(
            "Remote Song Library",
            "Enter your CDN URL:",
            placeholder: "https://pub-fac3ff2c2b384776b2761efc75069033.r2.dev",
            initialValue: Preferences.Get("RemoteBaseUrl", "https://pub-fac3ff2c2b384776b2761efc75069033.r2.dev"));

        if (string.IsNullOrWhiteSpace(url)) return;

        Preferences.Set("RemoteBaseUrl", url.TrimEnd('/'));

        SetLoadingVisible(true);

        try
        {
            LoadRemoteButtonPortrait.IsEnabled = false;
            LoadRemoteButtonLandscape.IsEnabled = false;
            LoadRemoteButtonPortrait.Text = "Loading...";
            LoadRemoteButtonLandscape.Text = "Loading...";

            var progress = new Progress<LoadProgress>(p => MainThread.BeginInvokeOnMainThread(() =>
            {
                var text = $"{p.Message} ({p.Current}/{p.Total})";
                LoadingLabelPortrait.Text = text;
                LoadingProgressBarPortrait.Progress = p.Percentage;
                LoadingLabelLandscape.Text = text;
                LoadingProgressBarLandscape.Progress = p.Percentage;
            }));

            var songs = await RemoteSongService.LoadSongsAsync(url, progress);

            foreach (var song in songs)
            {
                var series = RemoteSongService.GetSeriesName(song.SourcePath ?? string.Empty);
                AddSongToSeries(song, series, bannerPath: null);
            }

            await DisplayAlert(songs.Count == 0 ? "No Songs" : "Done",
                songs.Count == 0 ? "No songs found at that URL." : $"Loaded {songs.Count} remote song(s).",
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not load remote songs:\n{ex.Message}", "OK");
        }
        finally
        {
            LoadRemoteButtonPortrait.IsEnabled = true;
            LoadRemoteButtonPortrait.Text = "Load from URL";
            LoadRemoteButtonLandscape.IsEnabled = true;
            LoadRemoteButtonLandscape.Text = "Load from URL";
            SetLoadingVisible(false);
        }
    }

    private void SetLoadingVisible(bool visible)
    {
        LoadingLabelPortrait.IsVisible = visible;
        LoadingProgressBarPortrait.IsVisible = visible;
        LoadingProgressBarPortrait.Progress = 0;
        LoadingLabelLandscape.IsVisible = visible;
        LoadingProgressBarLandscape.IsVisible = visible;
        LoadingProgressBarLandscape.Progress = 0;
    }

    // -------------------------------------------------------------------------
    // Song / chart list management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds a song to the internal series dictionary and <see cref="GameSeriesList" />,
    /// creating the series entry if it doesn't exist yet.
    /// </summary>
    private void AddSongToSeries(SscSong song, string seriesName, string? bannerPath, ImageSource? bannerImageOverride = null)
    {
        if (!_songsBySeries.ContainsKey(seriesName))
        {
            _songsBySeries[seriesName] = [];

            ImageSource? bannerImage = bannerImageOverride;

            if (bannerImage == null && bannerPath != null)
            {
                // External file — use stream so it works on Android
                var captured = bannerPath;
                bannerImage = ImageSource.FromStream(() => File.OpenRead(captured));
            }
            else if (bannerImage == null)
            {
                var cleanSeriesName = GetCleanSeriesNameForBanner(seriesName);
                var embeddedBannerPath = $"banner_{cleanSeriesName}.png";
                try
                {
                    bannerImage = ImageSource.FromStream(() =>
                        FileSystem.OpenAppPackageFileAsync(embeddedBannerPath)
                            .GetAwaiter().GetResult());
                }
                catch { /* no banner — that's fine */ }
            }

            GameSeriesList.Add(new GameSeriesItem { Name = seriesName, Banner = bannerImage });
        }

        _songsBySeries[seriesName].Add(song);

        // Register in the global flat list for cross-series search
        var item = CreateSongListItem(song);
        _allSongsGlobal.Add(item);
    }

    /// <summary>
    /// Converts a series name like "16 - PHOENIX" to "phoenix" for embedded banner lookup.
    /// </summary>
    private static string GetCleanSeriesNameForBanner(string seriesName)
    {
        // Remove number prefix pattern like "16 - " and convert to lowercase
        var cleaned = seriesName;

        // Look for pattern: digits, space, dash, space at the start
        var match = System.Text.RegularExpressions.Regex.Match(seriesName, @"^\d+\s*-\s*");
        if (match.Success)
        {
            cleaned = seriesName.Substring(match.Length);
        }

        return cleaned.Replace(" ", "").ToLower();
    }

    private void AddSong(SscSong song)
    {
        var item = CreateSongListItem(song);
        _allSongsInSeries.Add(item);
        SongList.Add(item);
    }

    private static SongListItem CreateSongListItem(SscSong song) => new()
    {
        Song = song,
        Title = song.Title,
        Artist = song.Artist,
        ChartSummary = GenerateChartSummary(song),
        BackgroundImageSource = TryCreateBackground(song)
    };

    private static ImageSource? TryCreateBackground(SscSong song)
    {
        if (string.IsNullOrWhiteSpace(song.SourcePath) || string.IsNullOrWhiteSpace(song.BackgroundPath))
            return null;

        try
        {
            var baseDir = Path.GetDirectoryName(song.SourcePath);
            if (string.IsNullOrWhiteSpace(baseDir)) return null;

            if (!Path.IsPathRooted(song.SourcePath))
            {
                // Embedded app-package asset
                var relativePath = Path.Combine(baseDir, song.BackgroundPath.Replace('\\', '/'))
                                       .Replace('\\', '/');
                return ImageSource.FromStream(() =>
                    FileSystem.OpenAppPackageFileAsync(relativePath).GetAwaiter().GetResult());
            }
            else
            {
                // External file
                var absolutePath = Path.Combine(baseDir,
                    song.BackgroundPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(absolutePath)) return null;
                return ImageSource.FromStream(() => File.OpenRead(absolutePath));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SongSelect] Background load failed: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Search / filter
    // -------------------------------------------------------------------------

    private void ApplyGlobalSearchFilter()
    {
        SearchResults.Clear();

        if (!IsSearchActive) return;

        // When a series is open, filter only that series' songs.
        // When on the series selector screen, search across all songs.
        var source = !IsSeriesSelectionVisible ? (IEnumerable<SongListItem>)SongList : _allSongsGlobal;

        foreach (var item in source)
        {
            if (item.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || item.Artist.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            {
                SearchResults.Add(item);
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => SearchText = e.NewTextValue;

    // -------------------------------------------------------------------------
    // Series / song selection
    // -------------------------------------------------------------------------

    private void OnSeriesSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = e.CurrentSelection.FirstOrDefault() as GameSeriesItem;
        if (selected == null) return;

        _allSongsInSeries.Clear();
        SongList.Clear();

        if (_songsBySeries.TryGetValue(selected.Name, out var songs))
            foreach (var song in songs)
                AddSong(song);

        IsSeriesSelectionVisible = false;
    }

    private void OnBackToSeriesClicked(object sender, EventArgs e)
    {
        _allSongsInSeries.Clear();
        SongList.Clear();
        _selectedSong = null;
        _selectedChart = null;
        HasSelection = false;

        PortraitSeriesCollectionView.SelectedItem = null;
        LandscapeSeriesCollectionView.SelectedItem = null;

        IsSeriesSelectionVisible = true;
    }

    private async void OnSongSelected(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = e.CurrentSelection.FirstOrDefault() as SongListItem;

        if (selectedItem == null)
        {
            _selectedSong = null;
            _selectedChart = null;
            HasSelection = false;
            return;
        }

        // Tap the same song again to deselect
        if (_selectedSong == selectedItem.Song)
        {
            _selectedSong = null;
            _selectedChart = null;
            HasSelection = false;

            SongListView.SelectionChanged -= OnSongSelected;
            SongListView.SelectedItem = null;
            SongListView.SelectionChanged += OnSongSelected;

            if (LandscapeSongListView != null)
            {
                LandscapeSongListView.SelectionChanged -= OnSongSelected;
                LandscapeSongListView.SelectedItem = null;
                LandscapeSongListView.SelectionChanged += OnSongSelected;
            }

            PortraitSearchResultsView.SelectedItem = null;
            LandscapeSearchResultsView.SelectedItem = null;

            return;
        }

        // Lazy-load charts if this song was only header-parsed
        if (selectedItem.Song.Charts.Count == 0 && !string.IsNullOrWhiteSpace(selectedItem.Song.SourcePath))
        {
            try
            {
                var content = await ReadSscContentAsync(selectedItem.Song);
                if (content != null)
                {
                    var fullSong = SscParser.Parse(content, selectedItem.Song.SourcePath);
                    fullSong.BaseUrl = selectedItem.Song.BaseUrl;
                    fullSong.SongDocumentUri = selectedItem.Song.SongDocumentUri;

                    // Swap the shell song for the fully parsed one in the series dictionary
                    var seriesName = GetGameSeries(selectedItem.Song.SourcePath);
                    if (_songsBySeries.TryGetValue(seriesName, out var seriesList))
                    {
                        var idx = seriesList.IndexOf(selectedItem.Song);
                        if (idx >= 0) seriesList[idx] = fullSong;
                    }

                    selectedItem.Song = fullSong;
                    selectedItem.Artist = fullSong.Artist;
                    selectedItem.ChartSummary = GenerateChartSummary(fullSong);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SongSelect] Lazy chart load failed for {selectedItem.Song.Title}: {ex.Message}");
            }
        }

        _selectedSong = selectedItem.Song;
        HasSelection = true;

        SelectedTitleLabel.Text = _selectedSong.Title;
        SelectedArtistLabel.Text = _selectedSong.Artist;

        if (LandscapeSelectedTitleLabel != null) LandscapeSelectedTitleLabel.Text = _selectedSong.Title;
        if (LandscapeSelectedArtistLabel != null) LandscapeSelectedArtistLabel.Text = _selectedSong.Artist;

        // Sync the other CollectionView without re-firing the event
        if (sender == SongListView && LandscapeSongListView != null)
        {
            LandscapeSongListView.SelectionChanged -= OnSongSelected;
            LandscapeSongListView.SelectedItem = selectedItem;
            LandscapeSongListView.SelectionChanged += OnSongSelected;
        }
        else if (sender == LandscapeSongListView)
        {
            SongListView.SelectionChanged -= OnSongSelected;
            SongListView.SelectedItem = selectedItem;
            SongListView.SelectionChanged += OnSongSelected;
        }

        PopulateCharts(_selectedSong);
    }

    /// <summary>
    /// Reads the raw .ssc content from whichever source the song came from:
    /// embedded app package, CDN/HTTP, local filesystem, or Android SAF.
    /// </summary>
    private static async Task<string?> ReadSscContentAsync(SscSong song)
    {
        var sourcePath = song.SourcePath!;

#if ANDROID
        // Android SAF content:// URI — must be handled before the HTTP check
        // because content:// passes Uri.IsWellFormedUriString as absolute
        if (sourcePath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            return await TapItUp.Platforms.Android.AndroidSafScanner.ReadTextAsync(
                Android.App.Application.Context, sourcePath);
#endif

        // Remote CDN / HTTP(S)
        if (Uri.IsWellFormedUriString(sourcePath, UriKind.Absolute))
            return await RemoteSongService.HttpClient.GetStringAsync(sourcePath);

        // Embedded app-package asset (relative path, not rooted)
        if (!Path.IsPathRooted(sourcePath))
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(sourcePath);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        // Plain filesystem (Windows / Mac / iOS)
        return await File.ReadAllTextAsync(sourcePath);
    }

    private void OnCloseSelectionClicked(object sender, EventArgs e)
    {
        SongListView.SelectedItem = null;
        LandscapeSongListView.SelectedItem = null;
        PortraitSearchResultsView.SelectedItem = null;
        LandscapeSearchResultsView.SelectedItem = null;
        _selectedSong = null;
        _selectedChart = null;
        HasSelection = false;

        ChartPicker.Items.Clear();
        LandscapeChartPicker.Items.Clear();
        _currentSortedCharts.Clear();

        SelectedTitleLabel.Text = "";
        SelectedArtistLabel.Text = "";
        LandscapeSelectedTitleLabel.Text = "";
        LandscapeSelectedArtistLabel.Text = "";
    }

    // -------------------------------------------------------------------------
    // Chart selection
    // -------------------------------------------------------------------------

    private void PopulateCharts(SscSong? song)
    {
        ChartPicker.Items.Clear();
        LandscapeChartPicker?.Items.Clear();
        _currentSortedCharts.Clear();

        if (song?.Charts == null || song.Charts.Count == 0) return;

        _currentSortedCharts = song.Charts
            .OrderBy(c => c.StepType?.ToLower() == "pump-single" ? 0 : 1)
            .ThenBy(c => c.Meter)
            .ToList();

        foreach (var chart in _currentSortedCharts)
        {
            var text = GetChartDisplayText(chart);
            ChartPicker.Items.Add(text);
            LandscapeChartPicker?.Items.Add(text);
        }

        if (ChartPicker.Items.Count > 0)
        {
            ChartPicker.SelectedIndex = 0;
            if (LandscapeChartPicker != null)
                LandscapeChartPicker.SelectedIndex = 0;

            _selectedChart = _currentSortedCharts.First();
        }
    }

    private void OnChartChanged(object sender, EventArgs e)
    {
        var picker = sender as Picker;
        var selectedIndex = picker?.SelectedIndex ?? -1;

        if (_selectedSong == null || selectedIndex < 0 || selectedIndex >= _currentSortedCharts.Count)
        {
            return;
        }

        if (picker == ChartPicker && LandscapeChartPicker != null)
            LandscapeChartPicker.SelectedIndex = selectedIndex;
        else if (picker == LandscapeChartPicker)
            ChartPicker.SelectedIndex = selectedIndex;

        _selectedChart = _currentSortedCharts[selectedIndex];
    }

    // -------------------------------------------------------------------------
    // Play
    // -------------------------------------------------------------------------

    // ── Navigation guard ─────────────────────────────────────────────────────
    private bool _isNavigating;

    private async void OnPlayClicked(object sender, EventArgs e)
    {
        if (_isNavigating) return;

        if (_selectedSong == null || _selectedChart == null)
        {
            await DisplayAlert("No Selection", "Please select a song and chart first.", "OK");
            return;
        }

        _isNavigating = true;
        try
        {
            var audioUrl = !string.IsNullOrWhiteSpace(_selectedSong.BaseUrl)
                ? new Uri(RemoteSongService.ResolveAssetUrl(_selectedSong, _selectedSong.MusicPath)).AbsoluteUri
                : null;

            var startData = new GameStartData
            {
                Song = _selectedSong,
                Chart = _selectedChart,
                Av = _av,
                NoteSkin = _noteSkin,
                RemoteAudioUrl = audioUrl,
                JudgmentDifficulty = _judgmentDifficulty,
                AnimationsEnabled = _animationsEnabled,
                AudioOffsetMs = _audioOffsetMs
            };

            var encodedJson = Uri.EscapeDataString(JsonSerializer.Serialize(startData));
            await Shell.Current.GoToAsync("GamePage", new Dictionary<string, object> { { "songData", encodedJson } });
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to start game: {ex.Message}", "OK");
        }
        finally
        {
            // Reset after navigation so the button works again when returning to this page
            _isNavigating = false;
        }
    }

    // -------------------------------------------------------------------------
    // Back button — return to series selection instead of closing the app
    // -------------------------------------------------------------------------

    protected override bool OnBackButtonPressed()
    {
        // If a song list is open (series was selected), go back to series selection
        if (!IsSeriesSelectionVisible)
        {
            OnBackToSeriesClicked(this, EventArgs.Empty);
            return true; // consumed — do not close the app
        }

        // Already on the series screen; let the default behaviour close/navigate normally
        return base.OnBackButtonPressed();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetChartDisplayText(SscChart chart)
    {
        var prefix = chart.StepType?.ToLower() switch
        {
            "pump-single" => "S",
            "pump-double" => "D",
            _ => ""
        };

        if (!string.IsNullOrEmpty(prefix)) return $"{prefix}{chart.Meter}";

        return string.IsNullOrEmpty(chart.Difficulty)
            ? $"Level {chart.Meter}"
            : chart.Meter > 0 ? $"{chart.Difficulty} {chart.Meter}" : chart.Difficulty;
    }

    private static string GenerateChartSummary(SscSong song)
    {
        // Song is a path-only shell — charts not loaded yet
        if (song.Charts.Count == 0) return "Select to load charts";

        var parts = new List<string>();

        var singles = song.Charts.Where(c => c.StepType?.ToLower() == "pump-single" && c.Meter > 0)
                          .Select(c => c.Meter).Distinct().OrderBy(x => x).ToList();
        var doubles = song.Charts.Where(c => c.StepType?.ToLower() == "pump-double" && c.Meter > 0)
                          .Select(c => c.Meter).Distinct().OrderBy(x => x).ToList();

        if (singles.Count > 0) parts.Add(singles.First() == singles.Last() ? $"S{singles.First()}" : $"S{singles.First()}-{singles.Last()}");
        if (doubles.Count > 0) parts.Add(doubles.First() == doubles.Last() ? $"D{doubles.First()}" : $"D{doubles.First()}-{doubles.Last()}");

        return parts.Count == 0
            ? $"{song.Charts.Count} chart(s)"
            : $"{string.Join(", ", parts)} • {song.Charts.Count} chart(s)";
    }

    private static string GetGameSeries(string path)
    {
        path = path.Replace('\\', '/');
        var part = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                       .FirstOrDefault(p => p.Contains(" - "));
        if (string.IsNullOrEmpty(part)) return "UNKNOWN";
        var split = part.Split('-', 2);
        return split.Length < 2 ? "UNKNOWN" : split[1].Trim().ToUpperInvariant();
    }

    private static string GetGameSeriesFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "UNKNOWN";
        var path = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
        var part = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(part)) return "UNKNOWN";
        var split = part.Split('-', 2);
        return split.Length < 2 ? "UNKNOWN" : split[1].Trim().ToUpperInvariant();
    }

    private void OnAvChanged(object sender, ValueChangedEventArgs e) => Av = (int)Math.Round(e.NewValue);
    private void OnCommandsToggled(object sender, EventArgs e) => IsSettingsVisible = !IsSettingsVisible;

    private void OnAudioOffsetChanged(object sender, ValueChangedEventArgs e) =>
        AudioOffsetMs = (int)Math.Round(e.NewValue);
}

public class SongListItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public required SscSong Song { get; set; }
    public required string Title { get; set; }

    private string _artist = string.Empty;
    public required string Artist
    {
        get => _artist;
        set { if (_artist == value) return; _artist = value; OnPropertyChanged(); }
    }

    private string _chartSummary = string.Empty;
    public required string ChartSummary
    {
        get => _chartSummary;
        set { if (_chartSummary == value) return; _chartSummary = value; OnPropertyChanged(); }
    }

    public ImageSource? BackgroundImageSource { get; set; }
}

public class GameStartData
{
    public required SscSong Song { get; set; }
    public required SscChart Chart { get; set; }
    public int Av { get; set; } = GameConstants.DefaultAv;
    public string NoteSkin { get; set; } = "Prime";
    public string? RemoteAudioUrl { get; set; }
    public JudgmentDifficulty JudgmentDifficulty { get; set; } = JudgmentDifficulty.Standard;
    public bool AnimationsEnabled { get; set; } = true;
    /// <summary>
    /// Milliseconds to shift note timing relative to audio playback.
    /// Positive = arrows arrive later (compensates for late audio, e.g. Bluetooth).
    /// </summary>
    public int AudioOffsetMs { get; set; } = 0;
}