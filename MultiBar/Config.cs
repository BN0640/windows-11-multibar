using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiBar;

public enum ItemKind { App, Group }
public enum BarEdge { Bottom, Top }
public enum BarAlignment { Center, Left }

/// <summary>A pinned app or a group of apps.</summary>
public sealed class ItemConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ItemKind Kind { get; set; } = ItemKind.App;
    public string Name { get; set; } = "";

    // App: launched through Aumid when set, otherwise through Path (exe, lnk, file).
    public string? Aumid { get; set; }
    public string? Path { get; set; }
    public string? Arguments { get; set; }
    /// <summary>Executable used to recognise the app's windows (target of a .lnk, etc.).</summary>
    public string? MatchPath { get; set; }
    /// <summary>Profile folder (--user-data-dir) whose windows belong to this item.</summary>
    public string? DataDir { get; set; }
    /// <summary>Custom icon (.ico or .png) shown instead of the app's own.</summary>
    public string? IconPath { get; set; }

    // Group
    public List<ItemConfig> Members { get; set; } = new();
    /// <summary>Minimizing or restoring one window of the group does the same to all.</summary>
    public bool LinkMinimize { get; set; } = true;

    [JsonIgnore] public bool IsGroup => Kind == ItemKind.Group;

    public ItemConfig CloneApp() => new()
    {
        Kind = ItemKind.App, Name = Name, Aumid = Aumid, Path = Path,
        Arguments = Arguments, MatchPath = MatchPath, DataDir = DataDir, IconPath = IconPath
    };
}

public sealed class BarConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Monitor device name, e.g. \\.\DISPLAY1.</summary>
    public string Monitor { get; set; } = "";
    public BarEdge Edge { get; set; } = BarEdge.Bottom;
    public BarAlignment Alignment { get; set; } = BarAlignment.Center;
    public bool ShowRunningWindows { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowStartButton { get; set; } = true;
    /// <summary>Tray chevron, quick settings (network/volume) and notifications.</summary>
    public bool ShowTray { get; set; }
    public List<ItemConfig> Items { get; set; } = new();
}

public sealed class AppConfig
{
    public List<BarConfig> Bars { get; set; } = new();
    /// <summary>Hide the Windows taskbars while MultiBar runs.</summary>
    public bool HideWindowsTaskbar { get; set; }
    /// <summary>Windows taskbar state before MultiBar hid it; kept on disk so a crash can still be undone.</summary>
    public int? SavedTaskbarState { get; set; }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiBar");
    public static string FilePath => System.IO.Path.Combine(Folder, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the broken file for the user and start clean.
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            System.Windows.MessageBox.Show("Não consegui ler config.json, comecei uma configuração nova.\n\n" + ex.Message, "MultiBar");
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, FilePath, true);
    }
}
