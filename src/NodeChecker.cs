using System.IO;

namespace ComfyNodeUpdater;

/// <summary>Reads what every custom-node folder is, and what upstream has for it.</summary>
public static class NodeChecker
{
    private static readonly string[] Fallbacks = { "origin/HEAD", "origin/main", "origin/master" };

    public static List<NodeEntry> Scan(string targetDir)
    {
        var entries = new List<NodeEntry>();

        if (!Directory.Exists(targetDir)) return entries;

        foreach (var dir in Directory.EnumerateDirectories(targetDir)
                                       .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            if (name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;

            var gitPath = Path.Combine(dir, ".git");
            var isGit = Directory.Exists(gitPath) || File.Exists(gitPath);

            entries.Add(new NodeEntry
            {
                Name = name,
                FullPath = dir,
                IsNonGit = !isGit,
                HasRequirements = File.Exists(Path.Combine(dir, "requirements.txt")),
            });
        }

        return entries;
    }

    public static async Task CheckAsync(NodeEntry entry, CancellationToken ct = default)
    {
        if (entry.IsNonGit)
        {
            entry.State = NodeState.Unknown;
            return;
        }

        entry.State = NodeState.Checking;
        entry.SetDetail("asking upstream...");

        try
        {
            var fetch = await Git.RunAsync(entry.FullPath, ct, "fetch", "--all", "--prune", "--quiet");

            if (!fetch.Ok && fetch.Error.Length > 0)
            {
                entry.State = NodeState.Error;
                entry.SetDetail($"fetch failed: {LastLine(fetch.Error)}");
                return;
            }

            var upstream = await ResolveUpstream(entry.FullPath, ct);
            var current = await Git.Line(entry.FullPath, "rev-parse", "--short=8", "HEAD");

            if (upstream is null)
            {
                entry.State = NodeState.NoUpstream;
                entry.SetResult(current, "", "", 0, 0, 0, new List<string>(),
                                "no upstream branch (git has no @{u}, origin/HEAD, origin/main or origin/master)");
                return;
            }

            var behind = await Count(entry.FullPath, ct, $"HEAD..{upstream}");
            var ahead = await Count(entry.FullPath, ct, $"{upstream}..HEAD");
            var upstreamSha = await Git.Line(entry.FullPath, "rev-parse", "--short=8", upstream);
            var dirty = await DirtyCount(entry.FullPath, ct);
            var subjects = await Subjects(entry.FullPath, ct, upstream);

            var state = dirty > 0 || ahead > 0 ? NodeState.Protected
                      : behind > 0 ? NodeState.UpdateAvailable
                      : NodeState.UpToDate;

            entry.State = state;
            entry.SetResult(current, upstream, upstreamSha, behind, ahead, dirty, subjects, "");
        }
        catch (OperationCanceledException)
        {
            entry.State = NodeState.Unknown;
            entry.SetDetail("check cancelled");
        }
        catch (Exception ex)
        {
            entry.State = NodeState.Error;
            entry.SetDetail(ex.Message);
        }
    }

    public static async Task<bool> UpdateAsync(NodeEntry entry, CancellationToken ct = default)
    {
        if (entry.State != NodeState.UpdateAvailable || entry.HasLocalWork) return false;

        entry.State = NodeState.Updating;
        entry.SetDetail($"updating to {entry.UpstreamRef}...");

        try
        {
            var reset = await Git.RunAsync(entry.FullPath, ct, "reset", "--hard", entry.UpstreamRef);

            if (!reset.Ok)
            {
                entry.State = NodeState.Error;
                entry.SetDetail($"update failed: {LastLine(reset.Error)}");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            entry.State = NodeState.Error;
            entry.SetDetail("update cancelled");
            return false;
        }

        await CheckAsync(entry, ct);
        return true;
    }

    private static async Task<string?> ResolveUpstream(string dir, CancellationToken ct)
    {
        var upstream = await Git.Line(dir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (upstream.Length > 0) return upstream;

        foreach (var candidate in Fallbacks)
        {
            var verify = await Git.RunAsync(dir, ct, "rev-parse", "--verify", "--quiet", candidate);
            if (verify.Ok) return candidate;
        }

        return null;
    }

    private static async Task<int> Count(string dir, CancellationToken ct, string range)
    {
        var raw = await Git.Line(dir, "rev-list", "--count", range);
        return int.TryParse(raw, out var n) ? n : 0;
    }

    private static async Task<int> DirtyCount(string dir, CancellationToken ct)
    {
        var status = await Git.RunAsync(dir, ct, "status", "--porcelain", "--untracked-files=no");
        if (!status.Ok) return 0;
        return status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<List<string>> Subjects(string dir, CancellationToken ct, string upstream)
    {
        var log = await Git.RunAsync(dir, ct, "log", "--format=%s", "-n", "3", $"HEAD..{upstream}");
        if (!log.Ok) return new List<string>();

        return log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0)
                         .Select(s => s.Length > 90 ? s[..89] + "..." : s)
                         .ToList();
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : text.Trim();
    }
}
