using System.Windows.Data;

namespace Melora.Library;

/// <summary>A file-based library (Music or Mixes) as the UI sees it: its tracks, view and totals.</summary>
public sealed class TrackLibrary : ObservableObject
{
    readonly string[] _groupBy;

    /// <param name="groupBy">The Track properties the list is grouped by, outermost first (album then disc for Music, DJ for Mixes).</param>
    public TrackLibrary(LibraryService service, params string[] groupBy)
    {
        Service = service;
        _groupBy = groupBy;
    }

    public LibraryService Service { get; }

    public LibraryKind Kind => Service.Kind;

    public List<Track> Tracks { get; private set; } = [];

    /// <summary>Raised on the UI thread after the track list is replaced.</summary>
    public event Action? Reloaded;

    ListCollectionView? _view;
    public ListCollectionView? View { get => _view; private set => Set(ref _view, value); }

    public bool IsEmpty => Tracks.Count == 0;

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    public void Reload()
    {
        Tracks = Service.Load();
        var view = new ListCollectionView(Tracks);
        foreach (var property in _groupBy) view.GroupDescriptions.Add(new PropertyGroupDescription(property));
        View = view;

        var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        var noun = Kind == LibraryKind.Mixes ? "mixes" : "tracks";
        Summary = Tracks.Count == 0 ? "" : $"{Tracks.Count:N0} {noun}  ·  {FormatTotal(total)}";
        OnPropertyChanged(nameof(IsEmpty));
        Reloaded?.Invoke();
    }

    /// <summary>A DJ's heading line: "12 mixes · 9h 30m · 2014–2025" (the years from the mixes' dates, when they have any).</summary>
    public static string DjSummary(IReadOnlyCollection<Track> mixes)
    {
        var parts = new List<string>
        {
            mixes.Count == 1 ? "1 mix" : $"{mixes.Count:N0} mixes",
            FormatTotal(TimeSpan.FromTicks(mixes.Sum(t => t.Duration.Ticks))),
        };
        var years = mixes.Select(t => LibraryService.MixDate(t.Title, t.Group.Year)?.Year).OfType<int>().ToList();
        if (years.Count > 0) parts.Add(years.Min() == years.Max() ? $"{years.Min()}" : $"{years.Min()}–{years.Max()}");
        return string.Join("  ·  ", parts);
    }

    public async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            if (await Service.ScanAsync()) Reload();
        }
        finally
        {
            IsScanning = false;
        }
    }

    public static string FormatTotal(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{t.Minutes}m";
}
