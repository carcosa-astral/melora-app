using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Melora.Theme;

/// <summary>
/// The accent colour: Melora's periwinkle by default, or a colour taken from the playing album's art.
/// </summary>
public static class Accent
{
    public static readonly Color Default = Color.FromRgb(0x9B, 0xB0, 0xFF);

    const string ResourceKey = "AccentBrush";
    const int SampleSize = 48;
    const int HueBins = 36;
    static readonly Duration FadeTime = TimeSpan.FromMilliseconds(700);

    // Readable on the dark background: light enough for text, colourful but not neon.
    const double TargetLightness = 0.72;
    const double MinSaturation = 0.40;
    const double MaxSaturation = 0.80;

    // Art that's basically grey, or where the colour is a speck, keeps the default.
    const double MinColourfulShare = 0.06;

    /// <summary>
    /// Picks the most prominent vivid colour in an image, tuned to read well on Melora's dark theme.
    /// Returns null for greyscale or near-greyscale art. Safe to call off the UI thread.
    /// </summary>
    public static Color? FromImage(byte[] imageBytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = SampleSize;
        image.StreamSource = new MemoryStream(imageBytes);
        image.EndInit();

        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = pixels.PixelWidth * 4;
        var data = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(data, stride, 0);
        return FromPixels(data);
    }

    /// <summary>The picking itself, on raw BGRA pixels.</summary>
    public static Color? FromPixels(byte[] bgra)
    {
        var weight = new double[HueBins];
        var r = new double[HueBins];
        var g = new double[HueBins];
        var b = new double[HueBins];
        var counted = 0;

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue; // transparent
            counted++;

            var (h, s, l) = ToHsl(bgra[i + 2], bgra[i + 1], bgra[i]);
            // Colourful, mid-lightness pixels count most; greys, near-black and near-white barely count.
            var w = s * (1 - Math.Abs(l - 0.5) * 2);
            if (w < 0.15) continue;

            var bin = (int)(h / 360 * HueBins) % HueBins;
            weight[bin] += w;
            r[bin] += bgra[i + 2] * w;
            g[bin] += bgra[i + 1] * w;
            b[bin] += bgra[i] * w;
        }

        if (counted == 0) return null;

        // Neighbouring hue bins together, so a colour split across a bin edge still wins.
        var best = -1;
        var bestWeight = 0.0;
        for (var i = 0; i < HueBins; i++)
        {
            var around = weight[(i + HueBins - 1) % HueBins] + weight[i] + weight[(i + 1) % HueBins];
            if (around > bestWeight) { bestWeight = around; best = i; }
        }
        if (best < 0 || bestWeight / counted < MinColourfulShare) return null;

        // Average over the whole winning neighbourhood (the centre bin itself can be empty).
        double sumR = 0, sumG = 0, sumB = 0;
        for (var offset = -1; offset <= 1; offset++)
        {
            var bin = (best + offset + HueBins) % HueBins;
            sumR += r[bin];
            sumG += g[bin];
            sumB += b[bin];
        }
        var (hue, saturation, _) = ToHsl(sumR / bestWeight, sumG / bestWeight, sumB / bestWeight);
        return FromHsl(hue, Math.Clamp(saturation, MinSaturation, MaxSaturation), TargetLightness);
    }

    /// <summary>Fades the accent used across the app to a new colour.</summary>
    public static void Apply(Color color)
    {
        var resources = Application.Current.Resources;
        var from = (resources[ResourceKey] as SolidColorBrush)?.Color ?? Default;
        if (from == color) return;

        // A fresh brush each time, already fading before it goes into the resources: the dictionary
        // freezes brushes it can, and a frozen brush can't be animated, but a fading one can't be frozen.
        var brush = new SolidColorBrush(from);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, FadeTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        resources[ResourceKey] = brush;
    }

    static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        r /= 255; g /= 255; b /= 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d < 1e-6) return (0, 0, l);

        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
              : max == g ? (b - r) / d + 2
              : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));

        static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
