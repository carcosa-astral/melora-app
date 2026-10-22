using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Melora.Interop;
using Melora.Library;
using Microsoft.Win32;

namespace Melora;

public partial class SettingsWindow : Window
{
    readonly LibraryService _music;

    public SettingsWindow(LibraryService music)
    {
        InitializeComponent();
        _music = music;
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    void Refresh() => FolderList.ItemsSource = _music.Folders;

    public static string[]? PickFolders(Window owner)
    {
        var dialog = new OpenFolderDialog { Title = "Add music folder", Multiselect = true };
        return dialog.ShowDialog(owner) == true ? dialog.FolderNames : null;
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (PickFolders(this) is not { } folders) return;
        foreach (var folder in folders) _music.AddFolder(folder);
        Refresh();
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not string folder) return;
        _music.RemoveFolder(folder);
        Refresh();
    }

    void OnDone(object sender, RoutedEventArgs e) => Close();
}

public sealed class NotNullConverter : IValueConverter
{
    public static NotNullConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
