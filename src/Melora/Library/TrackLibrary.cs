using System.Windows.Data;

namespace Melora.Library;

/// <summary>A file-based library (Music or Mixes) as the UI sees it: its tracks, view and totals.</summary>
public sealed class TrackLibrary : ObservableObject
{
    readonly string _groupBy;

    /// <param name="groupBy">The Track property the list is grouped by (album for Music, DJ for Mixes).</param>
    public TrackLibrary(LibraryService service, string groupBy)
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
        view.GroupDescriptions.Add(new PropertyGroupDescription(_groupBy));
        View = view;

        var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        var noun = Kind == LibraryKind.Mixes ? "mixes" : "tracks";
        Summary = Tracks.Count == 0 ? "" : $"{Tracks.Count:N0} {noun}  ·  {FormatTotal(total)}";
        OnPropertyChanged(nameof(IsEmpty));
        Reloaded?.Invoke();
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

    static string FormatTotal(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalHours}h {t.Minutes}m";
}
