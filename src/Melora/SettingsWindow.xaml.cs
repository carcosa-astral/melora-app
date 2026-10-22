using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Navigation;
using Melora.Interop;
using Melora.Scrobbling;

namespace Melora;

public partial class SettingsWindow : Window
{
    // Approving Melora on Last.fm happens in the browser; check back this often, for this long.
    static readonly TimeSpan ApprovalPoll = TimeSpan.FromSeconds(3);
    static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    readonly MainViewModel _vm;
    int _lastFmAttempt;

    LastFmClient LastFm => _vm.Scrobbler.LastFm!;
    ListenBrainzClient ListenBrainz => _vm.Scrobbler.ListenBrainz!;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        var (music, mixes) = (vm.Music.Service, vm.Mixes.Service);
        MusicSection.Bind(music, mixes, "Music folders",
            "Melora watches these folders and picks up new or changed files on its own.");
        MixesSection.Bind(mixes, music, "Mixes folders",
            "One folder per DJ inside a mixes folder; the DJ's picture shows on their heading. A mixes folder inside a music folder only shows up here.");

        LastFmKey.Text = LastFm.ApiKey ?? "";
        LastFmSecret.Password = LastFm.Secret ?? "";
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lastFmAttempt++; // stop waiting for a Last.fm approval
        _ = _vm.Scrobbler.FlushAsync(); // a new connection may have plays waiting
        base.OnClosed(e);
    }

    void Refresh()
    {
        LastFmStatus.Text = LastFm.IsConnected
            ? (LastFm.NeedsReconnect ? $"Last.fm stopped accepting the login for {LastFm.UserName}. Disconnect and connect again." : $"Connected as {LastFm.UserName}")
            : "Not connected";
        LastFmDisconnect.Visibility = LastFm.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        LastFmSetup.Visibility = LastFm.IsConnected ? Visibility.Collapsed : Visibility.Visible;

        ListenBrainzStatus.Text = ListenBrainz.IsConnected
            ? (ListenBrainz.NeedsReconnect ? $"ListenBrainz stopped accepting the token for {ListenBrainz.UserName}. Disconnect and connect again." : $"Connected as {ListenBrainz.UserName}")
            : "Not connected";
        ListenBrainzDisconnect.Visibility = ListenBrainz.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        ListenBrainzSetup.Visibility = ListenBrainz.IsConnected ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Last.fm ----

    async void OnLastFmConnect(object sender, RoutedEventArgs e)
    {
        var key = LastFmKey.Text.Trim();
        var secret = LastFmSecret.Password.Trim();
        if (key.Length == 0 || secret.Length == 0)
        {
            LastFmMessage.Text = "Paste both the API key and the shared secret first.";
            return;
        }

        var attempt = ++_lastFmAttempt;
        LastFmConnect.IsEnabled = false;
        try
        {
            LastFmMessage.Text = "Contacting Last.fm…";
            var (token, url) = await LastFm.BeginConnectAsync(key, secret);
            OpenInBrowser(url);
            LastFmMessage.Text = "Approve Melora in your browser. This will update by itself.";

            var waited = TimeSpan.Zero;
            while (attempt == _lastFmAttempt && waited < ApprovalTimeout)
            {
                await Task.Delay(ApprovalPoll);
                waited += ApprovalPoll;
                if (attempt != _lastFmAttempt) return;
                if (await LastFm.TryFinishConnectAsync(token))
                {
                    LastFmMessage.Text = "";
                    Refresh();
                    return;
                }
            }
            if (attempt == _lastFmAttempt) LastFmMessage.Text = "Didn't hear back from Last.fm. Try Connect again.";
        }
        catch (LastFmException ex) when (ex.Code is 10 or 26)
        {
            LastFmMessage.Text = "Last.fm didn't accept that API key. Check it and the secret.";
        }
        catch (Exception ex)
        {
            LastFmMessage.Text = $"Couldn't reach Last.fm: {ex.Message}";
        }
        finally
        {
            if (attempt == _lastFmAttempt) LastFmConnect.IsEnabled = true;
        }
    }

    void OnLastFmDisconnect(object sender, RoutedEventArgs e)
    {
        LastFm.Disconnect();
        Refresh();
    }

    // ---- ListenBrainz ----

    async void OnListenBrainzConnect(object sender, RoutedEventArgs e)
    {
        var token = ListenBrainzToken.Password.Trim();
        if (token.Length == 0)
        {
            ListenBrainzMessage.Text = "Paste your user token first.";
            return;
        }

        ListenBrainzConnect.IsEnabled = false;
        ListenBrainzMessage.Text = "Checking…";
        try
        {
            if (await ListenBrainz.ConnectAsync(token) is null)
            {
                ListenBrainzMessage.Text = "ListenBrainz didn't accept that token.";
                return;
            }
            ListenBrainzMessage.Text = "";
            ListenBrainzToken.Password = "";
            Refresh();
        }
        catch (Exception ex)
        {
            ListenBrainzMessage.Text = $"Couldn't reach ListenBrainz: {ex.Message}";
        }
        finally
        {
            ListenBrainzConnect.IsEnabled = true;
        }
    }

    void OnListenBrainzDisconnect(object sender, RoutedEventArgs e)
    {
        ListenBrainz.Disconnect();
        Refresh();
    }

    // ---- Misc ----

    void OnLink(object sender, RequestNavigateEventArgs e)
    {
        OpenInBrowser(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    static void OpenInBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
