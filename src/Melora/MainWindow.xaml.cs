using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DragCompletedEventArgs = System.Windows.Controls.Primitives.DragCompletedEventArgs;
using DragDeltaEventArgs = System.Windows.Controls.Primitives.DragDeltaEventArgs;
using System.Windows.Input;
using System.Windows.Interop;
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
                _accentAlbum = null;
                UpdateAccent();
                break;
            case nameof(MainViewModel.IsPlaying):
                UpdateOverlayStatus();
                UpdateTimeline();
                break;
        }
    }

    // ---- Accent colour from album art ----

    AlbumGroup? _accentAlbum;

    async void UpdateAccent()
    {
        var album = _vm.AccentFromArt ? _vm.CurrentTrack?.Group : null;
        if (album == _accentAlbum) return;
        _accentAlbum = album;

        var color = album is null ? null : await album.GetAccentAsync();
        if (album != _accentAlbum) return; // the track changed while the art was being read
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
            if (key != _overlayKey) _overlay.Show(track.Title, track.Artist, track.Group.Title, track.Group.ReadArtBytes);
        }
        else if (_vm.CurrentStation is { } station)
        {
            key = $"station|{station.Id}|{station.Name}|{_vm.StreamTitle}";
            var (artist, title) = StreamMetadata.SplitArtistTitle(_vm.StreamTitle);
            if (key != _overlayKey) _overlay.Show(title ?? station.Name, artist ?? station.Name, station.Name, null);
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
}
