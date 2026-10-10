using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace Waveify;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly LibraryData library = LibraryStore.Load();
    readonly PlayerSettings playerSettings = PlayerSettings.Load();
    bool initializingAudioSettings = true;
    DateTime lastSettingsSave = DateTime.MinValue;

    readonly ObservableCollection<SongRow> rows = new();
    readonly ObservableCollection<PlaylistRow> playlistRows = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    readonly MetadataEnricher metadataEnricher = new();
    IWavePlayer? output;
    MMDeviceEnumerator? audioDevices;
    readonly List<MMDevice> availableOutputs = new();
    string? selectedOutputId;
    bool updatingProgress;
    bool refreshingPlaylistRows;
    AudioFileReader? reader;
    EqualizerProvider? equalizer;
    string sortMode = "Playlist order";
    bool recentOnly, historyOnly, unplayedOnly, duplicatesOnly;
    Point dragStart;
    SongRow? draggedSong;
    Song? current;
    List<Song> queue = new();
    readonly ObservableCollection<SongRow> queueRows = new();
    bool gridView;
    string artistFilter = "", albumFilter = "", genreFilter = "";
    bool likedFilter, localFilter;
    uint? yearFilter;
    Playlist? selectedPlaylist;
    bool likedOnly, shuffle, dragging;
    int repeatMode; // 0: off, 1: playlist, 2: one
    int playbackGeneration;
    bool restoredPositionConsumed;
    readonly Random random = new();
    public MainWindow()
    {
        InitializeComponent();
        MusicOrganizer.SetCustomRoot(playerSettings.MusicLibraryPath);
        Volume.Value = playerSettings.Volume;
        selectedOutputId = playerSettings.OutputDeviceId;
        SongList.ItemsSource = rows;
        PlaylistList.ItemsSource = playlistRows;
        QueueList.ItemsSource = queueRows;
        SongGrid.ItemsSource = rows;
        ArtistFilter.Text = playerSettings.ArtistFilter;
        AlbumFilter.Text = playerSettings.AlbumFilter;
        GenreFilter.Text = playerSettings.GenreFilter;
        YearFilter.Text = playerSettings.YearFilter;
        LikedFilter.IsChecked = playerSettings.LikedFilter;
        LocalFilter.IsChecked = playerSettings.LocalFilter;
        ApplyFilters_Click(this, new RoutedEventArgs());
        SortBox.SelectedIndex = 0;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        timer.Tick += (_, _) => UpdateProgress();
        timer.Start();
        Refresh();
        RefreshAudioDevices();
        initializingAudioSettings = false;
        repeatMode = Math.Clamp(playerSettings.RepeatMode, 0, 2);
        UpdateRepeatButton();
        UpdateShuffleButtons();
        UpdateCurrentLikeButton();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => { SaveAudioSettings(); Stop(); timer.Stop(); http.Dispose(); metadataEnricher.Dispose(); foreach (var d in availableOutputs) d.Dispose(); audioDevices?.Dispose(); };
    }
    async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RescanLibraryAsync(false);
        RestoreLastTrack();
        await CheckDependenciesAsync();
    }
    void RestoreLastTrack()
    {
        if (playerSettings.LastTrackId is not Guid id) return;
        var song = library.Songs.FirstOrDefault(x => x.Id == id && File.Exists(x.FilePath));
        if (song == null) return;
        // Display the last song without starting audio automatically.
        current = song;
        NowTitle.Text = song.Title;
        NowArtist.Text = song.Artist;
        NowArtwork.Source = LoadArtwork(song);
        UpdateCurrentLikeButton();
        queue = library.Songs.Where(x => File.Exists(x.FilePath)).ToList();
        Elapsed.Text = FormatPlaybackTime(TimeSpan.FromSeconds(Math.Max(0, playerSettings.LastPositionSeconds)));
        Total.Text = FormatPlaybackTime(TimeSpan.FromSeconds(Math.Max(0, song.DurationSeconds)));
        Progress.Value = song.DurationSeconds > 0
            ? Math.Clamp(playerSettings.LastPositionSeconds / song.DurationSeconds * 100, 0, 100) : 0;
    }

    async void SetupTools_Click(object sender, RoutedEventArgs e) => await CheckDependenciesAsync(true);

    async Task CheckDependenciesAsync(bool manual = false)
    {
        var missing = new[] { "yt-dlp", "deno", "sunnify", "ffmpeg" }.Where(name => !DependencyInstaller.IsAvailable(name)).ToList();
        if (missing.Count == 0)
        {
            if (manual) MessageBox.Show(this, "yt-dlp, Deno, Sunnify and FFmpeg are already available.", "Waveify tools");
            return;
        }
        var answer = MessageBox.Show(this,
            "Missing optional tools: " + string.Join(", ", missing) + ".\n\n" +
            "Download their Windows executables from the official GitHub releases into your local Waveify tools folder? " +
            "Waveify verifies release SHA-256 checksums before installing. These are third-party programs. " +
            "You can decline and still use the local music player.\n\n" +
            "FFmpeg is installed from an official Windows build. Large releases may take time and disk space.",
            "Set up download tools", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var setup = new ToolSetupProgressWindow { Owner = this };
        setup.Show();
        try
        {
            for (int i = 0; i < missing.Count; i++)
            {
                if (setup.Cancellation.IsCancellationRequested) break;
                string name = missing[i];
                setup.SetCurrentTool(i + 1, missing.Count, name);
                try
                {
                    var reporter = new Progress<InstallProgress>(setup.Report);
                    await DependencyInstaller.InstallAsync(name, reporter, setup.Cancellation.Token);
                    setup.Report(new InstallProgress(name, "Installed"));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    setup.Report(new InstallProgress(name, "Failed: " + ex.Message));
                    MessageBox.Show(setup, $"Could not install {name}: {ex.Message}", "Dependency setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally { setup.Finish(); }
        Refresh();
    }
    async void RescanLibrary_Click(object sender, RoutedEventArgs e) => await RescanLibraryAsync(true);

    async Task RescanLibraryAsync(bool showResult)
    {
        try
        {
            var removed = RemoveInvalidLibrarySongs();
            var paths = library.Songs.Select(s => s.FilePath).ToArray();
            var missing = await Task.Run(() => LibraryRecovery.FindMissing(paths));
            foreach (var song in missing) library.Songs.Add(song);
            if (missing.Count > 0 || removed > 0) Save();
            // Always show the full library after a recovery scan, not a playlist/search filter.
            selectedPlaylist = null;
            likedOnly = false;
            PlaylistList.SelectedItem = null;
            SearchBox.Clear();
            Refresh();
            if (showResult)
                MessageBox.Show(this,
                    $"Found {missing.Count} previously unlisted songs.\nRemoved {removed} invalid library entries.\n\nYour files were not moved or deleted.",
                    "Rescan library");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Unable to rescan music folders: " + ex.Message, "Waveify", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Save() => LibraryStore.Save(library);
    bool RemoveSongFromLibrary(Song song)
    {
        bool removed = library.Songs.Remove(song);
        library.PlaybackHistory.RemoveAll(id => id == song.Id);
        queue.RemoveAll(s => s.Id == song.Id);
        RefreshQueueRows();
        foreach (var p in library.Playlists) p.SongIds.Remove(song.Id);
        if (current?.Id == song.Id) { Stop(); current = null; }
        return removed;
    }

    int RemoveInvalidLibrarySongs()
    {
        var invalid = library.Songs
            .Where(song => string.IsNullOrWhiteSpace(song.FilePath) || !File.Exists(song.FilePath))
            .ToList();
        foreach (var song in invalid) RemoveSongFromLibrary(song);
        return invalid.Count;
    }

    void RefreshPlaylistArtwork()
    {
        if (refreshingPlaylistRows) return;
        refreshingPlaylistRows = true;
        try
        {
            var selected = selectedPlaylist;
            playlistRows.Clear();
            foreach (var p in library.Playlists)
            {
                var first = p.SongIds.Select(id => library.Songs.FirstOrDefault(s => s.Id == id))
                    .FirstOrDefault(song => song != null);
                BitmapImage? cover = null;
                if (!string.IsNullOrWhiteSpace(p.CoverPath) && File.Exists(p.CoverPath))
                {
                    try { cover = new BitmapImage(new Uri(p.CoverPath)); } catch (Exception ex) { Debug.WriteLine(ex); }
                }
                playlistRows.Add(new PlaylistRow(p, cover ?? (first == null ? null : ArtworkResolver.Load(first))));
            }
            if (selected != null)
                PlaylistList.SelectedItem = playlistRows.FirstOrDefault(r => ReferenceEquals(r.Playlist, selected));
        }
        finally { refreshingPlaylistRows = false; }
    }

    void Refresh()
    {
        RefreshPlaylistArtwork();
        IEnumerable<Song> songs = library.Songs;
        if (selectedPlaylist != null) songs = songs.Where(s => selectedPlaylist.SongIds.Contains(s.Id));
        if (likedOnly || likedFilter) songs = songs.Where(s => s.Liked);
        if (localFilter) songs = songs.Where(s => File.Exists(s.FilePath));
        if (yearFilter != null) songs = songs.Where(s => s.Year == yearFilter);
        if (artistFilter.Length > 0) songs = songs.Where(s => s.Artist.Contains(artistFilter, StringComparison.OrdinalIgnoreCase));
        if (albumFilter.Length > 0) songs = songs.Where(s => s.Album.Contains(albumFilter, StringComparison.OrdinalIgnoreCase));
        if (genreFilter.Length > 0) songs = songs.Where(s => s.Genre.Contains(genreFilter, StringComparison.OrdinalIgnoreCase));
        if (recentOnly) songs = songs.OrderByDescending(s => s.DateAddedUtc ?? DateTime.MinValue).Take(50);
        if (historyOnly) songs = library.PlaybackHistory.Select(id => library.Songs.FirstOrDefault(s => s.Id == id)).OfType<Song>();
        if (unplayedOnly) songs = songs.Where(s => s.PlayCount == 0);
        if (duplicatesOnly)
        {
            var duplicates = library.Songs.GroupBy(s => (s.Title.Trim().ToUpperInvariant(), s.Artist.Trim().ToUpperInvariant()))
                .Where(g => g.Count() > 1).SelectMany(g => g).Select(s => s.Id).ToHashSet();
            songs = songs.Where(s => duplicates.Contains(s.Id));
        }
        var search = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(search)) songs = songs.Where(s => (s.Title + " " + s.Artist + " " + s.Album).Contains(search, StringComparison.OrdinalIgnoreCase));
        songs = sortMode switch
        {
            "Title" => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
            "Artist" => songs.OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Title),
            "Date added" => songs.OrderByDescending(s => s.DateAddedUtc ?? DateTime.MinValue),
            _ when selectedPlaylist != null && !recentOnly && !historyOnly => songs.OrderBy(s => selectedPlaylist.SongIds.IndexOf(s.Id)),
            _ => songs
        };
        var previousId = (SongList.SelectedItem as SongRow)?.Song.Id;
        rows.Clear();
        int n = 1;
        foreach (var song in songs) rows.Add(new SongRow(song, n++));
        if (previousId != null) SongList.SelectedItem = rows.FirstOrDefault(s => s.Song.Id == previousId);
        HomeShelves.Visibility = selectedPlaylist == null && !likedOnly && !recentOnly && !historyOnly && !unplayedOnly && !duplicatesOnly && string.IsNullOrWhiteSpace(search)
            ? Visibility.Visible : Visibility.Collapsed;
        if (HomeShelves.Visibility == Visibility.Visible)
        {
            var valid = library.Songs.Where(x => !string.IsNullOrWhiteSpace(x.FilePath) && System.IO.File.Exists(x.FilePath)).ToList();
            ArtistShelves.ItemsSource = valid.GroupBy(x => string.IsNullOrWhiteSpace(x.Artist) ? "Unknown artist" : x.Artist, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key).Select(g => new MusicShelf(g.Key, g.Select((song, i) => new SongRow(song, i + 1)).ToList())).ToList();
            GenreShelves.ItemsSource = valid.GroupBy(x => string.IsNullOrWhiteSpace(x.Genre) ? "Unknown genre" : x.Genre, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key).Select(g => new MusicShelf(g.Key, g.Select((song, i) => new SongRow(song, i + 1)).ToList())).ToList();
        }
        AlbumShelves.ItemsSource = library.Songs.Where(s => File.Exists(s.FilePath))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Album) ? "Unknown album" : s.Album, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key).Select(g => new MusicShelf(g.Key, g.Select((song, i) => new SongRow(song, i + 1)).ToList())).ToList();
        Heading.Text = selectedPlaylist?.Name ?? (duplicatesOnly ? "Possible Duplicates" : unplayedOnly ? "Unplayed Songs" : recentOnly ? "Recently Added" : historyOnly ? "Recently Played" : likedOnly ? "Liked Songs" : "Library");
        SubHeading.Text = $"{rows.Count} tracks · Double-click a local track to play";
        RefreshQueueRows();
    }
    void ShelfTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is SongRow row)
            StartSong(row.Song);
    }
    void Home_Click(object sender, RoutedEventArgs e) { selectedPlaylist = null; likedOnly = recentOnly = historyOnly = unplayedOnly = duplicatesOnly = false; PlaylistList.SelectedItem = null; Refresh(); }
    void Liked_Click(object sender, RoutedEventArgs e) { selectedPlaylist = null; likedOnly = recentOnly = historyOnly = unplayedOnly = duplicatesOnly = false; likedOnly = true; PlaylistList.SelectedItem = null; Refresh(); }
    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (rows != null) Refresh(); }
    void ShowPlaylist(Playlist playlist)
    {
        selectedPlaylist = playlist;
        likedOnly = recentOnly = historyOnly = unplayedOnly = duplicatesOnly = false;
        SearchBox.Clear();
        PlaylistList.SelectedItem = playlistRows.FirstOrDefault(r => ReferenceEquals(r.Playlist, playlist));
        Refresh();
    }
    void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingPlaylistRows && PlaylistList.SelectedItem is PlaylistRow { Playlist: var p } && !ReferenceEquals(selectedPlaylist, p))
            ShowPlaylist(p);
    }
    void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, "Create playlist", "Playlist name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new Playlist { Name = name.Trim() };
        library.Playlists.Add(p);
        RefreshPlaylistArtwork(); PlaylistList.SelectedItem = playlistRows.FirstOrDefault(r => ReferenceEquals(r.Playlist, p)); Save(); Refresh();
    }
    void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not SongRow selected) { MessageBox.Show("Select a track first."); return; }
        if (library.Playlists.Count == 0) { MessageBox.Show("Create a playlist first."); return; }
        string? name = PromptDialog.Ask(this, "Add to playlist", "Enter an existing playlist name:", library.Playlists[0].Name);
        var p = library.Playlists.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p == null) { MessageBox.Show("Playlist not found."); return; }
        if (!p.SongIds.Contains(selected.Song.Id))
        {
            p.SongIds.Add(selected.Song.Id);
            Save();
            Refresh();
        }
    }
    // Works across the complete width of every song row, including artwork,
    // title, album, and otherwise empty cells. Do not use a stale selection.
    // Share one wheel policy across the sidebar, song list and central library.
    // This uses line scrolling so a virtualized ListView keeps its logical units.
    static bool TryWheelScroll(ScrollViewer scroll, int delta)
    {
        if (scroll.ScrollableHeight <= 0 || delta == 0) return false;
        if (delta > 0 && scroll.VerticalOffset <= 0) return false;
        if (delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight) return false;

        int lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (delta > 0) scroll.PageUp(); else scroll.PageDown();
        }
        else
        {
            int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / 120.0 * Math.Max(1, lines)));
            for (int i = 0; i < count; i++)
            {
                if (delta > 0) scroll.LineUp(); else scroll.LineDown();
            }
        }
        return true;
    }

    static ScrollViewer? FindNestedScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindNestedScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    ScrollViewer? songListScrollViewer;

    void SidebarScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TryWheelScroll(SidebarScrollViewer, e.Delta)) e.Handled = true;
    }

    // Catch wheel events before child ListView / ScrollViewer controls consume
    // them. At the edge of the song list, scrolling continues in the center.
    void CenterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SongList.IsVisible && SongList.IsMouseOver)
        {
            songListScrollViewer ??= FindNestedScrollViewer(SongList);
            if (songListScrollViewer != null && TryWheelScroll(songListScrollViewer, e.Delta))
            {
                e.Handled = true;
                return;
            }
        }
        if (SongGridScrollViewer.IsVisible && SongGridScrollViewer.IsMouseOver &&
            TryWheelScroll(SongGridScrollViewer, e.Delta))
        {
            e.Handled = true;
            return;
        }
        if (TryWheelScroll(CenterScrollViewer, e.Delta)) e.Handled = true;
    }

    void SongList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindSongRow(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    static ListViewItem? FindSongRow(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ListViewItem item) return item;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    void SongList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = Mouse.DirectlyOver as DependencyObject;
        var container = FindSongRow(hit);
        if (container?.DataContext is not SongRow selected)
        {
            e.Handled = true;
            return;
        }
        container.IsSelected = true;
        // Attach to that row rather than the entire ListView.
        container.ContextMenu = BuildSongMenu(selected.Song);
        SongList.ContextMenu = null;
        container.ContextMenu.PlacementTarget = container;
        container.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    // Shared by Home artist/genre shelves, featured cards, and Up Next.
    // The menu belongs to the clicked song card, not a stale ListView selection.
    void SongCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SongRow row)
            return;

        var menu = BuildSongMenu(row.Song);
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.MousePoint;
        element.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    ContextMenu BuildSongMenu(Song song)
    {
        var menu = new ContextMenu();
        var like = new MenuItem { Header = song.Liked ? "♥ Unlike song" : "♡ Like song" };
        like.Click += (_, _) => { ToggleLike(song); };
        menu.Items.Add(like);
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add to playlist" };
        if (library.Playlists.Count == 0)
            add.Items.Add(new MenuItem { Header = "No playlists yet", IsEnabled = false });
        foreach (var playlist in library.Playlists)
        {
            var item = new MenuItem
            {
                Header = playlist.Name,
                IsCheckable = true,
                IsChecked = playlist.SongIds.Contains(song.Id)
            };
            item.Click += (_, _) =>
            {
                if (!playlist.SongIds.Contains(song.Id))
                {
                    playlist.SongIds.Add(song.Id);
                    Save();
                    Refresh();
                }
            };
            add.Items.Add(item);
        }
        menu.Items.Add(add);
        var playNext = new MenuItem { Header = "Play next" };
        playNext.Click += (_, _) =>
        {
            if (current == null) { StartSong(song); return; }
            queue.RemoveAll(s => s.Id == song.Id && s.Id != current.Id);
            var position = queue.FindIndex(s => s.Id == current.Id);
            queue.Insert(Math.Max(0, position + 1), song);
            RefreshQueueRows();
        };
        menu.Items.Add(playNext);
        var copy = new MenuItem { Header = "Copy song details" };
        copy.Click += (_, _) => Clipboard.SetText($"{song.Title} — {song.Artist} | {song.Album}");
        menu.Items.Add(copy);
        if (!string.IsNullOrWhiteSpace(song.FilePath) && File.Exists(song.FilePath))
        {
            var location = new MenuItem { Header = "Show file in Explorer" };
            location.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{song.FilePath}\"") { UseShellExecute = true });
            menu.Items.Add(location);
        }
        var edit = new MenuItem { Header = "Edit metadata" };
        edit.Click += (_, _) => EditMetadata(song);
        menu.Items.Add(edit);
        if (selectedPlaylist != null && selectedPlaylist.SongIds.Contains(song.Id))
        {
            var removeFromPlaylist = new MenuItem { Header = "Remove from this playlist" };
            removeFromPlaylist.Click += (_, _) => { selectedPlaylist.SongIds.Remove(song.Id); Save(); Refresh(); };
            menu.Items.Add(removeFromPlaylist);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Remove from library" };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove '{song.Title}' from your library? (Audio files will not be deleted.)",
                    "Remove track", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RemoveSongFromLibrary(song);
            Save();
            Refresh();
        };
        menu.Items.Add(remove);
        return menu;
    }

    void Like_Click(object sender, RoutedEventArgs e)
    { if (SongList.SelectedItem is SongRow row) ToggleLike(row.Song); }
    void CurrentLike_Click(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        ToggleLike(current);
    }
    void ToggleLike(Song song)
    {
        song.Liked = !song.Liked;
        Save();
        Refresh();
        UpdateCurrentLikeButton();
    }
    void UpdateCurrentLikeButton()
    {
        bool liked = current?.Liked == true;
        BottomLikeButton.IsEnabled = current != null;
        BottomLikeButton.ToolTip = current == null
            ? "No song playing"
            : liked ? "Unlike current song" : "Like current song";
        BottomLikeIcon.Foreground = liked
            ? (System.Windows.Media.Brush)FindResource("WaveAccent")
            : System.Windows.Media.Brushes.White;
    }
    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not SongRow row) return;
        if (MessageBox.Show($"Remove '{row.Title}' from your library? (Audio files will not be deleted.)", "Remove track", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        RemoveSongFromLibrary(row.Song);
        Save(); Refresh();
    }
    async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import audio files", Filter = "Audio files|*.mp3;*.flac;*.wav;*.aac;*.m4a;*.wma;*.aiff|All files|*.*", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        int count = 0;
        foreach (string file in dialog.FileNames)
        {
            if (!ImportLocal(file)) continue;
            count++;
            await EnrichImportedAsync();
        }
        Save(); Refresh(); MessageBox.Show($"Added {count} tracks.");
    }
    bool ImportLocal(string file, string? sourceUrl = null)
    {
        // yt-dlp has already finished at this point. Do not confuse a library
        // or tag error with a failed network download.
        string stage = "Locating downloaded audio";
        try
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file))
                throw new FileNotFoundException("Downloader output disappeared before library import", file);
            if (library.Songs.Any(s => !string.IsNullOrWhiteSpace(s.FilePath) &&
                string.Equals(Path.GetFullPath(s.FilePath), file, StringComparison.OrdinalIgnoreCase))) return false;

            var song = new Song
            {
                FilePath = file, SourceUrl = sourceUrl ?? "", DateAddedUtc = DateTime.UtcNow,
                Title = Path.GetFileNameWithoutExtension(file)
            };
            stage = "Reading embedded song tags";
            try
            {
                using var tag = TagLib.File.Create(file);
                if (!string.IsNullOrWhiteSpace(tag.Tag.Title)) song.Title = tag.Tag.Title;
                song.Artist = tag.Tag.FirstPerformer ?? "Unknown artist";
                song.Album = tag.Tag.Album ?? "";
                song.Genre = tag.Tag.FirstGenre ?? "";
                song.DurationSeconds = (int)tag.Properties.Duration.TotalSeconds;
                if (tag.Tag.Pictures.Length > 0 && tag.Tag.Pictures[0].Data.Count > 0)
                {
                    var artDirectory = Path.Combine(LibraryStore.Folder, "artwork");
                    Directory.CreateDirectory(artDirectory);
                    song.ArtworkPath = Path.Combine(artDirectory, song.Id + ".jpg");
                    File.WriteAllBytes(song.ArtworkPath, tag.Tag.Pictures[0].Data.Data);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Broken/unsupported tags must not discard a successfully
                // downloaded playable MP3.
                Debug.WriteLine("Tag reading skipped: " + ex);
            }

            stage = "Copying audio into managed music library";
            song.FilePath = MusicOrganizer.Organize(file, song);
            if (!File.Exists(song.FilePath))
                throw new IOException("Managed audio file does not exist after copying: " + song.FilePath);

            stage = "Saving album artwork";
            try { MusicOrganizer.SaveAlbumArtwork(song); }
            catch (IOException ex) { Debug.WriteLine("Album artwork skipped: " + ex); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine("Album artwork skipped: " + ex); }

            stage = "Adding local song to library";
            library.Songs.Add(song);
            // Update the visible library and playlist artwork as each import completes.
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            throw new IOException($"Local import failed while {stage}. Source: '{file}'. {ex.Message}", ex);
        }
    }
    async Task EnrichImportedAsync()
    {
        var song = library.Songs.LastOrDefault();
        if (song == null || !File.Exists(song.FilePath)) return;
        try
        {
            SubHeading.Text = $"Looking up metadata: {song.Title}…";
            if (await metadataEnricher.EnrichAsync(song))
            {
                // Keep the audio at its current path to avoid changing paths during playback.
                Save(); Refresh();
            }
        }
        catch (Exception ex) { Debug.WriteLine("Metadata enrichment skipped: " + ex.Message); }
    }

    async void EnrichMetadata_Click(object sender, RoutedEventArgs e)
    {
        var tracks = SongList.SelectedItem is SongRow selected
            ? new List<Song> { selected.Song }
            : library.Songs.Where(x => File.Exists(x.FilePath)).ToList();
        if (tracks.Count == 0) { MessageBox.Show("No local songs to enrich."); return; }
        if (MessageBox.Show(this, $"Look up metadata for {tracks.Count} local song(s)? Existing tags will be preserved.",
            "Enrich metadata", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int matched = 0, failed = 0;
        foreach (var song in tracks)
        {
            try
            {
                SubHeading.Text = $"Checking metadata: {song.Title}…";
                if (await metadataEnricher.EnrichAsync(song)) matched++;
            }
            catch (Exception ex) { failed++; Debug.WriteLine(ex); }
            Save();
        }
        Refresh();
        MessageBox.Show(this, $"Matched {matched} of {tracks.Count} songs. Errors: {failed}. Uncertain matches were skipped.", "Metadata enrichment");
    }

    void OrganizeLibrary_Click(object sender, RoutedEventArgs e)
    {
        // Close playback handles so Windows can relocate/copy currently playing files.
        Stop();
        int organized = 0, missing = 0;
        foreach (var song in library.Songs.Where(s => !string.IsNullOrWhiteSpace(s.FilePath)))
        {
            if (!File.Exists(song.FilePath)) { missing++; continue; }
            try
            {
                var oldPath = song.FilePath;
                song.FilePath = MusicOrganizer.Organize(oldPath, song);
                MusicOrganizer.SaveAlbumArtwork(song);
                organized++;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not organize '{song.Title}': {ex.Message}");
            }
        }
        Save(); Refresh();
        MessageBox.Show($"Organized {organized} tracks into {MusicOrganizer.Root}. Missing files: {missing}. Original files were not deleted.");
    }

    void MusicFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where Waveify stores imported MP3s",
            InitialDirectory = Directory.Exists(MusicOrganizer.Root)
                ? MusicOrganizer.Root
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Directory.CreateDirectory(dialog.FolderName);
            MusicOrganizer.SetCustomRoot(dialog.FolderName);
            playerSettings.MusicLibraryPath = MusicOrganizer.Root;
            playerSettings.Save();
            MessageBox.Show(this,
                "Future imports and Sunnify/yt-dlp downloads will be stored in:\n\n" + MusicOrganizer.Root +
                "\n\nExisting library files will stay where they are unless you organize them again.",
                "Waveify music folder");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, "Could not use that folder: " + ex.Message, "Waveify music folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void ImportLink_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Ask(this, "Import music link", "Paste a Spotify, YouTube, SoundCloud or direct HTTPS audio link:");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        { MessageBox.Show("Enter a valid HTTPS link."); return; }
        try
        {
            if (IsHost(uri.Host.ToLowerInvariant(), "spotify.com"))
            {
                if (MessageBox.Show("Only download music you own or have permission to save. Continue using Sunnify?", "Authorized download", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var progressWindow = new SongDownloadProgressWindow(this, 1);
                progressWindow.Show();
                IReadOnlyList<string> files;
                try { files = await SunnifyDownloader.DownloadAsync(input, status => Dispatcher.Invoke(() => SubHeading.Text = status), progressWindow.Report); }
                finally { progressWindow.Finish(1); }
                int count = 0;
                foreach (var file in files) if (ImportLocal(file, input)) { count++; await EnrichImportedAsync(); }
                Save(); Refresh();
                MessageBox.Show($"Imported {count} tracks into your offline Waveify library.");
                return;
            }
            string host = uri.Host.ToLowerInvariant();
            if (IsHost(host, "youtube.com") || IsHost(host, "youtu.be") || IsHost(host, "soundcloud.com") || IsHost(host, "spotify.com"))
            {
                // Public oEmbed is used only for optional display metadata; no protected audio is extracted.
                string endpoint = IsHost(host, "spotify.com") ? "https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(input)
                  : IsHost(host, "soundcloud.com") ? "https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(input)
                  : "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(input);
                var song = new Song { Title = input, SourceUrl = input, Artist = "Online link" };
                try
                {
                    using var response = await http.GetAsync(endpoint);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var root = document.RootElement;
                    if (root.TryGetProperty("title", out var title)) song.Title = title.GetString() ?? song.Title;
                    if (root.TryGetProperty("author_name", out var artist)) song.Artist = artist.GetString() ?? song.Artist;
                    if (root.TryGetProperty("thumbnail_url", out var cover)) song.ArtworkUrl = cover.GetString() ?? "";
                }
                catch (HttpRequestException) { } catch (JsonException) { }
                library.Songs.Add(song); Save(); Refresh();
                MessageBox.Show("Added as an online link with available metadata. Double-click it to open the original platform. This link was added as an online bookmark.");
            }
            else if (new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma" }.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()))
            {
                if (MessageBox.Show("Only download audio you own or have permission to save. Do you have permission?", "Authorized download", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                var folder = Path.Combine(LibraryStore.Folder, "staging"); Directory.CreateDirectory(folder);
                string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                string filename = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
                try
                {
                    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > 150_000_000) throw new InvalidOperationException("File exceeds the 150 MB size limit.");
                    await using var source = await response.Content.ReadAsStreamAsync();
                    await using var target = File.Create(filename);
                    byte[] buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    { total += read; if (total > 150_000_000) throw new InvalidOperationException("File exceeds the 150 MB size limit."); await target.WriteAsync(buffer.AsMemory(0, read)); }
                    target.Close();
                    bool imported = ImportLocal(filename, input);
                    if (imported) await EnrichImportedAsync();
                    File.Delete(filename); // Finished copy: keep only managed library version.
                    Save(); Refresh();
                    MessageBox.Show(imported ? $"Downloaded and organized in {MusicOrganizer.Root}" : "The song is already in your library.");
                }
                catch { if (File.Exists(filename)) File.Delete(filename); throw; }
            }
            else MessageBox.Show("Supported: Spotify, YouTube and SoundCloud links for bookmarks/metadata; direct HTTPS audio-file URLs for authorized downloads.");
        }
        catch (Exception ex) { MessageBox.Show("Import failed: " + ex.Message); }
    }
    async void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var urls = BatchImportDialog.Ask(this);
        if (urls == null || urls.Length == 0) return;
        if (MessageBox.Show("Download only audio that you own or are authorized to save. Continue with the selected URLs?", "Permissions", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BatchButtonState(false);
        var progressWindow = new SongDownloadProgressWindow(this, urls.Length);
        progressWindow.Show();
        int completedUrls = 0;
        int saved = 0;
        var problems = new List<string>();
        try
        {
            foreach (var url in urls)
            {
                progressWindow.SetBatch(completedUrls, urls.Length, url);
                try
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                        throw new InvalidOperationException("Only HTTPS URLs are allowed.");
                    var host = uri.Host.ToLowerInvariant();
                    if (IsHost(host, "spotify.com"))
                    {
                        var files = await SunnifyDownloader.DownloadAsync(url, status => Dispatcher.Invoke(() => SubHeading.Text = status), progressWindow.Report);
                        foreach (var file in files)
                        {
                            if (ImportLocal(file, url)) { saved++; await EnrichImportedAsync(); }
                        }
                        continue;
                    }
                    if (IsHost(host, "youtube.com") || IsHost(host, "youtu.be") || IsHost(host, "soundcloud.com"))
                    {
                        var files = await AuthorizedDownloader.DownloadAsync(url, status => Dispatcher.Invoke(() => SubHeading.Text = status), progressWindow.Report);
                        foreach (var file in files)
                        {
                            try
                            {
                                if (ImportLocal(file, url)) { saved++; await EnrichImportedAsync(); }
                            }
                            finally { try { File.Delete(file); } catch (IOException) { } }
                        }
                        continue;
                    }
                    if (new[] { ".mp3", ".m4a", ".flac", ".wav", ".aac" }.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()))
                    {
                        var stage = Path.Combine(LibraryStore.Folder, "batch-staging");
                        Directory.CreateDirectory(stage);
                        var file = Path.Combine(stage, Guid.NewGuid().ToString("N") + Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());
                        try
                        {
                            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            if (response.Content.Headers.ContentLength > 150_000_000) throw new InvalidOperationException("File too large.");
                            await using var input = await response.Content.ReadAsStreamAsync();
                            await using (var outputFile = File.Create(file))
                            {
                                byte[] buffer = new byte[81920]; long total = 0; int n;
                                while ((n = await input.ReadAsync(buffer)) > 0)
                                {
                                    total += n;
                                    if (total > 150_000_000) throw new InvalidOperationException("File too large.");
                                    await outputFile.WriteAsync(buffer.AsMemory(0, n));
                                }
                            }
                            if (ImportLocal(file, url)) { saved++; await EnrichImportedAsync(); }
                        }
                        finally { if (File.Exists(file)) File.Delete(file); }
                        continue;
                    }
                    throw new NotSupportedException("Only supported music providers and direct audio URLs are accepted.");
                }
                catch (Exception ex) { problems.Add(url + ": " + ex.Message); }
                finally { completedUrls++; }
                Save(); Refresh();
            }
        }
        finally { progressWindow.Finish(completedUrls); BatchButtonState(true); Refresh(); }
        MessageBox.Show($"Imported {saved} playable files.\n" +
            (problems.Count > 0 ? "\nIssues:\n" + string.Join("\n", problems.Take(10)) : ""), "Batch import complete");
    }
    void BatchButtonState(bool enabled) => SubHeading.Text = enabled ? "Batch import finished · Local tracks are playable offline" : "Batch import in progress…";

    async Task AddOnlineBookmarkAsync(string url, string endpoint)
    {
        if (library.Songs.Any(x => x.SourceUrl == url)) return;
        var song = new Song { SourceUrl = url, Title = url, Artist = "Online link" };
        try
        {
            using var response = await http.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var name)) song.Title = name.GetString() ?? song.Title;
            if (root.TryGetProperty("author_name", out var artist)) song.Artist = artist.GetString() ?? song.Artist;
            if (root.TryGetProperty("thumbnail_url", out var cover)) song.ArtworkUrl = cover.GetString() ?? "";
        }
        catch (HttpRequestException) { } catch (JsonException) { }
        library.Songs.Add(song);
        Refresh();
    }
    async void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        var song = (SongList.SelectedItem as SongRow)?.Song ?? current;
        if (song == null) { MessageBox.Show("Select a song first."); return; }
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(song.FilePath) ?? LibraryStore.Folder,
                Path.GetFileNameWithoutExtension(song.FilePath.Length == 0 ? song.Id.ToString() : song.FilePath) + ".lrc");
            if (!File.Exists(path))
            {
                var lookup = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(song.Title) + "&artist_name=" + Uri.EscapeDataString(song.Artist);
                using var response = await http.GetAsync(lookup);
                if (!response.IsSuccessStatusCode) { MessageBox.Show("No lyrics available for this track."); return; }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var lyrics = root.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String ? synced.GetString() : null;
                lyrics ??= root.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString() : null;
                if (string.IsNullOrWhiteSpace(lyrics)) { MessageBox.Show("Lyrics aren't available for this song."); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, lyrics, Encoding.UTF8);
            }
            song.LyricsPath = path;
            Save();
            var viewer = new Window { Owner = this, Title = "Lyrics · " + song.Title, Width = 550, Height = 650 };
            Theme.ApplyWindow(viewer);
            viewer.Content = new TextBox { Text = await File.ReadAllTextAsync(path), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Theme.AppBg, Foreground = Theme.Text,
                BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), FontSize = 16, Padding = new Thickness(24) };
            viewer.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("Lyrics lookup failed: " + ex.Message); }
    }
    static bool IsHost(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (SongList.SelectedItem is SongRow row) StartSong(row.Song); }
    void PlayAll_Click(object sender, RoutedEventArgs e)
    { if (rows.FirstOrDefault(r => File.Exists(r.Song.FilePath)) is { } row) StartSong(row.Song); else MessageBox.Show("No playable local tracks in this view."); }
    void StartSong(Song song, bool preserveQueue = false)
    {
        if (string.IsNullOrEmpty(song.FilePath) || !File.Exists(song.FilePath))
        {
            if (Uri.TryCreate(song.SourceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            else if (MessageBox.Show(this,
                         "This track is missing its local audio file. Remove it from your library?",
                         "Missing audio file", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                RemoveSongFromLibrary(song);
                Save();
                Refresh();
            }
            return;
        }
        try
        {
            // Snapshot the old playback state before opening another audio file.
            if (current != null && reader != null)
                playerSettings.LastPositionSeconds = reader.CurrentTime.TotalSeconds;
            Stop();
            // User selections should play through the visible view, such as the
            // selected playlist. Auto-advance and repeat keep the active queue.
            if (!preserveQueue)
                queue = rows.Select(r => r.Song).Where(x => File.Exists(x.FilePath)).ToList();
            RefreshQueueRows();
            reader = new AudioFileReader(song.FilePath) { Volume = (float)Volume.Value };
            if (!restoredPositionConsumed && playerSettings.LastTrackId == song.Id
                && playerSettings.LastPositionSeconds > 0
                && playerSettings.LastPositionSeconds < reader.TotalTime.TotalSeconds - 2)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(playerSettings.LastPositionSeconds);
                // Resume at most once per application launch, not on every repeat.
            }
            restoredPositionConsumed = true;
            output = CreateAudioOutput();
            equalizer = new EqualizerProvider(reader);
            equalizer.SetBands(EqualizerEnabled.IsChecked == true ? EqualizerSlider.Value : 0, EqualizerEnabled.IsChecked == true ? MidSlider.Value : 0, EqualizerEnabled.IsChecked == true ? TrebleSlider.Value : 0);
            output.Init(new SampleToWaveProvider(equalizer));
            int generation = playbackGeneration;
            var playingReader = reader;
            output.PlaybackStopped += (_, args) => Dispatcher.BeginInvoke(() =>
            {
                // Ignore stale callbacks from a previously stopped/disposed player.
                if (generation != playbackGeneration || reader != playingReader) return;
                if (args.Exception != null)
                {
                    SetPlayIcon(false);
                    return;
                }
                if (playingReader.Position >= playingReader.Length)
                    NextTrack(true);
                else
                    SetPlayIcon(false);
            });
            current = song; NowTitle.Text = song.Title; NowArtist.Text = song.Artist;
            library.PlaybackHistory.Remove(song.Id);
            library.PlaybackHistory.Insert(0, song.Id);
            song.PlayCount++;
            song.LastPlayedUtc = DateTime.UtcNow;
            if (library.PlaybackHistory.Count > 100) library.PlaybackHistory.RemoveRange(100, library.PlaybackHistory.Count - 100);
            Save();
            playerSettings.LastTrackId = song.Id;
            NowArtwork.Source = LoadArtwork(song);
            UpdateCurrentLikeButton();
            Progress.Maximum = 100;
            updatingProgress = true;
            Progress.Value = reader.TotalTime.TotalSeconds > 0
                ? reader.CurrentTime.TotalSeconds / reader.TotalTime.TotalSeconds * 100 : 0;
            updatingProgress = false;
            Elapsed.Text = FormatPlaybackTime(reader.CurrentTime);
            Total.Text = FormatPlaybackTime(reader.TotalTime);
            output.Play();
            SetPlayIcon(true);
            SaveAudioSettings();
        }
        catch (Exception ex) { Stop(); MessageBox.Show("Playback error: " + ex.Message); }
    }
    BitmapImage? LoadArtwork(Song song) => ArtworkResolver.Load(song);
    void SetPlayIcon(bool isPlaying)
    {
        PlayGlyph.Symbol = isPlaying
            ? Wpf.Ui.Controls.SymbolRegular.Pause24
            : Wpf.Ui.Controls.SymbolRegular.Play24;
    }
    void Stop()
    {
        // Invalidate callbacks before disposing the old output device.
        playbackGeneration++;
        var oldOutput = output;
        output = null;
        if (oldOutput != null)
        {
            oldOutput.Stop();
            oldOutput.Dispose();
        }
        reader?.Dispose();
        reader = null;
        SetPlayIcon(false);
    }
    void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (output == null || reader == null)
        {
            if (current != null) StartSong(current);
            else PlayAll_Click(sender, e);
            return;
        }
        if (output.PlaybackState == PlaybackState.Playing)
        {
            output.Pause();
            SetPlayIcon(false);
        }
        else
        {
            if (reader.Position >= reader.Length) reader.CurrentTime = TimeSpan.Zero;
            output.Play();
            SetPlayIcon(true);
        }
        SaveAudioSettings();
    }
    void NextTrack(bool automatic = false)
    {
        if (queue.Count == 0 || current == null) { SetPlayIcon(false); return; }
        // Repeat-one affects automatic end-of-track playback, not the Next button.
        if (automatic && repeatMode == 2)
        {
            playerSettings.LastPositionSeconds = 0;
            StartSong(current, preserveQueue: true);
            return;
        }
        int idx = queue.FindIndex(x => x.Id == current.Id);
        if (idx < 0) { SetPlayIcon(false); return; }
        if (automatic && idx == queue.Count - 1 && repeatMode == 0 && !shuffle)
        {
            // End of queue: button becomes Play and progress remains at the end.
            SetPlayIcon(false);
            if (reader != null)
            {
                updatingProgress = true;
                Progress.Value = 100;
                updatingProgress = false;
                Elapsed.Text = Total.Text;
                playerSettings.LastPositionSeconds = 0;
            }
            playerSettings.Save();
            return;
        }
        int next;
        if (shuffle)
        {
            next = random.Next(queue.Count);
            if (queue.Count > 1 && next == idx)
                next = (next + 1) % queue.Count;
        }
        else next = (idx + 1) % queue.Count;
        playerSettings.LastPositionSeconds = 0;
        StartSong(queue[next], preserveQueue: true);
    }
    void Next_Click(object sender, RoutedEventArgs e) => NextTrack(false);
    void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (reader != null && reader.CurrentTime.TotalSeconds > 3) { reader.CurrentTime = TimeSpan.Zero; return; }
        if (queue.Count == 0 || current == null) return;
        int idx = queue.FindIndex(x => x.Id == current.Id);
        StartSong(queue[(idx - 1 + queue.Count) % queue.Count], preserveQueue: true);
    }
    void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        shuffle = !shuffle;
        UpdateShuffleButtons();
    }
    void UpdateShuffleButtons()
    {
        var brush = shuffle
            ? (System.Windows.Media.Brush)FindResource("WaveAccent")
            : System.Windows.Media.Brushes.White;
        ShuffleButton.Foreground = brush;
        BottomShuffleButton.Foreground = brush;
        ShuffleButton.ToolTip = shuffle ? "Shuffle on" : "Shuffle";
        BottomShuffleButton.ToolTip = shuffle ? "Shuffle on" : "Shuffle";
    }
    void Repeat_Click(object sender, RoutedEventArgs e)
    {
        repeatMode = (repeatMode + 1) % 3;
        UpdateRepeatButton();
        SaveAudioSettings();
    }
    void UpdateRepeatButton()
    {
        RepeatButton.ToolTip = repeatMode switch
        {
            1 => "Repeat playlist",
            2 => "Repeat one song",
            _ => "Repeat off"
        };
        RepeatButton.Content = repeatMode == 2 ? "↻₁" : "↻";
        RepeatButton.Foreground = repeatMode == 0
            ? System.Windows.Media.Brushes.White
            : (System.Windows.Media.Brush)FindResource("WaveAccent");
    }
    void SaveAudioSettings()
    {
        if (initializingAudioSettings) return;
        playerSettings.Volume = Volume.Value;
        playerSettings.OutputDeviceId = selectedOutputId;
        playerSettings.RepeatMode = repeatMode;
        if (current != null && reader != null)
        {
            playerSettings.LastTrackId = current.Id;
            playerSettings.LastPositionSeconds = reader.CurrentTime.TotalSeconds;
        }
        playerSettings.Save();
        lastSettingsSave = DateTime.UtcNow;
    }

    void UpdateProgress()
    {
        if (reader == null || dragging) return;
        if (output != null && output.PlaybackState == PlaybackState.Stopped && reader.Position >= reader.Length)
            SetPlayIcon(false);
        updatingProgress = true;
        Progress.Value = reader.TotalTime.TotalSeconds > 0 ? Math.Clamp(reader.CurrentTime.TotalSeconds / reader.TotalTime.TotalSeconds * 100, 0, 100) : 0;
        updatingProgress = false;
        Elapsed.Text = FormatPlaybackTime(reader.CurrentTime);
        if ((DateTime.UtcNow - lastSettingsSave).TotalSeconds >= 10)
            SaveAudioSettings();
    }
    // Seek uses percentages (0..100), independent of track duration.
    void SeekToPercentage(double percent)
    {
        if (reader == null) return;
        double seconds = reader.TotalTime.TotalSeconds * Math.Clamp(percent, 0, 100) / 100.0;
        reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        Elapsed.Text = FormatPlaybackTime(reader.CurrentTime);
        SaveAudioSettings();
    }
    // TotalMinutes prevents the time display wrapping back to zero after 59 minutes.
    static string FormatPlaybackTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }


    void Progress_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragging = true;
        // Clicking the empty track should jump there; clicking the thumb keeps normal dragging.
        if (e.OriginalSource is DependencyObject source && FindThumb(source) != null) return;
        SeekFromPointer((Slider)sender, e);
    }
    static Thumb? FindThumb(DependencyObject child)
    {
        for (DependencyObject? node = child; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is Thumb thumb) return thumb;
        return null;
    }
    void SeekFromPointer(Slider slider, MouseEventArgs e)
    {
        if (slider.ActualWidth <= 0) return;
        double x = e.GetPosition(slider).X;
        slider.Value = Math.Clamp(x / slider.ActualWidth * 100.0, 0, 100);
    }
    void Progress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider) SeekToPercentage(slider.Value);
        dragging = false;
    }
    void Progress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingProgress || reader == null) return;
        SeekToPercentage(e.NewValue);
    }
    void RefreshAudioDevices()
    {
        try
        {
            foreach (var device in availableOutputs) device.Dispose();
            availableOutputs.Clear();
            audioDevices?.Dispose();
            audioDevices = new MMDeviceEnumerator();
            var devices = audioDevices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var previouslySelected = selectedOutputId;
            OutputDeviceCombo.Items.Clear();
            OutputDeviceCombo.Items.Add("System default");
            foreach (var device in devices)
            {
                availableOutputs.Add(device);
                OutputDeviceCombo.Items.Add(device.FriendlyName);
            }
            int match = availableOutputs.FindIndex(d => d.ID == previouslySelected);
            initializingAudioSettings = true;
            try { OutputDeviceCombo.SelectedIndex = match < 0 ? 0 : match + 1; }
            finally { initializingAudioSettings = false; }
        }
        catch (Exception ex)
        {
            OutputDeviceCombo.Items.Clear();
            OutputDeviceCombo.Items.Add("System default");
            OutputDeviceCombo.SelectedIndex = 0;
            Debug.WriteLine("Audio device enumeration: " + ex);
        }
    }
    IWavePlayer CreateAudioOutput()
    {
        if (selectedOutputId != null)
        {
            var device = availableOutputs.FirstOrDefault(d => d.ID == selectedOutputId);
            if (device != null) return new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        }
        return new WasapiOut(AudioClientShareMode.Shared, 100);
    }
    void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializingAudioSettings || OutputDeviceCombo.SelectedIndex < 0) return;
        string? newId = OutputDeviceCombo.SelectedIndex == 0 ? null :
            availableOutputs.ElementAtOrDefault(OutputDeviceCombo.SelectedIndex - 1)?.ID;
        if (newId == selectedOutputId) return;
        selectedOutputId = newId;
        SaveAudioSettings();
        // Preserve playback position and paused state when changing speakers.
        if (reader == null || current == null) return;
        var song = current;
        var position = reader.CurrentTime;
        bool paused = output?.PlaybackState == PlaybackState.Paused;
        StartSong(song);
        if (reader != null) reader.CurrentTime = position;
        if (paused && output != null) { output.Pause(); SetPlayIcon(false); }
    }

    void DevicePickerButton_Click(object sender, RoutedEventArgs e)
    {
        OutputPickerHost.Visibility = OutputPickerHost.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (OutputPickerHost.Visibility == Visibility.Visible)
            OutputDeviceCombo.Focus();
    }

    void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin)
        {
            for (DependencyObject? current = origin; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is Button || current is TextBox || current is ComboBox)
                    return;
            }
        }
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        try { DragMove(); } catch { }
    }

    void WindowMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void WindowMaxRestore_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }


    void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem item)
        {
            sortMode = item.Content?.ToString() ?? "Playlist order";
            if (rows != null && SongList != null) Refresh();
        }
    }
    void RecentlyAdded_Click(object sender, RoutedEventArgs e)
    {
        selectedPlaylist = null; likedOnly = historyOnly = unplayedOnly = duplicatesOnly = false; recentOnly = true;
        PlaylistList.SelectedItem = null; Refresh();
    }
    void History_Click(object sender, RoutedEventArgs e)
    {
        selectedPlaylist = null; likedOnly = recentOnly = unplayedOnly = duplicatesOnly = false; historyOnly = true;
        PlaylistList.SelectedItem = null; Refresh();
    }
    void SongCard_Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SongRow row }) StartSong(row.Song);
    }
    void EditMetadata(Song song)
    {
        var title = PromptDialog.Ask(this, "Edit metadata", "Title:", song.Title);
        if (title == null) return;
        var artist = PromptDialog.Ask(this, "Edit metadata", "Artist:", song.Artist);
        if (artist == null) return;
        var album = PromptDialog.Ask(this, "Edit metadata", "Album:", song.Album);
        if (album == null) return;
        var genre = PromptDialog.Ask(this, "Edit metadata", "Genre:", song.Genre);
        if (genre == null) return;
        if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath) &&
            MessageBox.Show(this, "Write these metadata changes into the audio file too?", "Embedded tags", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try
            {
                using var file = TagLib.File.Create(song.FilePath);
                file.Tag.Title = title.Trim(); file.Tag.Performers = new[] { artist.Trim() };
                file.Tag.Album = album.Trim(); file.Tag.Genres = new[] { genre.Trim() };
                file.Save();
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not write embedded tags: " + ex.Message); }
        }
        song.Title = title.Trim(); song.Artist = artist.Trim();
        song.Album = album.Trim(); song.Genre = genre.Trim();
        Save(); Refresh();
        if (current?.Id == song.Id) { NowTitle.Text = song.Title; NowArtist.Text = song.Artist; }
    }
    void SongList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(SongList);
        draggedSong = FindSongRow(e.OriginalSource as DependencyObject)?.DataContext as SongRow;
    }
    void SongList_MouseMove(object sender, MouseEventArgs e)
    {
        if (selectedPlaylist == null || draggedSong == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(SongList);
        if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = draggedSong;
        draggedSong = null;
        DragDrop.DoDragDrop(SongList, source, DragDropEffects.Move);
    }
    void SongList_Drop(object sender, DragEventArgs e)
    {
        if (selectedPlaylist == null || !e.Data.GetDataPresent(typeof(SongRow))) return;
        var from = (SongRow)e.Data.GetData(typeof(SongRow))!;
        var target = FindSongRow(e.OriginalSource as DependencyObject)?.DataContext as SongRow;
        if (target == null || target.Song.Id == from.Song.Id) return;
        var ids = selectedPlaylist.SongIds;
        if (!ids.Remove(from.Song.Id)) return;
        var index = ids.IndexOf(target.Song.Id);
        if (index < 0) { ids.Add(from.Song.Id); }
        else ids.Insert(index, from.Song.Id);
        sortMode = "Playlist order"; SortBox.SelectedIndex = 0;
        Save(); Refresh();
    }
    void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        MediaKeyDown(sender, e);
        if (e.Handled || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M) { Volume.Value = Volume.Value == 0 ? 0.75 : 0; e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { AddFiles_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Space) { PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Right) { Next_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Left) { Previous_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
    void EqualizerPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EqualizerPreset.SelectedItem is not ComboBoxItem item || EqualizerSlider == null || MidSlider == null || TrebleSlider == null) return;
        (double bass, double mid, double treble) = item.Content?.ToString() switch
        {
            "Rock" => (5, -1, 4),
            "Vocal" => (-2, 4, 2),
            "Electronic" => (6, -2, 5),
            _ => (0, 0, 0)
        };
        EqualizerSlider.Value = bass; MidSlider.Value = mid; TrebleSlider.Value = treble;
    }
    void ToggleEqualizer_Click(object sender, RoutedEventArgs e)
    {
        EqualizerPanel.Visibility = EqualizerPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }
    void EqualizerEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (EqualizerSlider != null && MidSlider != null && TrebleSlider != null)
            equalizer?.SetBands(EqualizerEnabled.IsChecked == true ? EqualizerSlider.Value : 0, EqualizerEnabled.IsChecked == true ? MidSlider.Value : 0, EqualizerEnabled.IsChecked == true ? TrebleSlider.Value : 0);
    }
    void EqualizerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EqualizerSlider != null && MidSlider != null && TrebleSlider != null)
            equalizer?.SetBands(EqualizerEnabled?.IsChecked == true ? EqualizerSlider.Value : 0, EqualizerEnabled?.IsChecked == true ? MidSlider.Value : 0, EqualizerEnabled?.IsChecked == true ? TrebleSlider.Value : 0);
    }


    void RefreshQueueRows()
    {
        queueRows.Clear();
        var position = current == null ? -1 : queue.FindIndex(s => s.Id == current.Id);
        foreach (var song in queue.Skip(Math.Max(0, position + 1)))
            queueRows.Add(new SongRow(song, queueRows.Count + 1));
    }
    void Queue_Click(object sender, RoutedEventArgs e) =>
        QueuePanel.Visibility = QueuePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        queue = current == null ? new List<Song>() : new List<Song> { current };
        RefreshQueueRows();
    }
    void QueueSong_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is SongRow row) StartSong(row.Song, preserveQueue: true);
    }
    void QueueMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not SongRow row) return;
        int i = queue.FindIndex(s => s.Id == row.Song.Id);
        int currentIndex = current == null ? -1 : queue.FindIndex(s => s.Id == current.Id);
        if (i <= currentIndex + 1) return;
        (queue[i - 1], queue[i]) = (queue[i], queue[i - 1]);
        RefreshQueueRows();
    }
    void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not SongRow row || row.Song.Id == current?.Id) return;
        queue.RemoveAll(s => s.Id == row.Song.Id);
        RefreshQueueRows();
    }
    void ListView_Click(object sender, RoutedEventArgs e) { gridView = false; UpdateViewMode(); }
    void GridView_Click(object sender, RoutedEventArgs e) { gridView = true; UpdateViewMode(); }
    void UpdateViewMode()
    {
        SongList.Visibility = gridView ? Visibility.Collapsed : Visibility.Visible;
        SongGridScrollViewer.Visibility = gridView ? Visibility.Visible : Visibility.Collapsed;
    }
    void SeeAll_Click(object sender, RoutedEventArgs e)
    {
        Home_Click(sender, e);
        SongList.BringIntoView();
    }
    void ShowSmartView(bool unplayed, bool duplicates)
    {
        selectedPlaylist = null;
        likedOnly = recentOnly = historyOnly = false;
        unplayedOnly = unplayed;
        duplicatesOnly = duplicates;
        PlaylistList.SelectedItem = null;
        SearchBox.Clear();
        Refresh();
    }
    void Unplayed_Click(object sender, RoutedEventArgs e) => ShowSmartView(true, false);
    void Duplicates_Click(object sender, RoutedEventArgs e) => ShowSmartView(false, true);
    void SurpriseMe_Click(object sender, RoutedEventArgs e)
    {
        var playable = library.Songs.Where(s => File.Exists(s.FilePath)).ToList();
        if (playable.Count == 0) { MessageBox.Show(this, "Import some local songs first."); return; }
        Home_Click(sender, e);
        StartSong(playable[random.Next(playable.Count)]);
    }
    void ExportPlaylist(Playlist playlist)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export playlist",
            Filter = "M3U playlist|*.m3u",
            FileName = string.Concat(playlist.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".m3u"
        };
        if (dialog.ShowDialog(this) != true) return;
        var songs = playlist.SongIds.Select(id => library.Songs.FirstOrDefault(s => s.Id == id))
            .Where(s => s != null && File.Exists(s.FilePath)).ToList();
        var lines = new List<string> { "#EXTM3U" };
        foreach (var song in songs)
        {
            lines.Add($"#EXTINF:{song!.DurationSeconds},{song.Artist} - {song.Title}".Replace("\r", " ").Replace("\n", " "));
            lines.Add(song.FilePath);
        }
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false));
        MessageBox.Show(this, $"Exported {songs.Count} playable tracks.", "Playlist export");
    }
    void PlaylistList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Use the row under the cursor, not a stale selected playlist.
        DependencyObject? hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit is not ListBoxItem { DataContext: PlaylistRow row } item) return;
        var playlist = row.Playlist;
        var menu = new ContextMenu();
        void Add(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Rename", () =>
        {
            var name = PromptDialog.Ask(this, "Rename playlist", "Name:", playlist.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            playlist.Name = name.Trim(); Save(); Refresh();
        });
        Add("Duplicate", () =>
        {
            library.Playlists.Add(new Playlist { Name = playlist.Name + " (copy)", SongIds = new List<Guid>(playlist.SongIds), CoverPath = playlist.CoverPath });
            Save(); Refresh();
        });
        Add("Export M3U", () => ExportPlaylist(playlist));
        Add("Change cover", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog() != true) return;
            playlist.CoverPath = dialog.FileName; Save(); Refresh();
        });
        Add("Delete", () =>
        {
            if (MessageBox.Show(this, "Delete playlist '" + playlist.Name + "'?", "Delete playlist", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            library.Playlists.Remove(playlist);
            if (ReferenceEquals(selectedPlaylist, playlist)) selectedPlaylist = null;
            Save(); Refresh();
        });
        menu.PlacementTarget = item;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }
    void ApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        artistFilter = ArtistFilter.Text.Trim();
        albumFilter = AlbumFilter.Text.Trim();
        genreFilter = GenreFilter.Text.Trim();
        likedFilter = LikedFilter.IsChecked == true;
        localFilter = LocalFilter.IsChecked == true;
        yearFilter = uint.TryParse(YearFilter.Text, out var year) ? year : null;
        playerSettings.ArtistFilter = artistFilter;
        playerSettings.AlbumFilter = albumFilter;
        playerSettings.GenreFilter = genreFilter;
        playerSettings.YearFilter = YearFilter.Text.Trim();
        playerSettings.LikedFilter = likedFilter;
        playerSettings.LocalFilter = localFilter;
        playerSettings.Save();
        Refresh();
    }
    void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        ArtistFilter.Clear(); AlbumFilter.Clear(); GenreFilter.Clear(); YearFilter.Clear();
        LikedFilter.IsChecked = LocalFilter.IsChecked = false;
        ApplyFilters_Click(sender, e);
    }
    void Stats_Click(object sender, RoutedEventArgs e)
    {
        var played = library.Songs.Where(s => s.PlayCount > 0).OrderByDescending(s => s.PlayCount).Take(15);
        MessageBox.Show(this, string.Join("\n", played.Select(s => $"{s.Title} — {s.PlayCount} plays; last {s.LastPlayedUtc?.ToLocalTime():g}")),
            "Most played");
    }
    void MediaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.MediaPlayPause) { PlayPause_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.MediaNextTrack) { Next_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.MediaPreviousTrack) { Previous_Click(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    void RefreshOutputs_Click(object sender, RoutedEventArgs e) => RefreshAudioDevices();
    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (reader != null) reader.Volume = (float)Volume.Value;
        if (!initializingAudioSettings) SaveAudioSettings();
    }
}
public class SongRow
{
    public Song Song { get; }
    public SongRow(Song song, int index) { Song = song; Index = index; }
    public int Index { get; }
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Album => Song.Album;
    public string Source => Song.Source;
    public string Duration => Song.Duration;
    public string DateAdded => Song.DateAddedUtc?.ToLocalTime().ToString("MMM d, yyyy") ?? "—";
    public string Heart => Song.Liked ? "♥" : "";
    public BitmapImage? Artwork => ArtworkResolver.Load(Song);
}

public sealed class MusicShelf
{
    public string Title { get; }
    public IReadOnlyList<SongRow> Tracks { get; }
    public MusicShelf(string title, IReadOnlyList<SongRow> tracks) { Title = title; Tracks = tracks; }
}

public sealed class PlaylistRow
{
    public Playlist Playlist { get; }
    public System.Windows.Media.Imaging.BitmapImage? Cover { get; }
    public PlaylistRow(Playlist playlist, System.Windows.Media.Imaging.BitmapImage? cover) { Playlist = playlist; Cover = cover; }
    public override string ToString() => Playlist.Name;
}
