using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiWallpaper;

/// <summary>某张图片的轮换配置。</summary>
public sealed class ImageEntry
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>该图片在所选显示器上的显示时长（分钟）。</summary>
    [JsonPropertyName("minutes")]
    public int Minutes { get; set; } = 30;

    /// <summary>勾选要显示该图片的显示器内部编号（0-based，按当前枚举顺序）。</summary>
    [JsonPropertyName("monitors")]
    public List<int> Monitors { get; set; } = new();
}

public sealed class Config
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "";

    [JsonPropertyName("images")]
    public List<ImageEntry> Images { get; set; } = new();

    /// <summary>当只有单显示器或旧版系统（Win10 1809 以下）时的回退主壁纸。</summary>
    [JsonPropertyName("fallbackMainWallpaper")]
    public bool FallbackMainWallpaper { get; set; } = true;
}

/// <summary>负责读写配置 JSON 文件（存于 %APPDATA%\MultiWallpaper\config.json）。</summary>
public static class ConfigStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiWallpaper");

    public static string FilePath => Path.Combine(Dir, "config.json");

    public static void Save(Config c)
    {
        Directory.CreateDirectory(Dir);
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // 显式 UTF-8 无 BOM 写入
        File.WriteAllText(FilePath, JsonSerializer.Serialize(c, opts),
            new System.Text.UTF8Encoding(false));
    }

    public static Config? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            // 显式 UTF-8 读，避免系统代码页把中文路径读成乱码
            string json = File.ReadAllText(FilePath, new System.Text.UTF8Encoding(false));
            return JsonSerializer.Deserialize<Config>(json);
        }
        catch
        {
            return null;
        }
    }

    public static List<ImageEntry> ScanImages(string dir)
    {
        var exts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };
        var list = new List<ImageEntry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (exts.Contains(ext))
            {
                list.Add(new ImageEntry
                {
                    File = f,
                    Minutes = 30,
                    Monitors = new List<int>(),
                });
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a.File), Path.GetFileName(b.File)));
        return list;
    }
}
