using System.IO;
using System.Text.Json;

namespace ComfyNodeUpdater;

/// <summary>Per-machine settings: last folder, window size, the list filter.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string TargetDir { get; set; } = DefaultDir();
    public double Width { get; set; } = 1020;
    public double Height { get; set; } = 740;
    public bool OnlyUpdates { get; set; }
    public bool InstallRequirements { get; set; }
    public string PythonPath { get; set; } = DefaultPython();

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComfyNodeUpdater");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(FilePath));
                if (loaded is not null && loaded.TargetDir.Length > 0) return loaded;
            }
        }
        catch
        {
            // a broken settings file just means defaults
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // settings are a convenience, never a reason to fail
        }
    }

    private static string DefaultDir()
    {
        var candidates = new[]
        {
            @"H:\rocm313\ComfyUI\custom_nodes",
            @"H:\ComfyUI\custom_nodes",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ComfyUI", "custom_nodes"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }

    private static string DefaultPython()
    {
        var candidates = new[]
        {
            @"H:\rocm313\python_embeded\python.exe",
            @"H:\ComfyUI\python_embeded\python.exe",
        };

        foreach (var candidate in candidates)
        {
            if (System.IO.File.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }
}
