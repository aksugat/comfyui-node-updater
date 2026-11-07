using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyNodeUpdater;

/// <summary>--check &lt;folder&gt; [report.json]: the same scan the window does, without a window.</summary>
public static class Headless
{
    public static async Task<bool> RunCheckAsync(string targetDir, string? reportPath)
    {
        var report = new StringBuilder();
        report.AppendLine($"ComfyUI Node Updater {typeof(Headless).Assembly.GetName().Version}");
        report.AppendLine($"folder: {targetDir}");
        report.AppendLine($"checked: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        if (!Directory.Exists(targetDir))
        {
            report.AppendLine($"ERROR: folder not found: {targetDir}");
            Write(reportPath, report.ToString());
            return false;
        }

        var entries = NodeChecker.Scan(targetDir);
        var done = 0;

        using (var gate = new SemaphoreSlim(6))
        {
            var tasks = entries.Where(e => !e.IsNonGit).Select(async entry =>
            {
                await gate.WaitAsync();
                try
                {
                    await NodeChecker.CheckAsync(entry);
                }
                finally
                {
                    gate.Release();
                    Interlocked.Increment(ref done);
                }
            });

            await Task.WhenAll(tasks);
        }

        foreach (var entry in entries)
        {
            report.AppendLine($"{entry.Name,-42} {entry.State,-16} {entry.ShaText,-26} {entry.DetailText}");
        }

        var updates = entries.Count(e => e.State == NodeState.UpdateAvailable);
        var protectedNodes = entries.Count(e => e.State == NodeState.Protected);
        report.AppendLine();
        report.AppendLine($"updates available: {updates}");
        report.AppendLine($"left alone (local changes): {protectedNodes}");

        Write(reportPath, report.ToString());

        var json = JsonSerializer.Serialize(entries.Select(e => new
        {
            name = e.Name,
            path = e.FullPath,
            state = e.State.ToString(),
            behind = e.Behind,
            ahead = e.Ahead,
            dirty = e.Dirty,
            git = !e.IsNonGit,
            sha = e.ShaText,
            detail = e.DetailText,
        }), new JsonSerializerOptions { WriteIndented = true });

        if (reportPath is not null)
        {
            var jsonPath = Path.ChangeExtension(reportPath, ".json");
            try { File.WriteAllText(jsonPath, json); } catch { /* report only */ }
        }

        return true;
    }

    private static void Write(string? path, string text)
    {
        if (path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
        }
        catch
        {
            // nothing else to do without a console
        }
    }
}
