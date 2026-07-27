namespace DriveVisualizer.Core;

public enum FileCategory
{
    Apps,
    Archives,
    Pictures,
    Documents,
    TempAndLogs,
    Code,
    DiskImages,
    Media,
    Other,
}

/// <summary>
/// Extension → semantic category mapping. Lives in Core so snapshots, reports,
/// and the UI legend all agree on what a file "is"; the UI supplies colors.
/// </summary>
public static class FileClassification
{
    public static readonly string[] DisplayNames =
    [
        "Apps & libraries",
        "Archives",
        "Pictures",
        "Documents",
        "Temp & logs",
        "Code & dev",
        "Disk images & VMs",
        "Video & audio",
        "Other",
    ];

    public static int CategoryCount => DisplayNames.Length;

    public static string NameOf(FileCategory category) => DisplayNames[(int)category];

    private static readonly Dictionary<string, FileCategory> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Apps & libraries
        [".exe"] = FileCategory.Apps, [".dll"] = FileCategory.Apps, [".sys"] = FileCategory.Apps,
        [".msi"] = FileCategory.Apps, [".msix"] = FileCategory.Apps, [".winmd"] = FileCategory.Apps,
        [".drv"] = FileCategory.Apps, [".mui"] = FileCategory.Apps, [".so"] = FileCategory.Apps,
        [".lib"] = FileCategory.Apps, [".pdb"] = FileCategory.Apps, [".mun"] = FileCategory.Apps,
        // Archives
        [".zip"] = FileCategory.Archives, [".rar"] = FileCategory.Archives, [".7z"] = FileCategory.Archives,
        [".tar"] = FileCategory.Archives, [".gz"] = FileCategory.Archives, [".bz2"] = FileCategory.Archives,
        [".xz"] = FileCategory.Archives, [".cab"] = FileCategory.Archives, [".iso"] = FileCategory.Archives,
        [".zst"] = FileCategory.Archives, [".nupkg"] = FileCategory.Archives,
        // Pictures
        [".jpg"] = FileCategory.Pictures, [".jpeg"] = FileCategory.Pictures, [".png"] = FileCategory.Pictures,
        [".gif"] = FileCategory.Pictures, [".bmp"] = FileCategory.Pictures, [".webp"] = FileCategory.Pictures,
        [".heic"] = FileCategory.Pictures, [".svg"] = FileCategory.Pictures, [".ico"] = FileCategory.Pictures,
        [".tif"] = FileCategory.Pictures, [".tiff"] = FileCategory.Pictures, [".raw"] = FileCategory.Pictures,
        [".psd"] = FileCategory.Pictures,
        // Documents
        [".pdf"] = FileCategory.Documents, [".doc"] = FileCategory.Documents, [".docx"] = FileCategory.Documents,
        [".xls"] = FileCategory.Documents, [".xlsx"] = FileCategory.Documents, [".ppt"] = FileCategory.Documents,
        [".pptx"] = FileCategory.Documents, [".txt"] = FileCategory.Documents, [".md"] = FileCategory.Documents,
        [".rtf"] = FileCategory.Documents, [".odt"] = FileCategory.Documents, [".csv"] = FileCategory.Documents,
        [".epub"] = FileCategory.Documents, [".one"] = FileCategory.Documents,
        // Temp & logs
        [".tmp"] = FileCategory.TempAndLogs, [".log"] = FileCategory.TempAndLogs, [".etl"] = FileCategory.TempAndLogs,
        [".dmp"] = FileCategory.TempAndLogs, [".cache"] = FileCategory.TempAndLogs, [".bak"] = FileCategory.TempAndLogs,
        [".old"] = FileCategory.TempAndLogs,
        // Code & dev
        [".cs"] = FileCategory.Code, [".cpp"] = FileCategory.Code, [".c"] = FileCategory.Code,
        [".h"] = FileCategory.Code, [".hpp"] = FileCategory.Code, [".js"] = FileCategory.Code,
        [".ts"] = FileCategory.Code, [".tsx"] = FileCategory.Code, [".jsx"] = FileCategory.Code,
        [".py"] = FileCategory.Code, [".java"] = FileCategory.Code, [".go"] = FileCategory.Code,
        [".rs"] = FileCategory.Code, [".rb"] = FileCategory.Code, [".php"] = FileCategory.Code,
        [".html"] = FileCategory.Code, [".css"] = FileCategory.Code, [".scss"] = FileCategory.Code,
        [".xaml"] = FileCategory.Code, [".json"] = FileCategory.Code, [".xml"] = FileCategory.Code,
        [".yml"] = FileCategory.Code, [".yaml"] = FileCategory.Code, [".toml"] = FileCategory.Code,
        [".sql"] = FileCategory.Code, [".sh"] = FileCategory.Code, [".ps1"] = FileCategory.Code,
        [".psm1"] = FileCategory.Code, [".ipynb"] = FileCategory.Code, [".csproj"] = FileCategory.Code,
        [".sln"] = FileCategory.Code, [".slnx"] = FileCategory.Code, [".lock"] = FileCategory.Code,
        // Disk images & VMs
        [".vhd"] = FileCategory.DiskImages, [".vhdx"] = FileCategory.DiskImages, [".vmdk"] = FileCategory.DiskImages,
        [".qcow2"] = FileCategory.DiskImages, [".wim"] = FileCategory.DiskImages, [".esd"] = FileCategory.DiskImages,
        [".img"] = FileCategory.DiskImages,
        // Video & audio
        [".mp4"] = FileCategory.Media, [".mkv"] = FileCategory.Media, [".avi"] = FileCategory.Media,
        [".mov"] = FileCategory.Media, [".wmv"] = FileCategory.Media, [".webm"] = FileCategory.Media,
        [".m4v"] = FileCategory.Media, [".mp3"] = FileCategory.Media, [".wav"] = FileCategory.Media,
        [".flac"] = FileCategory.Media, [".m4a"] = FileCategory.Media, [".ogg"] = FileCategory.Media,
        [".aac"] = FileCategory.Media, [".wma"] = FileCategory.Media,
    };

    public static FileCategory Classify(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var cat) ? cat : FileCategory.Other;
    }
}
