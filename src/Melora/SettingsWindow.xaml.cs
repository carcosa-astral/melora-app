using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Melora.Interop;
using Melora.Library;

namespace Melora;

public partial class SettingsWindow : Window
{
    public SettingsWindow(LibraryService music, LibraryService mixes)
    {
        InitializeComponent();
        MusicSection.Bind(music, mixes, "Music folders",
            "Melora watches these folders and picks up new or changed files on its own.");
        MixesSection.Bind(mixes, music, "Mixes folders",
            "Kept apart from your albums. A mixes folder inside a music folder only shows up here.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
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

/// <summary>Binds a RadioButton to an enum value: <c>IsChecked="{Binding Tab, Converter=..., ConverterParameter=Music}"</c>.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(typeof(LibraryTab), name) : Binding.DoNothing;
}
