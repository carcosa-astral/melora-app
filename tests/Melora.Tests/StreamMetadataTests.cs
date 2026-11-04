using Melora.Radio;

namespace Melora.Tests;

public class StreamMetadataTests
{
    [Theory]
    [InlineData("StreamTitle='Boards of Canada - Roygbiv';StreamUrl='';", "Boards of Canada - Roygbiv")]
    [InlineData("StreamTitle='Guns N' Roses - Patience';", "Guns N' Roses - Patience")]
    [InlineData("StreamTitle='Live set';", "Live set")]
    [InlineData("StreamTitle='';StreamUrl='';", null)]
    [InlineData("StreamUrl='http://x';", null)]
    [InlineData(null, null)]
    public void ParseIcyTitle(string? meta, string? expected) =>
        Assert.Equal(expected, StreamMetadata.ParseIcyTitle(meta));

    [Fact]
    public void FromVorbisComments_JoinsArtistAndTitle() =>
        Assert.Equal("Bonobo - Kerala", StreamMetadata.FromVorbisComments(["TITLE=Kerala", "ARTIST=Bonobo", "ENCODER=x"]));

    [Fact]
    public void FromVorbisComments_TitleOnly() =>
        Assert.Equal("Station ID", StreamMetadata.FromVorbisComments(["title=Station ID"]));

    [Theory]
    [InlineData("Boards of Canada - Roygbiv", "Boards of Canada", "Roygbiv")]
    [InlineData("Jay-Z - 99 Problems", "Jay-Z", "99 Problems")]
    [InlineData("A - B - C", "A", "B - C")]
    [InlineData("Station ID", null, "Station ID")]
    [InlineData("  ", null, null)]
    public void SplitArtistTitle(string input, string? artist, string? title) =>
        Assert.Equal((artist, title), StreamMetadata.SplitArtistTitle(input));

    [Fact]
    public void HeaderValue_FindsIcyName() =>
        Assert.Equal("Groove Salad", StreamMetadata.HeaderValue(["Content-Type: audio/mpeg", "icy-name: Groove Salad", "icy-br:128"], "icy-name"));

    [Theory]
    [InlineData("ice2.somafm.com/groovesalad-128-mp3", "http://ice2.somafm.com/groovesalad-128-mp3")]
    [InlineData("  https://stream.example.org/live.aac  ", "https://stream.example.org/live.aac")]
    [InlineData("http://radio.example:8000/stream", "http://radio.example:8000/stream")]
    [InlineData("ftp://example.org/x", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void NormalizeUrl(string input, string? expected) =>
        Assert.Equal(expected, StreamMetadata.NormalizeUrl(input));
}

public class StationFileTests
{
    [Fact]
    public void Stations_RoundTrip_ThroughAnM3uFile()
    {
        var stations = new List<(string, string)>
        {
            ("Groove Salad", "https://somafm.com/groovesalad256.pls"),
            ("BBC Radio 1 Dance, UK", "http://lstn.lv/bbcradio.m3u8?station=bbc_radio_one_dance&bitrate=320000"),
        };

        var text = StationFile.Write(stations);

        Assert.StartsWith("#EXTM3U", text);
        Assert.Equal(stations, StationFile.Read(text));
    }

    [Fact]
    public void Read_TakesPlainAddresses_AndSkipsAnythingElse() =>
        Assert.Equal(
            [("ice.example.org", "http://ice.example.org/live"), ("Named", "https://b.example/x")],
            StationFile.Read("ice.example.org/live\n# a comment\nnot an address at all\n#EXTINF:123,Named\nhttps://b.example/x\n"));
}
