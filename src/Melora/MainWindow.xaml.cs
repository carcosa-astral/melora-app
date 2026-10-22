using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Melora.Interop;
using Melora.Library;
using Melora.Radio;

namespace Melora;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Melora", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
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

    void OnSeekStart(object sender, MouseButtonEventArgs e) => _vm.IsSeeking = true;

    void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        _vm.SeekTo(SeekBar.Value);
        _vm.IsSeeking = false;
    }

    // ---- Libraries ----

    async void OnSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_vm.Music.Service, _vm.Mixes.Service) { Owner = this }.ShowDialog();
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
                MessageBox.Show(this, problem, "Melora", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var answer = MessageBox.Show(this, $"Delete “{station.Name}” from your stations?", "Melora",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _vm.DeleteStation(station);
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
