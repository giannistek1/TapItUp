using PumpMaui.Game;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PumpMaui;

public partial class SongSelectPage : ContentPage, INotifyPropertyChanged
{
    private SscSong? _selectedSong;
    private SscChart? _selectedChart;

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

    public SongSelectPage()
    {
        InitializeComponent();
        BindingContext = this;

        // Register the GamePage route for Shell navigation
        Routing.RegisterRoute("GamePage", typeof(GamePage));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SongList.Count == 0)
        {
            await LoadPhoenixSongsAsync();
        }
    }

    private async Task LoadPhoenixSongsAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("🎵 Loading all Phoenix songs...");

            // Complete list of Phoenix songs with correct file names
            var phoenixSongs = new[]
            {
                "Songs/16 - PHOENIX/18039 - Solfeggietto/16A8 - Solfeggietto.ssc",
                "Songs/16 - PHOENIX/18040 - MURDOCH/16A9 - MURDOCH.ssc",
                "Songs/16 - PHOENIX/18041 - Little Munchkin/16B0 - Little Munchkin.ssc",
                "Songs/16 - PHOENIX/18042 - Simon Says, EURODANCE!! (feat. Sara M)/16B1 - Simon Says, EURODANCE!! (feat. Sara M).ssc",
                "Songs/16 - PHOENIX/18043 - Barber's Madness/16B2 - Barber's Madness.ssc",
                "Songs/16 - PHOENIX/18044 - Yo! Say!! Fairy!!!/16B3 - Yo! Say!! Fairy!!!.ssc",
                "Songs/16 - PHOENIX/18044.1 - Le Nozze di Figaro ~Celebrazione Remix~/16B4 - Le Nozze di Figaro ~Celebrazione Remix~.ssc",
                "Songs/16 - PHOENIX/1701 - Festival of Death Moon/1701 - Festival of Death Moon.ssc",
                "Songs/16 - PHOENIX/1703 - ESP/1703 - ESP.ssc",
                "Songs/16 - PHOENIX/1706 - Highway Chaser/1706 - Highway Chaser.ssc",
                "Songs/16 - PHOENIX/1708 - Binary Star/1708 - Binary Star.ssc",
                "Songs/16 - PHOENIX/1717 - About The Universe/1717 - About The Universe.ssc",
                "Songs/16 - PHOENIX/1719 - Perpetual/1719 - Perpetual.ssc",
                "Songs/16 - PHOENIX/1720 - Dancing/1720 - Dancing.ssc",
                "Songs/16 - PHOENIX/1721 - Galaxy Collapse/1721 - Galaxy Collapse.ssc",
                "Songs/16 - PHOENIX/1722 - Fracture Temporelle/1722 - Fracture Temporelle.ssc",
                "Songs/16 - PHOENIX/1723 - Catastrophe/1723 - Catastrophe.ssc",
                "Songs/16 - PHOENIX/1724 - Human Extinction (PIU Edit.)/1724 - Human Extinction (PIU Edit.).ssc",
                "Songs/16 - PHOENIX/1725 - That Kitty (PIU Edit.)/1725 - That Kitty (PIU Edit.).ssc",
                "Songs/16 - PHOENIX/1726 - Underworld ft. Skizzo (PIU Edit.)/1726 - Underworld ft. Skizzo (PIU Edit.).ssc",
                "Songs/16 - PHOENIX/1730 - Eternal Universe/1730 - Eternal Universe.ssc",
                "Songs/16 - PHOENIX/1732 - Burn Out/1732 - Burn Out.ssc",
                "Songs/16 - PHOENIX/1734 - 4NT/1734 - 4NT.ssc",
                "Songs/16 - PHOENIX/1744 - Ultimate Eyes/1744 - Ultimate Eyes.ssc",
                "Songs/16 - PHOENIX/1751 - Nyan-turne (feat. KuTiNA)/1751 - Nyan-turne (feat. KuTiNA).ssc",
                "Songs/16 - PHOENIX/18036.3 - 1948/1948.ssc",
                "Songs/16 - PHOENIX/18220 - Acquire/Acquire.ssc",
                "Songs/16 - PHOENIX/18125 - After LIKE/After LIKE.ssc",
                "Songs/16 - PHOENIX/18140 - Airplane/Airplane.ssc",
                "Songs/16 - PHOENIX/18236 - Alice in Misanthrope/Alice in Misanthrope.ssc",
                "Songs/16 - PHOENIX/18243.7 - ALiVE/ALiVE.ssc",
                "Songs/16 - PHOENIX/18115 - Alone/Alone.ssc",
                "Songs/16 - PHOENIX/18230 - Altale/Altale.ssc",
                "Songs/16 - PHOENIX/18120 - Amor Fati/Amor Fati.ssc",
                "Songs/16 - PHOENIX/18048 - Appassionata/Appassionata.ssc",
                "Songs/16 - PHOENIX/18239 - Aragami/Aragami.ssc",
                "Songs/16 - PHOENIX/18A50 - Athena's Shield/Athena's Shield.ssc",
                "Songs/16 - PHOENIX/18245 - BATTLE NO.1/BATTLE NO.1.ssc",
                "Songs/16 - PHOENIX/18130 - Beautiful Liar/Beautiful Liar.ssc",
                "Songs/16 - PHOENIX/18242.6 - Becouse of You/Becouse of You.ssc",
                "Songs/16 - PHOENIX/18242.3 - Big Daddy/Big Daddy.ssc",
                "Songs/16 - PHOENIX/18044.5 - Bluish Rose/Bluish Rose.ssc",
                "Songs/16 - PHOENIX/18100 - BOCA/BOCA.ssc",
                "Songs/16 - PHOENIX/18015 - BOOOM!!/BOOOM!!.ssc",
                "Songs/16 - PHOENIX/18242.9 - Break Through Myself feat. Risa Yuzuki/Break Through Myself feat. Risa Yuzuki.ssc",
                "Songs/16 - PHOENIX/18075 - Bubble/Bubble.ssc",
                "Songs/16 - PHOENIX/18050 - CHAOS AGAIN/CHAOS AGAIN.ssc",
                "Songs/16 - PHOENIX/18243.5 - Chobit Flavor/Chobit Flavor.ssc",
                "Songs/16 - PHOENIX/18205 - CO5M1C R4ILR0AD/CO5M1C R4ILR0AD.SSC",
                "Songs/16 - PHOENIX/18A90 - Crimson hood/Crimson hood.ssc",
                "Songs/16 - PHOENIX/18070 - CRUSH/CRUSH.ssc",
                "Songs/16 - PHOENIX/18057 - Deca Dance/Deca Dance.ssc",
                "Songs/16 - PHOENIX/18044.3 - Demon of Laplace/Demon of Laplace.ssc",
                "Songs/16 - PHOENIX/18242 - Destr0yer/Destr0yer.ssc",
                "Songs/16 - PHOENIX/18072 - Discord/Discord.ssc",
                "Songs/16 - PHOENIX/18067.5 - DO or DIE/DO or DIE.ssc",
                "Songs/16 - PHOENIX/18A99 - Doppelganger/Doppelganger.ssc",
                "Songs/16 - PHOENIX/18036.5 - DUEL/DUEL.ssc",
                "Songs/16 - PHOENIX/18A30 - Earendel/Earendel.ssc",
                "Songs/16 - PHOENIX/18126 - ELEVEN/ELEVEN.ssc",
                "Songs/16 - PHOENIX/18237.5 - EMOMOMO/EMOMOMO.ssc",
                "Songs/16 - PHOENIX/18049.7 - E.O.N/E.O.N.ssc",
                "Songs/16 - PHOENIX/18035 - Etude Op 10-4/Etude Op 10-4.ssc",
                "Songs/16 - PHOENIX/18020 - Euphorianic/Euphorianic.ssc",
                "Songs/16 - PHOENIX/18033 - Flavor Step!/Flavor Step!.ssc",
                "Songs/16 - PHOENIX/18242.4 - FLVSH OUT/FLVSH OUT.ssc",
                "Songs/16 - PHOENIX/18238.8 - Giselle/Giselle.ssc",
                "Songs/16 - PHOENIX/18054 - Glimmer Gleam/Glimmer Gleam.ssc",
                "Songs/16 - PHOENIX/18200 - GOODTEK/GOODTEK.ssc",
                "Songs/16 - PHOENIX/18235 - GOODBOUNCE/GOODBOUNCE.ssc",
                "Songs/16 - PHOENIX/18027 - Ghroth/Ghroth.ssc",
                "Songs/16 - PHOENIX/18240 - Halcyon/Halcyon.ssc",
                "Songs/16 - PHOENIX/18022 - Halloween Party ~Multiverse~/Halloween Party ~Multiverse~.ssc",
                "Songs/16 - PHOENIX/18251 - Heliosphere/Heliosphere.ssc",
                "Songs/16 - PHOENIX/18A10 - Hercules/Hercules.ssc",
                "Songs/16 - PHOENIX/18250 - Horang Pungryuga/Horang Pungryuga.ssc",
                "Songs/16 - PHOENIX/18A40 - Hymn of Golden Glory/Hymn of Golden Glory.ssc",
                "Songs/16 - PHOENIX/18A80 - Imaginarized City/Imaginarized City.ssc",
                "Songs/16 - PHOENIX/18046.5 - Imperium/Imperium.ssc",
                "Songs/16 - PHOENIX/18A20 - INVASION/INVASION.ssc",
                "Songs/16 - PHOENIX/18233 - iRELLiA/iRELLiA.ssc",
                "Songs/16 - PHOENIX/18025 - Jupin/Jupin.ssc",
                "Songs/16 - PHOENIX/18030 - KUGUTSU/KUGUTSU.ssc",
                "Songs/16 - PHOENIX/18037 - Lacrimosa/Lacrimosa.ssc",
                "Songs/16 - PHOENIX/18045 - Lohxia/Lohxia.ssc",
                "Songs/16 - PHOENIX/18033.5 - Lucid Dream/Lucid Dream.ssc",
                "Songs/16 - PHOENIX/18238.7.5 - Mahika/Mahika.ssc",
                "Songs/16 - PHOENIX/18049.5 - MEGAHEARTZ/MEGAHEARTZ.ssc",
                "Songs/16 - PHOENIX/18215 - MiiK/MiiK.ssc",
                "Songs/16 - PHOENIX/18A60 - Murdoch vs Otada/Murdoch vs Otada.ssc",
                "Songs/16 - PHOENIX/18180 - Nade Nade/Nade Nade.ssc",
                "Songs/16 - PHOENIX/18047 - Neo Catharsis/Neo Catharsis.ssc",
                "Songs/16 - PHOENIX/18060 - New Rose/New Rose.ssc",
                "Songs/16 - PHOENIX/18145 - Nostalgia/Nostalgia.ssc",
                "Songs/16 - PHOENIX/18105 - Nxde/Nxde.ssc",
                "Songs/16 - PHOENIX/18242.5 - Odin/Odin.ssc",
                "Songs/16 - PHOENIX/18133 - PANDORA/PANDORA.ssc",
                "Songs/16 - PHOENIX/18127 - Pirate/Pirate.ssc",
                "Songs/16 - PHOENIX/18225 - Pneumonoultramicroscopicsilicovolcanoconiosis ft. Kagamine Len GUMI/Pneumonoultramicroscopicsilicovolcanoconiosis ft. Kagamine Len GUMI.ssc",
                "Songs/16 - PHOENIX/18242.7 - Poppin' Shower/Poppin' Shower.ssc",
                "Songs/16 - PHOENIX/18046 - PRiMA MATERiA/PRiMA MATERiA.ssc",
                "Songs/16 - PHOENIX/18238 - PUPA/PUPA.ssc",
                "Songs/16 - PHOENIX/18080 - Queencard/Queencard.ssc",
                "Songs/16 - PHOENIX/18237 - R.I.P/R.I.P.ssc",
                "Songs/16 - PHOENIX/18238.7.0 - Rush-Hour/Rush-Hour.ssc",
                "Songs/16 - PHOENIX/18238.7 - Rush-More/Rush-More.ssc",
                "Songs/16 - PHOENIX/18034 - See/See.ssc",
                "Songs/16 - PHOENIX/18247 - Soldiers (TANO C W TEAM RED ANTHEM)/Soldiers (TANO C W TEAM RED ANTHEM).ssc",
                "Songs/16 - PHOENIX/18053 - Solve My Hurt/Solve My Hurt.ssc",
                "Songs/16 - PHOENIX/18049.3 - SONIC BOOM/SONIC BOOM.ssc",
                "Songs/16 - PHOENIX/18238.9 - Spooky Macaron/Spooky Macaron.ssc",
                "Songs/16 - PHOENIX/18129.5 - Spray/Spray.ssc",
                "Songs/16 - PHOENIX/18238.5 - STAGER/STAGER.ssc",
                "Songs/16 - PHOENIX/18036 - Stardream -Eurobeat Remix-/Stardream -Eurobeat Remix-.ssc",
                "Songs/16 - PHOENIX/18135 - STORM/STORM.ssc",
                "Songs/16 - PHOENIX/18055 - Sudden Appearance Image/Sudden Appearance Image.ssc",
                "Songs/16 - PHOENIX/18048.5 - Super Akuma Emperor/Super Akuma Emperor.ssc",
                "Songs/16 - PHOENIX/18252 - SWEET WONDERLAND/SWEET WONDERLAND.ssc",
                "Songs/16 - PHOENIX/18110 - Teddy Bear/Teddy Bear.ssc",
                "Songs/16 - PHOENIX/18006 - The Apocalypse/The Apocalypse.ssc",
                "Songs/16 - PHOENIX/18238.7.1 - this game does not exist/this game does not exist.ssc",
                "Songs/16 - PHOENIX/18128 - TOMBOY/TOMBOY.ssc",
                "Songs/16 - PHOENIX/18244 - TRICKL4SH 220/TRICKL4SH 220.ssc",
                "Songs/16 - PHOENIX/18065 - Uh-Heung/Uh-Heung.ssc",
                "Songs/16 - PHOENIX/18036.7 - Vanish 2 - Roar of the invisible dragon/Vanish 2 - Roar of the invisible dragon.ssc",
                "Songs/16 - PHOENIX/18000 - VECTOR/VECTOR.ssc",
                "Songs/16 - PHOENIX/18005 - Versailles/Versailles.ssc",
                "Songs/16 - PHOENIX/18243 - Viyella's Nightmare/Viyella's Nightmare.ssc",
                "Songs/16 - PHOENIX/18129 - WHISPER/WHISPER.ssc",
                "Songs/16 - PHOENIX/18A70 - wither garden/wither garden.ssc",
                
                // Remix and Short versions
                "Songs/16 - PHOENIX/18D10 - [Remix] DISTRICT V/[Remix] DISTRICT V.ssc",
                "Songs/16 - PHOENIX/18E10 - [Full Song] Teddy Bear/[Full Song] Teddy Bear.ssc",
                "Songs/16 - PHOENIX/18F60 - [Short Cut] Euphorianic/[Short Cut] Euphorianic.ssc",
                "Songs/16 - PHOENIX/18F65 - [Short Cut] Phoenix Opening/[Short Cut] Phoenix Opening.ssc",
                "Songs/16 - PHOENIX/18F70 - [Short Cut] Jupin/[Short Cut] Jupin.ssc",
                "Songs/16 - PHOENIX/18F75 - [Short Cut] Ghroth/[Short Cut] Ghroth.ssc",
                "Songs/16 - PHOENIX/18F76 - [Short Cut] Neo Catharsis/[Short Cut] Neo Catharsis.ssc",
                "Songs/16 - PHOENIX/18F77 - [Short Cut] Hymn of Golden Glory/[Short Cut] Hymn of Golden Glory.ssc",
                "Songs/16 - PHOENIX/18F78 - [Short Cut] Halloween Party ~Multiverse~/[Short Cut] Halloween Party ~Multiverse~.ssc",
                "Songs/16 - PHOENIX/18F79 - [Short Cut] Stardream -Eurobeat Remix-/Stardream -Eurobeat Remix-.ssc",
                "Songs/16 - PHOENIX/18F80 - [Short Cut] PRiMA MATERiA/[Short Cut] PRiMA MATERiA.ssc",
                "Songs/16 - PHOENIX/18F81 - [Short Cut] DUEL/[Short Cut] DUEL.ssc",
                "Songs/16 - PHOENIX/18F82 - [Short Cut] Murdoch vs Otada/[Short Cut] Murdoch vs Otada.ssc",
                "Songs/16 - PHOENIX/18F83 - [Short Cut] Solve My Hurt/[Short Cut] Solve My Hurt.ssc"
            };

            var loadedCount = 0;

            foreach (var songPath in phoenixSongs)
            {
                try
                {
                    await using var stream = await FileSystem.OpenAppPackageFileAsync(songPath);
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync();

                    var song = SscParser.Parse(content, songPath);
                    if (song.Charts.Count > 0)
                    {
                        AddSong(song);
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load {songPath}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"🎵 Successfully loaded {loadedCount} Phoenix songs");

            // If no Phoenix songs loaded, try the demo
            if (loadedCount == 0)
            {
                await LoadDemoFallback();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Error loading Phoenix songs: {ex.Message}");
            await LoadDemoFallback();
        }
    }

    private async Task LoadDemoFallback()
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("phoenix_demo.ssc");
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            AddSong(SscParser.Parse(content, "phoenix_demo.ssc"));
            System.Diagnostics.Debug.WriteLine("✅ Demo song loaded as fallback");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", "Could not load any songs. Please check that song files are properly embedded.", "OK");
        }
    }

    private async void OnLoadDemoClicked(object? sender, EventArgs e)
    {
        SongList.Clear();
        HasSelection = false;
        await LoadPhoenixSongsAsync();
    }

    private async void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select any file in the folder to load all .ssc files from that folder"
            });
            if (result is null) return;

            var folderPath = Path.GetDirectoryName(result.FullPath);
            if (string.IsNullOrEmpty(folderPath)) return;

            SongList.Clear();
            HasSelection = false;

            var sscFiles = Directory.GetFiles(folderPath, "*.ssc", SearchOption.AllDirectories);
            foreach (var sscPath in sscFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(sscPath);
                    var song = SscParser.Parse(content, sscPath);
                    if (song.Charts.Count > 0)
                        AddSong(song);
                }
                catch { /* skip invalid files */ }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private void AddSong(SscSong song)
    {
        if (SongList.Any(s => s.Song.SourcePath == song.SourcePath && song.SourcePath is not null))
            return;

        SongList.Add(new SongListItem { Song = song });
    }

    private void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SongListItem item)
            return;

        _selectedSong = item.Song;
        SelectedTitleLabel.Text = item.Song.Title;
        SelectedArtistLabel.Text = item.Song.Artist;

        ChartPicker.ItemsSource = item.Song.Charts
            .Select(c => c.ToString())
            .ToList();
        ChartPicker.SelectedIndex = item.Song.Charts.Count > 0 ? 0 : -1;
        _selectedChart = item.Song.Charts.Count > 0 ? item.Song.Charts[0] : null;

        HasSelection = _selectedSong is not null && _selectedChart is not null;
    }

    private void OnChartChanged(object? sender, EventArgs e)
    {
        if (_selectedSong is null || ChartPicker.SelectedIndex < 0) return;
        _selectedChart = _selectedSong.Charts[ChartPicker.SelectedIndex];
    }

    private async void OnPlayClicked(object? sender, EventArgs e)
    {
        if (_selectedSong is null || _selectedChart is null) return;

        // Pass data via query parameters and Shell navigation
        var songData = System.Text.Json.JsonSerializer.Serialize(new GamePageData
        {
            SongTitle = _selectedSong.Title,
            SongArtist = _selectedSong.Artist,
            SongSourcePath = _selectedSong.SourcePath,
            SongMusicPath = _selectedSong.MusicPath,
            SongBackgroundPath = _selectedSong.BackgroundPath,
            SongOffsetSeconds = _selectedSong.OffsetSeconds,
            ChartIndex = Array.IndexOf(_selectedSong.Charts.ToArray(), _selectedChart)
        });

        await Shell.Current.GoToAsync($"GamePage?songData={Uri.EscapeDataString(songData)}");
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed class SongListItem
    {
        public required SscSong Song { get; init; }
        public string Title => Song.Title;
        public string Artist => Song.Artist;
        public string ChartSummary => string.Join(", ",
            Song.Charts.Select(c => $"{c.Difficulty} {c.Meter}"));
    }

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