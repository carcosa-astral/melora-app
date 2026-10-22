using System.Windows;
using System.Windows.Input;
using Melora.Interop;
using Melora.Radio;

namespace Melora;

/// <summary>
/// Adding or editing a radio station. The address is checked by connecting to it, which also
/// suggests a name from the station's own icy-name; the user names it before it's saved.
/// </summary>
public partial class StationWindow : Window
{
    int _checkId;

    public string StationName => NameBox.Text.Trim();
    public string StationUrl => StreamMetadata.NormalizeUrl(UrlBox.Text) ?? UrlBox.Text.Trim();

    public StationWindow(Station? existing = null)
    {
        InitializeComponent();
        if (existing is not null)
        {
            Title = "Edit station";
            Heading.Text = "Edit station";
            UrlBox.Text = existing.Url;
            NameBox.Text = existing.Name;
        }

        DataObject.AddPastingHandler(UrlBox, (_, _) => Dispatcher.BeginInvoke(() => _ = CheckAsync()));
        Loaded += (_, _) =>
        {
            var box = existing is null ? UrlBox : NameBox;
            box.Focus();
            box.SelectAll();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SaveButton.IsEnabled = StationName.Length > 0 && StreamMetadata.NormalizeUrl(UrlBox.Text) is not null;

    void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = CheckAsync();
    }

    void OnCheck(object sender, RoutedEventArgs e) => _ = CheckAsync();

    async Task CheckAsync()
    {
        if (StreamMetadata.NormalizeUrl(UrlBox.Text) is not { } url)
        {
            ShowStatus("That doesn't look like a web address.", "ErrorBrush");
            return;
        }

        var id = ++_checkId;
        UrlBox.Text = url;
        CheckButton.IsEnabled = false;
        ShowStatus("Connecting…", "TextDimBrush");

        var result = await StationProbe.ProbeAsync(url);
        if (id != _checkId) return; // the address changed while we were connecting
        CheckButton.IsEnabled = true;

        if (!result.Ok)
        {
            ShowStatus(result.Error!, "ErrorBrush");
            return;
        }

        var details = new[] { result.Format, result.Genre, result.Name is null ? null : $"calls itself “{result.Name}”" };
        ShowStatus("✓ Works  ·  " + string.Join("  ·  ", details.OfType<string>()), "OkBrush");

        // Suggest the station's own name, selected so typing replaces it with a better one.
        if (NameBox.Text.Trim().Length == 0 && result.Name is { } name) NameBox.Text = name;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void ShowStatus(string text, string brushKey)
    {
        CheckStatus.Text = text;
        CheckStatus.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
    }

    void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
