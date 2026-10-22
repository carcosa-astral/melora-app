using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Melora.Interop;

/// <summary>
/// The notification-area icon. WinForms' NotifyIcon draws the icon; the menu is a WPF
/// ContextMenu so it matches the rest of Melora.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const int MaxTooltip = 127; // NotifyIcon.Text limit

    readonly Forms.NotifyIcon _icon;
    readonly ContextMenu _menu;

    /// <summary>The icon was left-clicked.</summary>
    public event Action? Clicked;

    public TrayIcon(ContextMenu menu)
    {
        _menu = menu;
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Melora.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Melora",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) OpenMenu();
        };
    }

    public string Tooltip
    {
        set => _icon.Text = value.Length <= MaxTooltip ? value : value[..(MaxTooltip - 1)] + "…";
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    void OpenMenu()
    {
        _menu.IsOpen = true;
        // A tray menu must own the foreground, or it won't close when you click elsewhere.
        if (PresentationSource.FromVisual(_menu) is HwndSource source) SetForegroundWindow(source.Handle);
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
