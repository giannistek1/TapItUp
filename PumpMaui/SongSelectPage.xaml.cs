using PumpMaui.Game;
using PumpMaui.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PumpMaui;

public partial class SongSelectPage : ContentPage, INotifyPropertyChanged
{
    private SscSong? _selectedSong;
    private SscChart? _selectedChart;
    private double _scrollSpeed = GameConstants.DefaultScrollSpeed;
    private List<SscChart> _currentSortedCharts = []; // Keep track of sorted charts for picker

    public ObservableCollection<SongListItem> SongList { get; } = [];

    private bool _hasSelection;
    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (_hasSelection == value) return;
            _hasSelection = value;
            OnPropertyChanged();
        }
    }

    public double ScrollSpeed
    {
        get => _scrollSpeed;
        set
        {
            if (Math.Abs(_scrollSpeed - value) < 0.01) return;
            _scrollSpeed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScrollSpeedText));
        }
    }

    public string ScrollSpeedText => $"{_scrollSpeed:F1}x";

    private string _noteSkin = "Prime";

    public string NoteSkin
    {
        get => _noteSkin;
        set
        {
            if (_noteSkin == value) return;
            _noteSkin = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<GameSeriesItem> GameSeriesList { get; } = new();

    private Dictionary<string, List<SscSong>> _songsBySeries = new();

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
        }
    }

    public bool IsSongListVisible => !IsSeriesSelectionVisible;

    public SongSelectPage()
    {
        InitializeComponent();
        BindingContext = this;

        // Register the GamePage route for Shell navigation
        Routing.RegisterRoute("GamePage", typeof(GamePage));

        // Handle size changes for responsive layout
        SizeChanged += OnPageSizeChanged;
    }

    private void OnPageSizeChanged(object sender, EventArgs e)
    {
        var isLandscape = Width > Height;
        System.Diagnostics.Debug.WriteLine($"🔄 OnPageSizeChanged: isLandscape={isLandscape}, Width={Width}, Height={Height}");

        // Toggle between portrait and landscape layouts
        PortraitLayout.IsVisible = !isLandscape;
        LandscapeLayout.IsVisible = isLandscape;

        if (isLandscape)
        {
            System.Diagnostics.Debug.WriteLine("🔄 Switching to landscape mode");

            // Make sure landscape elements exist before accessing them
            if (LandscapeSongListView != null)
            {
                // Sync selection from portrait to landscape WITHOUT triggering events
                LandscapeSongListView.SelectionChanged -= OnSongSelected;
                LandscapeSongListView.SelectedItem = SongListView.SelectedItem;
                LandscapeSongListView.SelectionChanged += OnSongSelected;

                System.Diagnostics.Debug.WriteLine($"🔄 Synced selection to landscape view: {SongListView.SelectedItem != null}");
            }

            // Sync the landscape labels with portrait labels if there's a selection
            if (HasSelection && _selectedSong != null)
            {
                System.Diagnostics.Debug.WriteLine($"🔄 Syncing selection data for: {_selectedSong.Title}");

                if (LandscapeSelectedTitleLabel != null)
                    LandscapeSelectedTitleLabel.Text = _selectedSong.Title;
                if (LandscapeSelectedArtistLabel != null)
                    LandscapeSelectedArtistLabel.Text = _selectedSong.Artist;

                // Sync chart picker
                if (LandscapeChartPicker != null)
                {
                    LandscapeChartPicker.SelectedIndexChanged -= OnChartChanged;
                    LandscapeChartPicker.Items.Clear();
                    foreach (var item in ChartPicker.Items)
                    {
                        LandscapeChartPicker.Items.Add(item);
                    }
                    LandscapeChartPicker.SelectedIndex = ChartPicker.SelectedIndex;
                    LandscapeChartPicker.SelectedIndexChanged += OnChartChanged;
                }
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("🔄 Switching to portrait mode");

            // Sync from landscape back to portrait
            if (LandscapeSongListView != null && LandscapeSongListView.SelectedItem != null)
            {
                SongListView.SelectionChanged -= OnSongSelected;
                SongListView.SelectedItem = LandscapeSongListView.SelectedItem;
                SongListView.SelectionChanged += OnSongSelected;

                System.Diagnostics.Debug.WriteLine($"🔄 Synced selection to portrait view");
            }
        }
    }

    private void OnScrollSpeedChanged(object sender, ValueChangedEventArgs e)
    {
        ScrollSpeed = e.NewValue;
        System.Diagnostics.Debug.WriteLine($"🎮 Scroll speed changed to: {ScrollSpeed:F1}x");
    }

    private async Task LoadEmbeddedSongs()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("🎵 Loading embedded songs...");

            var loadedCount = 0;

            foreach (var songPath in GameConstants.Songs)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"📂 Loading: {songPath}");
                    await using var stream = await FileSystem.OpenAppPackageFileAsync(songPath);
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync();

                    var song = SscParser.Parse(content, songPath);
                    System.Diagnostics.Debug.WriteLine($"   Parsed: '{song.Title}' - Charts: {song.Charts?.Count ?? 0}");

                    if (song.Charts?.Count > 0)
                    {
                        var series = GetGameSeries(songPath);

                        if (!_songsBySeries.ContainsKey(series))
                        {
                            _songsBySeries[series] = new List<SscSong>();

                            GameSeriesList.Add(new GameSeriesItem
                            {
                                Name = series,
                                Banner = ImageSource.FromStream(() =>
                                {
                                    return FileSystem
                                        .OpenAppPackageFileAsync($"banner_{series.Replace(" ", "").ToLower()}.png")
                                        .GetAwaiter()
                                        .GetResult();
                                })
                            });
                        }

                        _songsBySeries[series].Add(song);
                        loadedCount++;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"   ⚠️ Skipped '{song.Title}' - no charts");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ Failed to load {songPath}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"🎵 Successfully loaded {loadedCount} Phoenix songs");

            // Show a message if no songs were loaded
            if (loadedCount == 0)
            {
                await DisplayAlert("No Songs", "No embedded songs could be loaded. Please check that the song files are included in your project.", "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to load embedded songs: {ex.Message}");
            await DisplayAlert("Error", $"Failed to load songs: {ex.Message}", "OK");
        }
    }

    private void AddSong(SscSong song)
    {
        var item = new SongListItem
        {
            Song = song,
            Title = song.Title,
            Artist = song.Artist,
            ChartSummary = GenerateChartSummary(song),
            BackgroundImageSource = TryCreateBackground(song)
        };
        SongList.Add(item);
    }

    private static ImageSource? TryCreateBackground(SscSong song)
    {
        if (string.IsNullOrWhiteSpace(song.SourcePath) ||
            string.IsNullOrWhiteSpace(song.BackgroundPath))
            return null;

        try
        {
            var baseDirectory = Path.GetDirectoryName(song.SourcePath);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return null;

            // Normalize path for MAUI package
            var relativePath = Path.Combine(baseDirectory,
                song.BackgroundPath.Replace('\\', '/'))
                .Replace('\\', '/'); // ensure forward slashes

            return ImageSource.FromStream(() =>
            {
                return FileSystem
                    .OpenAppPackageFileAsync(relativePath)
                    .GetAwaiter()
                    .GetResult();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Background load failed: {ex.Message}");
            return null;
        }
    }

    private void PopulateCharts(SscSong? song)
    {
        if (song == null)
        {
            System.Diagnostics.Debug.WriteLine("❌ PopulateCharts called with NULL song!");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"🎵 PopulateCharts called for: '{song.Title}' by {song.Artist}");
        System.Diagnostics.Debug.WriteLine($"🎵 Song.Charts is null: {song.Charts == null}");
        System.Diagnostics.Debug.WriteLine($"🎵 Song.Charts.Count: {song.Charts?.Count ?? 0}");

        // Clear both chart pickers
        ChartPicker.Items.Clear();
        if (LandscapeChartPicker != null)
            LandscapeChartPicker.Items.Clear();

        _currentSortedCharts.Clear();
        SelectedChartBorder.IsVisible = false;

        if (song.Charts == null || song.Charts.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine($"❌ No charts available for this song. Charts null: {song.Charts == null}, Count: {song.Charts?.Count ?? 0}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"🎵 Processing {song.Charts.Count} charts for '{song.Title}':");

        // Debug: List all charts found
        for (int i = 0; i < song.Charts.Count; i++)
        {
            var chart = song.Charts[i];
            System.Diagnostics.Debug.WriteLine($"   Chart {i}: StepType='{chart.StepType}', Difficulty='{chart.Difficulty}', Meter={chart.Meter}, Notes={chart.Notes.Count}");
        }

        // Sort charts by StepType first, then by meter
        _currentSortedCharts = song.Charts
            .OrderBy(c => c.StepType?.ToLower() == "pump-single" ? 0 : 1) // Singles first
            .ThenBy(c => c.Meter)
            .ToList();

        System.Diagnostics.Debug.WriteLine($"🎵 After sorting, have {_currentSortedCharts.Count} charts");

        foreach (var chart in _currentSortedCharts)
        {
            var displayText = GetChartDisplayText(chart);
            ChartPicker.Items.Add(displayText);
            if (LandscapeChartPicker != null)
                LandscapeChartPicker.Items.Add(displayText);
            System.Diagnostics.Debug.WriteLine($"   Added chart: {displayText} (StepType: '{chart.StepType}', Difficulty: '{chart.Difficulty}', Meter: {chart.Meter})");
        }

        if (ChartPicker.Items.Count > 0)
        {
            ChartPicker.SelectedIndex = 0;
            if (LandscapeChartPicker != null)
                LandscapeChartPicker.SelectedIndex = 0;
            _selectedChart = _currentSortedCharts.First();
            System.Diagnostics.Debug.WriteLine($"   ✅ Selected first chart: {ChartPicker.Items[0]}");

            // Show the selected chart display (portrait only now)
            UpdateSelectedChartDisplay();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("   ❌ No charts to display after processing");
        }
    }

    /// <summary>
    /// Generates display text for a chart using S/D notation (e.g., "S5", "D12")
    /// </summary>
    private static string GetChartDisplayText(SscChart chart)
    {
        var stepPrefix = chart.StepType?.ToLower() switch
        {
            "pump-single" => "S",
            "pump-double" => "D",
            _ => ""
        };

        if (!string.IsNullOrEmpty(stepPrefix))
        {
            return $"{stepPrefix}{chart.Meter}";
        }

        // Fallback for charts without recognized StepType
        return string.IsNullOrEmpty(chart.Difficulty)
            ? $"Level {chart.Meter}"
            : (chart.Meter > 0
                ? $"{chart.Difficulty} {chart.Meter}"
                : chart.Difficulty);
    }

    private static string GenerateChartSummary(SscSong song)
    {
        if (song.Charts.Count == 0)
            return "No charts available";

        // Group by StepType and get range for each
        var singleCharts = song.Charts
            .Where(c => c.StepType?.ToLower() == "pump-single" && c.Meter > 0)
            .Select(c => c.Meter)
            .OrderBy(d => d)
            .Distinct()
            .ToList();

        var doubleCharts = song.Charts
            .Where(c => c.StepType?.ToLower() == "pump-double" && c.Meter > 0)
            .Select(c => c.Meter)
            .OrderBy(d => d)
            .Distinct()
            .ToList();

        var summaryParts = new List<string>();

        if (singleCharts.Count > 0)
        {
            var sMin = singleCharts.First();
            var sMax = singleCharts.Last();
            summaryParts.Add(sMin == sMax ? $"S{sMin}" : $"S{sMin}-{sMax}");
        }

        if (doubleCharts.Count > 0)
        {
            var dMin = doubleCharts.First();
            var dMax = doubleCharts.Last();
            summaryParts.Add(dMin == dMax ? $"D{dMin}" : $"D{dMin}-{dMax}");
        }

        // Fallback for unknown step types
        var otherCharts = song.Charts
            .Where(c => c.StepType?.ToLower() != "pump-single" &&
                       c.StepType?.ToLower() != "pump-double" &&
                       c.Meter > 0)
            .Select(c => c.Meter)
            .OrderBy(d => d)
            .Distinct()
            .ToList();

        if (otherCharts.Count > 0)
        {
            var oMin = otherCharts.First();
            var oMax = otherCharts.Last();
            summaryParts.Add(oMin == oMax ? $"L{oMin}" : $"L{oMin}-{oMax}");
        }

        if (summaryParts.Count == 0)
            return $"{song.Charts.Count} chart(s)";

        return $"{string.Join(", ", summaryParts)} • {song.Charts.Count} chart(s)";
    }

    private async void OnOpenFolderClicked(object sender, EventArgs e)
    {
        try
        {
#if WINDOWS
            await DisplayAlert("Feature Not Available", "External folder support coming soon for Windows!", "OK");
#else
            await DisplayAlert("Feature Not Available", "External folder support coming soon!", "OK");
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open folder picker: {ex.Message}", "OK");
        }
    }

    private void OnSongSelected(object sender, SelectionChangedEventArgs e)
    {
        // Debug output
        System.Diagnostics.Debug.WriteLine($"🎵 OnSongSelected called from {(sender == SongListView ? "Portrait" : "Landscape")}. Current selection count: {e.CurrentSelection.Count}");

        // Handle selection from either CollectionView
        var selectedItem = e.CurrentSelection.FirstOrDefault() as SongListItem;

        if (selectedItem == null)
        {
            System.Diagnostics.Debug.WriteLine("🎵 No selection or selection was cleared");
            // No selection or selection was cleared
            _selectedSong = null;
            _selectedChart = null;
            HasSelection = false;
            return;
        }

        System.Diagnostics.Debug.WriteLine($"🎵 Song selected: {selectedItem.Title} by {selectedItem.Artist}");

        if (selectedItem.Song == null)
        {
            System.Diagnostics.Debug.WriteLine("❌ selectedItem.Song is NULL! This is the problem.");
            DisplayAlert("Error", "Selected song data is corrupted. Please restart the app.", "OK");
            return;
        }

        // Check if the same song was selected again (to allow deselection)
        if (_selectedSong == selectedItem.Song)
        {
            System.Diagnostics.Debug.WriteLine($"🎵 Same song selected again, deselecting");
            // Clear the selection immediately and return - don't trigger more events
            _selectedSong = null;
            _selectedChart = null;
            HasSelection = false;

            // Clear the selection in both views WITHOUT triggering events
            SongListView.SelectionChanged -= OnSongSelected;
            SongListView.SelectedItem = null;
            SongListView.SelectionChanged += OnSongSelected;

            if (LandscapeSongListView != null)
            {
                LandscapeSongListView.SelectionChanged -= OnSongSelected;
                LandscapeSongListView.SelectedItem = null;
                LandscapeSongListView.SelectionChanged += OnSongSelected;
            }

            System.Diagnostics.Debug.WriteLine("🎵 Song deselected and cleared");
            return;
        }

        // Set the selected song and populate charts
        _selectedSong = selectedItem.Song;
        System.Diagnostics.Debug.WriteLine($"🎵 _selectedSong set to: {_selectedSong?.Title ?? "NULL"}");
        HasSelection = true;

        // Update labels for both orientations
        SelectedTitleLabel.Text = _selectedSong.Title;
        SelectedArtistLabel.Text = _selectedSong.Artist;

        // Update landscape labels if they exist
        if (LandscapeSelectedTitleLabel != null)
            LandscapeSelectedTitleLabel.Text = _selectedSong.Title;
        if (LandscapeSelectedArtistLabel != null)
            LandscapeSelectedArtistLabel.Text = _selectedSong.Artist;

        // Sync selection between views WITHOUT triggering events
        if (sender == SongListView && LandscapeSongListView != null)
        {
            LandscapeSongListView.SelectionChanged -= OnSongSelected;
            LandscapeSongListView.SelectedItem = selectedItem;
            LandscapeSongListView.SelectionChanged += OnSongSelected;
        }
        else if (sender == LandscapeSongListView && SongListView != null)
        {
            SongListView.SelectionChanged -= OnSongSelected;
            SongListView.SelectedItem = selectedItem;
            SongListView.SelectionChanged += OnSongSelected;
        }

        System.Diagnostics.Debug.WriteLine($"🎵 About to call PopulateCharts with song: {_selectedSong?.Title ?? "NULL"}");
        PopulateCharts(_selectedSong);

        System.Diagnostics.Debug.WriteLine($"🎵 Song selection completed. HasSelection: {HasSelection}");
    }

    private void OnChartChanged(object sender, EventArgs e)
    {
        var picker = sender as Picker;
        var selectedIndex = picker?.SelectedIndex ?? -1;

        if (_selectedSong == null || selectedIndex < 0 || selectedIndex >= _currentSortedCharts.Count)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Chart changed but invalid selection. Index: {selectedIndex}, Charts count: {_currentSortedCharts.Count}");
            SelectedChartBorder.IsVisible = false;
            return;
        }

        // Sync both pickers
        if (picker == ChartPicker && LandscapeChartPicker != null)
            LandscapeChartPicker.SelectedIndex = selectedIndex;
        else if (picker == LandscapeChartPicker)
            ChartPicker.SelectedIndex = selectedIndex;

        _selectedChart = _currentSortedCharts[selectedIndex];
        System.Diagnostics.Debug.WriteLine($"📝 Chart changed to: {ChartPicker.Items[selectedIndex]} - Notes: {_selectedChart.Notes.Count}");

        // Update the selected chart display
        UpdateSelectedChartDisplay();
    }

    private void UpdateSelectedChartDisplay()
    {
        if (_selectedChart == null)
        {
            SelectedChartBorder.IsVisible = false;
            return;
        }

        // Display chart using S/D notation
        var chartNotation = GetChartDisplayText(_selectedChart);

        // Update portrait display (landscape uses the picker directly)
        SelectedChartLevelLabel.Text = chartNotation;
        SelectedChartNotesLabel.Text = $"{_selectedChart.Notes.Count} notes";

        // Show note count for debugging
        System.Diagnostics.Debug.WriteLine($"📊 Chart {chartNotation} has {_selectedChart.Notes.Count} notes");

        SelectedChartBorder.IsVisible = true;
    }

    private async void OnPlayClicked(object sender, EventArgs e)
    {
        if (_selectedSong == null || _selectedChart == null)
        {
            await DisplayAlert("No Selection", "Please select a song and chart first.", "OK");
            return;
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"🎮 Starting game with:");
            System.Diagnostics.Debug.WriteLine($"   Song: {_selectedSong.Title}");
            System.Diagnostics.Debug.WriteLine($"   Chart: {_selectedChart.Difficulty} {_selectedChart.Meter}");
            System.Diagnostics.Debug.WriteLine($"   Scroll Speed: {ScrollSpeed:F1}x");
            System.Diagnostics.Debug.WriteLine($"   Note Skin: {NoteSkin}");

            var audioUrl = !string.IsNullOrWhiteSpace(_selectedSong.BaseUrl)
                ? RemoteSongService.ResolveAssetUrl(_selectedSong, _selectedSong.MusicPath)
                : null;

            if (!string.IsNullOrWhiteSpace(audioUrl))
            {
                // 🔥 FORCE proper URL encoding
                audioUrl = new Uri(audioUrl).AbsoluteUri;
            }

            var gameData = new GameStartData
            {
                Song = _selectedSong,
                Chart = _selectedChart,
                ScrollSpeed = ScrollSpeed,
                NoteSkin = NoteSkin,
                RemoteAudioUrl = audioUrl   // add this property to GameStartData
            };

            var json = JsonSerializer.Serialize(gameData);
            var encodedJson = Uri.EscapeDataString(json);

            var queryParams = new Dictionary<string, object>
            {
                { "songData", encodedJson }
            };

            await Shell.Current.GoToAsync("GamePage", queryParams);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Error starting game: {ex.Message}");
            await DisplayAlert("Error", $"Failed to start game: {ex.Message}", "OK");
        }
    }

    private void OnCloseSelectionClicked(object sender, EventArgs e)
    {
        // Clear the selection from both views
        SongListView.SelectedItem = null;
        LandscapeSongListView.SelectedItem = null;
        _selectedSong = null;
        _selectedChart = null;
        HasSelection = false;

        // Clear both chart pickers
        ChartPicker.Items.Clear();
        LandscapeChartPicker.Items.Clear();
        _currentSortedCharts.Clear();
        SelectedChartBorder.IsVisible = false;

        // Clear all labels
        SelectedTitleLabel.Text = "";
        SelectedArtistLabel.Text = "";
        LandscapeSelectedTitleLabel.Text = "";
        LandscapeSelectedArtistLabel.Text = "";

        System.Diagnostics.Debug.WriteLine("🎵 Selection cleared by user");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SongList.Count == 0)
        {
            await LoadEmbeddedSongs();
        }
    }

    private async void OnLoadRemoteClicked(object sender, EventArgs e)
    {
        // Prompt for the base URL (or you can wire this to a persistent Entry in XAML)
        var url = await DisplayPromptAsync(
            "Remote Song Library",
            "Enter your CDN URL:",
            placeholder: "https://pub-fac3ff2c2b384776b2761efc75069033.r2.dev",
            initialValue: Preferences.Get("RemoteBaseUrl", "https://pub-fac3ff2c2b384776b2761efc75069033.r2.dev"));

        if (string.IsNullOrWhiteSpace(url)) return;

        // Persist so the user doesn't have to retype it
        Preferences.Set("RemoteBaseUrl", url.TrimEnd('/'));

        LoadingLabelPortrait.IsVisible = true;
        LoadingProgressBarPortrait.IsVisible = true;
        LoadingProgressBarPortrait.Progress = 0;

        LoadingLabelLandscape.IsVisible = true;
        LoadingProgressBarLandscape.IsVisible = true;
        LoadingProgressBarLandscape.Progress = 0;

        try
        {
            // Show a simple loading indicator
            LoadRemoteButtonPortrait.IsEnabled = false;
            LoadRemoteButtonLandscape.IsEnabled = false;
            LoadRemoteButtonPortrait.Text = "Loading...";
            LoadRemoteButtonLandscape.Text = "Loading...";

            var progress = new Progress<LoadProgress>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    LoadingLabelPortrait.Text = $"{p.Message} ({p.Current}/{p.Total})";
                    LoadingProgressBarPortrait.Progress = p.Percentage;

                    LoadingLabelLandscape.Text = $"{p.Message} ({p.Current}/{p.Total})";
                    LoadingProgressBarLandscape.Progress = p.Percentage;
                });
            });

            var songs = await RemoteSongService.LoadSongsAsync(url, progress);

            foreach (var song in songs)
            {
                if (song.Charts.Count > 0)
                {
                    var series = GetGameSeriesFromUrl(song.SourcePath ?? string.Empty);

                    if (!_songsBySeries.ContainsKey(series))
                    {
                        _songsBySeries[series] = new List<SscSong>();

                        GameSeriesList.Add(new GameSeriesItem
                        {
                            Name = series,
                            Banner = ImageSource.FromStream(() =>
                            {
                                return FileSystem
                                    .OpenAppPackageFileAsync($"banner_{series.Replace(" ", "").ToLower()}.png")
                                    .GetAwaiter()
                                    .GetResult();
                            })
                        });
                    }

                    _songsBySeries[series].Add(song);
                }
            }

            if (songs.Count == 0)
                await DisplayAlert("No Songs", "No songs were found at that URL.", "OK");
            else
                await DisplayAlert("Done", $"Loaded {songs.Count} remote song(s).", "OK");
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

            LoadingLabelPortrait.IsVisible = false;
            LoadingProgressBarPortrait.IsVisible = false;
            LoadingProgressBarPortrait.Progress = 0;

            LoadingLabelLandscape.IsVisible = false;
            LoadingProgressBarLandscape.IsVisible = false;
            LoadingProgressBarLandscape.Progress = 0;
        }
    }

    // Helper
    private string GetGameSeries(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "UNKNOWN";

        // Normalize slashes
        path = path.Replace("\\", "/");

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Find the part that contains " - "
        var seriesPart = parts.FirstOrDefault(p => p.Contains(" - "));
        if (string.IsNullOrWhiteSpace(seriesPart))
            return "UNKNOWN";

        // "16 - Phoenix " → "Phoenix"
        var split = seriesPart.Split('-', 2);
        if (split.Length < 2)
            return "UNKNOWN";

        return split[1].Trim().ToUpperInvariant();
    }

    // Helper
    private string GetGameSeriesFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "UNKNOWN";

        var uri = new Uri(url);

        // AbsolutePath is already URL-decoded except for some cases
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return "UNKNOWN";

        var seriesPart = parts[0]; // "16 - PHOENIX"

        var split = seriesPart.Split('-', 2);
        if (split.Length < 2)
            return "UNKNOWN";

        return split[1].Trim().ToUpperInvariant();
    }

    private void OnSeriesSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = e.CurrentSelection.FirstOrDefault() as GameSeriesItem;
        if (selected == null)
            return;

        SongList.Clear();

        if (_songsBySeries.TryGetValue(selected.Name, out var songs))
        {
            foreach (var song in songs)
                AddSong(song);
        }

        // 🔥 Switch UI
        IsSeriesSelectionVisible = false;
    }

    private void OnBackToSeriesClicked(object sender, EventArgs e)
    {
        // Clear songs + selection
        SongList.Clear();
        _selectedSong = null;
        _selectedChart = null;
        HasSelection = false;

        // If you have both layouts:
        PortraitSeriesCollectionView.SelectedItem = null;
        LandscapeSeriesCollectionView.SelectedItem = null;

        // Show series again
        IsSeriesSelectionVisible = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SongListItem
{
    public required SscSong Song { get; set; }
    public required string Title { get; set; }
    public required string Artist { get; set; }
    public required string ChartSummary { get; set; }
    public ImageSource? BackgroundImageSource { get; set; }
}

public class GameStartData
{
    public required SscSong Song { get; set; }
    public required SscChart Chart { get; set; }
    public double ScrollSpeed { get; set; } = GameConstants.DefaultScrollSpeed;
    public string NoteSkin { get; set; } = "Prime";
    public string? RemoteAudioUrl { get; set; }
}
