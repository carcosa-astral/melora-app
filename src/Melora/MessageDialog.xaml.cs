using System.Windows;
using System.Windows.Media;
using Melora.Interop;

namespace Melora;

/// <summary>Melora's own message box, in place of the system one.</summary>
public partial class MessageDialog : Window
{
    MessageDialog(Window? owner, string heading, string body, string confirm, string? cancel, bool destructive)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Heading.Text = heading;
        Body.Text = body;
        Body.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirm;

        if (cancel is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancel;

        if (destructive)
        {
            // Destructive actions default to the safe button and show their own in red.
            ConfirmButton.Foreground = (Brush)FindResource("ErrorBrush");
            ConfirmButton.IsDefault = false;
            CancelButton.IsDefault = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Asks a yes/no question. Returns true if the user confirmed.</summary>
    public static bool Confirm(Window? owner, string heading, string body, string confirm, bool destructive = false) =>
        new MessageDialog(owner, heading, body, confirm, "Cancel", destructive).ShowDialog() == true;

    /// <summary>Tells the user something, with a single OK button.</summary>
    public static void Inform(Window? owner, string heading, string body = "") =>
        new MessageDialog(owner, heading, body, "OK", null, destructive: false).ShowDialog();
}
