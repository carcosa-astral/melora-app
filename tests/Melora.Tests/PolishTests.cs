using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Melora.Theme;

namespace Melora.Tests;

public class AccentTests
{
    /// <summary>BGRA pixels: <paramref name="count"/> of one colour.</summary>
    static IEnumerable<byte> Pixels(byte r, byte g, byte b, int count) =>
        Enumerable.Range(0, count).SelectMany(_ => new[] { b, g, r, (byte)255 });

    static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    static double Lightness(Color c) =>
        (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 2.0 / 255;

    [Fact]
    public void RedArt_GivesARedAccent_LightEnoughForTheDarkTheme()
    {
        var accent = Accent.FromPixels([.. Pixels(200, 30, 40, 100)]);

        Assert.NotNull(accent);
        var hue = Hue(accent.Value);
        Assert.True(hue < 10 || hue > 350, $"hue {hue}");
        Assert.InRange(Lightness(accent.Value), 0.65, 0.80);
    }

    [Fact]
    public void GreyArt_KeepsTheDefault() =>
        Assert.Null(Accent.FromPixels([.. Pixels(128, 128, 128, 80), .. Pixels(20, 20, 20, 20)]));

    [Fact]
    public void ASpeckOfColour_KeepsTheDefault() =>
        Assert.Null(Accent.FromPixels([.. Pixels(120, 120, 120, 98), .. Pixels(0, 200, 80, 2)]));

    [Fact]
    public void MostlyGreyWithABlueSubject_PicksBlue()
    {
        var accent = Accent.FromPixels([.. Pixels(90, 90, 95, 70), .. Pixels(30, 80, 220, 30)]);

        Assert.NotNull(accent);
        Assert.InRange(Hue(accent.Value), 205, 235);
    }

    [Fact]
    public void FromImage_ReadsARealImageFile()
    {
        // A small orange PNG, encoded the same way cover files are read.
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null,
            Pixels(240, 140, 30, 16).ToArray(), 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var accent = Accent.FromImage(stream.ToArray());

        Assert.NotNull(accent);
        Assert.InRange(Hue(accent.Value), 20, 40);
    }
}

public class ColumnLayoutTests
{
    [Fact]
    public void DraggingTitleArtistRight_ShrinksArtist()
    {
        var layout = new ColumnLayout { Artist = 200 };
        layout.DragTitleArtist(30);
        Assert.Equal(170, layout.Artist);
    }

    [Fact]
    public void DraggingArtistLength_MovesWidthBetweenThem_WithinLimits()
    {
        var layout = new ColumnLayout { Artist = 200, Length = 64 };

        layout.DragArtistLength(10);
        Assert.Equal((210, 54), (layout.Artist, layout.Length));

        layout.DragArtistLength(100); // Length can't go below its minimum
        Assert.Equal((216, 48), (layout.Artist, layout.Length));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_AndIgnoreGarbage()
    {
        var saved = new ColumnLayout { Artist = 260, Length = 90 }.Save();
        var loaded = new ColumnLayout();
        loaded.Load(saved);
        Assert.Equal((260, 90), (loaded.Artist, loaded.Length));

        loaded.Load("not,numbers");
        loaded.Load(null);
        Assert.Equal((260, 90), (loaded.Artist, loaded.Length));
    }
}

public class WindowPlacementTests
{
    static readonly Rect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var placement = new WindowPlacement(new Rect(100, 50, 1280, 800), Maximized: true);
        Assert.Equal(placement, WindowPlacement.Load(placement.Save()));
    }

    [Theory]
    [InlineData(100, 50, true)]     // on screen
    [InlineData(1850, 50, false)]   // only a sliver of the title bar would show
    [InlineData(2500, 100, false)]  // on a monitor that's been unplugged
    [InlineData(-200, 20, true)]    // partly off the left edge is fine
    public void IsVisibleOn(double left, double top, bool expected) =>
        Assert.Equal(expected, new WindowPlacement(new Rect(left, top, 1280, 800), false).IsVisibleOn(Screen));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("1,2,0,0,0")]
    public void Load_RejectsBadValues(string? saved) => Assert.Null(WindowPlacement.Load(saved));
}
