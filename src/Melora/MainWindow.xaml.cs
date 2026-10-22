using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Melora.Interop;
using Melora.Library;

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

    Track? SelectedTrack => TrackList.SelectedItem as Track;

    void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: Track track }) _vm.PlayFrom(track);
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => _vm.PlayPause(SelectedTrack);
    void OnPrevious(object sender, RoutedEventArgs e) => _vm.Previous();
    void OnNext(object sender, RoutedEventArgs e) => _vm.Next();
    void OnRepeat(object sender, RoutedEventArgs e) => _vm.CycleRepeat();

    void OnSeekStart(object sender, MouseButtonEventArgs e) => _vm.IsSeeking = true;

    void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        _vm.SeekTo(SeekBar.Value);
        _vm.IsSeeking = false;
    }

    async void OnSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_vm.Music) { Owner = this }.ShowDialog();
        await _vm.RescanAsync();
    }

    async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.PickFolders(this) is not { Length: > 0 } folders) return;
        foreach (var folder in folders) _vm.Music.AddFolder(folder);
        await _vm.RescanAsync();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space:
                _vm.PlayPause(SelectedTrack);
                break;
            case Key.Enter when SelectedTrack is { } track:
                _vm.PlayFrom(track);
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
