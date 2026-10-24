using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Melora.Discord;

namespace Melora.Library;

/// <summary>
/// Covers for albums, and pictures for DJs, that have none of their own: found online with
/// <see cref="CoverLookup"/> and saved as image files in <c>data\covers</c>. The music files and
/// their folders are never written to.
/// </summary>
public sealed class OnlineArt(CoverLookup lookup, string folder)
{
    // One lookup at a time: a screenful of albums at once would trip the stores' rate limits.
    readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Off: nothing is looked up, and pictures saved earlier aren't shown.</summary>
    public bool Enabled { get; set; } = true;

    public string Folder => folder;

    /// <summary>A picture found earlier for this album or DJ, or null. Reads from disk only.</summary>
    public byte[]? ReadSaved(CoverQuery query)
    {
        if (!Enabled) return null;
        var file = FileFor(query);
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    /// <summary>The saved picture, or else one looked up and downloaded now (and saved); null if there's none.</summary>
    public async Task<byte[]?> FindAsync(CoverQuery query)
    {
        if (!Enabled) return null;
        await _one.WaitAsync();
        try
        {
            if (ReadSaved(query) is { } saved) return saved; // found while this one waited

            var url = await lookup.FindAsync(query);
            var bytes = url is null ? null : await lookup.DownloadAsync(url);
            if (bytes is null || !IsImage(bytes)) return null;

            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(FileFor(query), bytes);
            return bytes;
        }
        catch (IOException e)
        {
            Log.Write($"Couldn't save a cover in {folder}: {e.Message}");
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The file a picture is saved in, named after the lookup's key so it survives rescans.</summary>
    public string FileFor(CoverQuery query)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(CoverLookup.Key(query)));
        return Path.Combine(folder, Convert.ToHexStringLower(hash)[..16] + ".jpg");
    }

    static bool IsImage(byte[] bytes)
    {
        try
        {
            BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
