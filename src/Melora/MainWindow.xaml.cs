using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DragCompletedEventArgs = System.Windows.Controls.Primitives.DragCompletedEventArgs;
using DragDeltaEventArgs = System.Windows.Controls.Primitives.DragDeltaEventArgs;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Melora.Interop;
using Melora.Library;
using Melora.Radio;
using Melora.Theme;
using Windows.Media;

namespace Melora;

public partial class MainWindow : Window
{
    // The overlay doesn't extrapolate position by itself, so nudge it now and then.
    static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);

    readonly MainViewModel _vm = new();
    readonly DispatcherTimer _timelineTimer;
    TrayIcon? _tray;
    MediaOverlay? _overlay;
    string? _overlayKey;
    WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        RestoreLayout();
        Loaded += OnLoaded;
        _vm.PropertyChanged += OnViewModelChanged;
        _timelineTimer = new DispatcherTimer(TimelineInterval, DispatcherPriority.Background, (_, _) => UpdateTimeline(), Dispatcher);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);

        var trayMenu = (ContextMenu)FindResource("TrayMenu");
        trayMenu.DataContext = _vm;
        _tray = new TrayIcon(trayMenu);
        _tray.Clicked += ToggleFromTray;

        try
        {
            _overlay = new MediaOverlay(new WindowInteropHelper(this).Handle, Dispatcher);
            _overlay.ButtonPressed += OnOverlayButton;
            _overlay.SeekRequested += position => { _vm.SeekTo(position.TotalSeconds); UpdateTimeline(); };
            _timelineTimer.Start();
        }
        catch
        {
            // Older Windows without the media overlay: everything else still works.
            _overlay = null;
        }
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't start: {ex}");
            MessageDialog.Inform(this, "Melora couldn't start", ex.Message);
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveLayout();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timelineTimer.Stop();
        _overlay?.Clear();
        _tray?.Dispose();
        _vm.Dispose();
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _vm.MinimizeToTray) HideToTray();
        else if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        base.OnStateChanged(e);
    }

    // ---- Tray ----

    void HideToTray()
    {
        Hide();
        if (_vm.TrayHintShown) return;
        _vm.TrayHintShown = true;
        _tray?.ShowBalloon("Melora is in the system tray", "Click the tray icon to bring it back.");
    }

    /// <summary>Brings the window back, e.g. from the tray or when Melora is launched again.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
    }

    void ToggleFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) HideToTray();
        else ShowFromTray();
    }

    void Quit() => Close();

    void OnTrayShow(object sender, RoutedEventArgs e) => ShowFromTray();
    void OnTrayQuit(object sender, RoutedEventArgs e) => Quit();

    // ---- Now playing: tray tooltip and media overlay ----

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.NowPlaying):
                if (_tray is not null)
                    _tray.Tooltip = _vm.CurrentTrack is null && _vm.CurrentStation is null ? "Melora" : _vm.NowPlaying;
                UpdateOverlay();
                UpdateAccent();
                break;
            case nameof(MainViewModel.AccentFromArt):
                _accentSource = null;
                UpdateAccent();
                break;
            case nameof(MainViewModel.RadioArt):
                UpdateOverlay();
                UpdateAccent();
                break;
            case nameof(MainViewModel.IsPlaying):
                UpdateOverlayStatus();
                UpdateTimeline();
                break;
        }
    }

    // ---- Accent colour from album art ----

    /// <summary>What the accent was last picked from: the playing album, or the radio song's cover.</summary>
    object? _accentSource;

    async void UpdateAccent()
    {
        var source = !_vm.AccentFromArt ? null : (object?)_vm.CurrentTrack?.Group ?? _vm.RadioArt;
        if (source == _accentSource) return;
        _accentSource = source;

        var color = source switch
        {
            AlbumGroup album => await album.GetAccentAsync(),
            byte[] cover => await Task.Run(() => Accent.FromImage(cover)),
            _ => null,
        };
        if (source != _accentSource) return; // the track changed while the art was being read
        Accent.Apply(color ?? Accent.Default);
    }

    // ---- Window placement and column widths ----

    void RestoreLayout()
    {
        ((ColumnLayout)FindResource("MusicColumns")).Load(_vm.GetViewSetting("columns.music"));
        ((ColumnLayout)FindResource("MixColumns")).Load(_vm.GetViewSetting("columns.mixes"));

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (WindowPlacement.Load(_vm.GetViewSetting("window")) is not { } placement || !placement.IsVisibleOn(screen)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Bounds.Left;
        Top = placement.Bounds.Top;
        Width = Math.Max(placement.Bounds.Width, MinWidth);
        Height = Math.Max(placement.Bounds.Height, MinHeight);
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    void SaveLayout()
    {
        // RestoreBounds is the normal-size rectangle even while maximized or minimized.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            var maximized = WindowState == WindowState.Maximized
                            || (WindowState == WindowState.Minimized && _restoreState == WindowState.Maximized);
            _vm.SetViewSetting("window", new WindowPlacement(bounds, maximized).Save());
        }
        SaveColumns();
    }

    void SaveColumns()
    {
        _vm.SetViewSetting("columns.music", ((ColumnLayout)FindResource("MusicColumns")).Save());
        _vm.SetViewSetting("columns.mixes", ((ColumnLayout)FindResource("MixColumns")).Save());
    }

    /// <summary>A header divider was dragged. Its Tag says which layout and which divider ("MusicColumns|TitleArtist").</summary>
    void OnColumnDrag(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || tag.Split('|') is not [var key, var divider]) return;
        var layout = (ColumnLayout)FindResource(key);
        if (divider == "TitleArtist") layout.DragTitleArtist(e.HorizontalChange);
        else layout.DragArtistLength(e.HorizontalChange);
    }

    void OnColumnDragDone(object sender, DragCompletedEventArgs e) => SaveColumns();

    void UpdateOverlay()
    {
        if (_overlay is null) return;

        string? key;
        if (_vm.CurrentTrack is { } track)
        {
            key = "track|" + track.Path;
            if (key != _overlayKey) _overlay.Show(track.Title, track.Artist, track.Group.Title, track.Group.GetArtBytesAsync);
        }
        else if (_vm.CurrentStation is { } station)
        {
            var cover = _vm.RadioArt;
            key = $"station|{station.Id}|{station.Name}|{_vm.StreamTitle}|{cover?.Length}";
            var (artist, title) = StreamMetadata.SplitArtistTitle(_vm.StreamTitle);
            if (key != _overlayKey)
                _overlay.Show(title ?? station.Name, artist ?? station.Name, station.Name, cover is null ? null : () => Task.FromResult<byte[]?>(cover));
        }
        else
        {
            key = null;
            if (_overlayKey is not null) _overlay.Clear();
        }

        _overlayKey = key;
        UpdateOverlayStatus();
        UpdateTimeline();
    }

    void UpdateOverlayStatus()
    {
        if (_overlay is null || _overlayKey is null) return;
        _overlay.Status = _vm.IsPlaying ? MediaPlaybackStatus.Playing
            : _vm.IsPaused ? MediaPlaybackStatus.Paused
            : MediaPlaybackStatus.Stopped;
    }

    void UpdateTimeline()
    {
        if (_overlay is null || _overlayKey is null) return;
        var length = _vm.CanSeek ? TimeSpan.FromSeconds(_vm.Length) : TimeSpan.Zero;
        var position = _vm.CanSeek ? TimeSpan.FromSeconds(_vm.Position) : TimeSpan.Zero;
        _overlay.SetTimeline(position, length);
    }

    /// <summary>Overlay buttons and keyboard media keys.</summary>
    void OnOverlayButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play when !_vm.IsPlaying:
            case SystemMediaTransportControlsButton.Pause when _vm.IsPlaying:
                _vm.PlayPause(null);
                break;
            case SystemMediaTransportControlsButton.Stop:
                _vm.Stop();
                break;
            case SystemMediaTransportControlsButton.Next:
                _vm.Next();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _vm.Previous();
                break;
        }
    }

    /// <summary>The selected row in the visible tab: a Track or a Station.</summary>
    object? Selected => _vm.Tab switch
    {
        LibraryTab.Music => MusicList.SelectedItem,
        LibraryTab.Mixes => MixesList.SelectedItem,
        _ => StationList.SelectedItem,
    };

    // ---- Playback ----

    void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: Track track }) _vm.PlayFrom(track);
    }

    void OnStationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: Station station }) _ = _vm.PlayStation(station);
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => _vm.PlayPause(Selected);
    void OnPrevious(object sender, RoutedEventArgs e) => _vm.Previous();
    void OnNext(object sender, RoutedEventArgs e) => _vm.Next();
    void OnRepeat(object sender, RoutedEventArgs e) => _vm.CycleRepeat();
    void OnLove(object sender, RoutedEventArgs e) => _vm.ToggleLove();

    void OnSeekStart(object sender, MouseButtonEventArgs e) => _vm.IsSeeking = true;

    /// <summary>The seek bar moved with the keyboard (arrows 5 s, Page Up/Down 30 s, Home/End).</summary>
    void OnSeekKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            _vm.SeekTo(SeekBar.Value);
    }

    void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        _vm.SeekTo(SeekBar.Value);
        _vm.IsSeeking = false;
    }

    // ---- Libraries ----

    async void OnSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_vm) { Owner = this }.ShowDialog();
        _vm.SettingsChanged();
        await _vm.RescanAsync();
    }

    async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var (library, other) = (sender as FrameworkElement)?.Tag as string == "Mixes"
            ? (_vm.Mixes.Service, _vm.Music.Service)
            : (_vm.Music.Service, _vm.Mixes.Service);

        var title = $"Add {library.Kind.ToString().ToLowerInvariant()} folder";
        if (FolderSection.PickFolders(this, title) is not { Length: > 0 } folders) return;

        foreach (var folder in folders)
        {
            if (FolderSection.TryAddFolder(library, other, folder) is { } problem)
                MessageDialog.Inform(this, "Folder not added", problem);
        }
        await _vm.RescanAsync();
    }

    // ---- Radio ----

    void OnAddStation(object sender, RoutedEventArgs e)
    {
        var dialog = new StationWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var station = _vm.AddStation(dialog.StationName, dialog.StationUrl);
        StationList.SelectedItem = station;
        StationList.ScrollIntoView(station);
    }

    void OnStationsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnExportStations(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export stations",
            FileName = "Melora stations.m3u",
            Filter = "Station list (*.m3u)|*.m3u",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, StationFile.Write(_vm.Stations.Select(s => (s.Name, s.Url))));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageDialog.Inform(this, "Stations not exported", ex.Message);
        }
    }

    void OnImportStations(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import stations",
            Filter = "Station list (*.m3u, *.m3u8)|*.m3u;*.m3u8|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        List<(string Name, string Url)> stations;
        try
        {
            stations = StationFile.Read(System.IO.File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageDialog.Inform(this, "Stations not imported", ex.Message);
            return;
        }

        var added = _vm.ImportStations(stations);
        var skipped = stations.Count - added;
        MessageDialog.Inform(this, added == 0 ? "No new stations" : added == 1 ? "1 station added" : $"{added} stations added",
            stations.Count == 0 ? "That file has no station addresses in it."
            : skipped == 0 ? "They're in your Radio list now."
            : $"Skipped {skipped} already in your list.");
    }

    void EditStation(Station station)
    {
        var dialog = new StationWindow(station) { Owner = this };
        if (dialog.ShowDialog() == true) _vm.UpdateStation(station, dialog.StationName, dialog.StationUrl);
    }

    void DeleteStation(Station station)
    {
        if (MessageDialog.Confirm(this, $"Delete “{station.Name}”?",
                "It will be removed from your stations. You can add it again later.", "Delete", destructive: true))
            _vm.DeleteStation(station);
    }

    static Station? MenuStation(object sender) => (sender as FrameworkElement)?.DataContext as Station;

    void OnStationMenuPlay(object sender, RoutedEventArgs e)
    {
        if (MenuStation(sender) is { } s) _ = _vm.PlayStation(s);
    }

    void OnStationMenuEdit(object sender, RoutedEventArgs e)
    {
        if (MenuStation(sender) is { } s) EditStation(s);
    }

    void OnStationMenuDelete(object sender, RoutedEventArgs e)
    {
        if (MenuStation(sender) is { } s) DeleteStation(s);
    }

    void OnStationListKeyDown(object sender, KeyEventArgs e)
    {
        if (StationList.SelectedItem is not Station station) return;
        switch (e.Key)
        {
            case Key.F2: EditStation(station); break;
            case Key.Delete: DeleteStation(station); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- Keyboard ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Q when ctrl:
                Quit();
                break;
            case Key.L when ctrl:
                _vm.ToggleLove();
                break;
            case Key.J when ctrl:
                JumpToPlaying();
                break;
            case Key.Enter when Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase:
                return; // a button reached with Tab: Enter presses it
            case Key.Space when IsTyping:
                TypeToJump(" "); // part of a name ("barry c"), not play/pause
                break;
            case Key.Space:
                _vm.PlayPause(Selected);
                break;
            case Key.Enter when Selected is Track track:
                _vm.PlayFrom(track);
                break;
            case Key.Enter when Selected is Station station:
                _ = _vm.PlayStation(station);
                break;
            case Key.Right when ctrl:
                _vm.Next();
                break;
            case Key.Left when ctrl:
                _vm.Previous();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- Jumping around the lists ----

    static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(1);
    string _typed = "";
    DateTime _typedAt;

    /// <summary>Letters typed less than a second apart make up one name.</summary>
    bool IsTyping => _typed.Length > 0 && DateTime.UtcNow - _typedAt < TypingPause;

    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || char.IsControl(e.Text[0]) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        e.Handled = true;
        TypeToJump(e.Text);
    }

    /// <summary>Jumps to the first artist (Music), DJ (Mixes) or station whose name starts with what's been typed.</summary>
    async void TypeToJump(string text)
    {
        _typed = (IsTyping ? _typed : "") + text;
        _typedAt = DateTime.UtcNow;

        var (list, target) = _vm.Tab switch
        {
            LibraryTab.Music => (MusicList, ListJump.Find(_vm.Music.Tracks, t => t.Group.Artist, _typed)),
            LibraryTab.Mixes => (MixesList, ListJump.Find(_vm.Mixes.Tracks, t => t.Collection, _typed)),
            _ => (StationList, (object?)ListJump.Find(_vm.Stations, s => s.Name, _typed)),
        };
        if (target is not null) await ShowInList(list, target);
    }

    void OnNowPlayingClick(object sender, MouseButtonEventArgs e) => JumpToPlaying();

    /// <summary>Shows the playing track or station in its list, switching tabs if needed.</summary>
    async void JumpToPlaying()
    {
        if (_vm.CurrentTrack is { } track)
        {
            var mixes = track.Library == LibraryKind.Mixes;
            // After a rescan the list holds new Track objects; the playing one is marked.
            var row = (mixes ? _vm.Mixes : _vm.Music).Tracks.FirstOrDefault(t => t.IsPlaying);
            if (row is null) return;
            _vm.Tab = mixes ? LibraryTab.Mixes : LibraryTab.Music;
            await ShowInList(mixes ? MixesList : MusicList, row);
        }
        else if (_vm.CurrentStation is { } station)
        {
            _vm.Tab = LibraryTab.Radio;
            await ShowInList(StationList, station);
        }
    }

    /// <summary>
    /// Selects a row and scrolls to it: its album or DJ heading at the top of the list if the row
    /// still fits on screen that way, otherwise the row a third of the way down.
    /// </summary>
    async Task ShowInList(ListView list, object item)
    {
        // A tab that was just switched to has to be laid out before it can scroll.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        list.SelectedItem = item;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem row || FindChild<ScrollViewer>(list) is not { } viewer) return;
        row.Focus();
        list.UpdateLayout();

        var rowTop = row.TransformToAncestor(viewer).Transform(default).Y;
        var headingTop = OutermostGroup(row, list)?.TransformToAncestor(viewer).Transform(default).Y ?? rowTop;
        var fits = rowTop - headingTop + row.ActualHeight <= viewer.ViewportHeight;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (fits ? headingTop : rowTop - viewer.ViewportHeight / 3));
    }

    /// <summary>The album or DJ heading a row sits under (not a disc heading inside an album).</summary>
    static GroupItem? OutermostGroup(DependencyObject row, ListView list)
    {
        GroupItem? group = null;
        for (var node = VisualTreeHelper.GetParent(row); node is not null && node != list; node = VisualTreeHelper.GetParent(node))
            if (node is GroupItem g) group = g;
        return group;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found) return found;
        }
        return null;
    }
}
