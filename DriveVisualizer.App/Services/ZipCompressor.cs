using System.IO.Compression;

namespace DriveVisualizer_App.Services;

public static class ZipCompressor
{
    /// <summary>Sibling path "name.zip", or "name (2).zip" etc. if taken.</summary>
    public static string MakeZipPath(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath)!;
        string name = Path.GetFileName(sourcePath);
        string candidate = Path.Combine(dir, name + ".zip");
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}).zip");
        return candidate;
    }

    /// <summary>Compresses a file or directory into <paramref name="zipPath"/>; returns the zip's size.</summary>
    public static Task<long> CompressAsync(string sourcePath, string zipPath, bool isDirectory) => Task.Run(() =>
    {
        if (isDirectory)
        {
            ZipFile.CreateFromDirectory(sourcePath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        else
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Optimal);
        }
        return new FileInfo(zipPath).Length;
    });
}
