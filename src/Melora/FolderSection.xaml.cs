using System.Windows;
using System.Windows.Controls;
using Melora.Library;
using Microsoft.Win32;

namespace Melora;

/// <summary>The folder list for one library in Settings.</summary>
public partial class FolderSection : UserControl
{
    LibraryService? _library;
    LibraryService? _other;

    public FolderSection()
    {
        InitializeComponent();
    }

    /// <param name="other">The other file library; a folder can't be in both.</param>
    public void Bind(LibraryService library, LibraryService other, string heading, string hint)
    {
        _library = library;
        _other = other;
        Heading.Text = heading;
        Hint.Text = hint;
        Refresh();
    }

    void Refresh() => FolderList.ItemsSource = _library?.Folders;

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_library is null || _other is null) return;
        if (PickFolders(Window.GetWindow(this)!, $"Add {_library.Kind.ToString().ToLowerInvariant()} folder") is not { } folders) return;

        foreach (var folder in folders)
        {
            if (TryAddFolder(_library, _other, folder) is { } problem)
                MessageDialog.Inform(Window.GetWindow(this), "Folder not added", problem);
        }
        Refresh();
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_library is null || FolderList.SelectedItem is not string folder) return;
        _library.RemoveFolder(folder);
        Refresh();
    }

    public static string[]? PickFolders(Window owner, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = true };
        return dialog.ShowDialog(owner) == true ? dialog.FolderNames : null;
    }

    /// <summary>Adds a folder unless the other library already has it. Returns why it wasn't added, or null.</summary>
    public static string? TryAddFolder(LibraryService library, LibraryService other, string folder)
    {
        if (other.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return $"“{folder}” is already in your {other.Kind} library. A folder can only be in one.";

        library.AddFolder(folder);
        return null;
    }
}
