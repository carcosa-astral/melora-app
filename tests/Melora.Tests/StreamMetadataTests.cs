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
