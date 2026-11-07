using System.IO;
using System.Text;

namespace ComfyNodeUpdater;

/// <summary>--self-test [report.txt]: builds throwaway repositories and checks the scan/guard/update rules against them.</summary>
public static class SelfTest
{
    public static async Task<bool> RunAsync(string? reportPath)
    {
        var report = new StringBuilder();
        var results = new List<(bool Pass, string Label)>();
        var root = Path.Combine(Path.GetTempPath(), "cnupd_selftest_" + Guid.NewGuid().ToString("N")[..8]);

        report.AppendLine($"ComfyUI Node Updater {typeof(SelfTest).Assembly.GetName().Version} self-test");
        report.AppendLine($"fixtures: {root}");
        report.AppendLine();

        void Check(bool condition, string label) => results.Add((condition, label));

        string Node(string name) => Path.Combine(root, "nodes", name);

        try
        {
            var remote = Path.Combine(root, "remote.git");
            var up = Path.Combine(root, "up");
            var nodes = Path.Combine(root, "nodes");
            Directory.CreateDirectory(nodes);

            await GitOk(root, "init", "--bare", "--quiet", remote);
            await GitOk(root, "init", "--quiet", up);
            await GitOk(up, "config", "user.email", "selftest@example.com");
            await GitOk(up, "config", "user.name", "selftest");
            File.WriteAllText(Path.Combine(up, "a.txt"), "one");
            await GitOk(up, "add", "-A");
            await GitOk(up, "commit", "--quiet", "-m", "one");
            await GitOk(up, "branch", "-M", "main");
            await GitOk(up, "remote", "add", "origin", remote);
            await GitOk(up, "push", "--quiet", "-u", "origin", "main");
            await GitOk(remote, "symbolic-ref", "HEAD", "refs/heads/main");

            foreach (var name in new[] { "Clean", "Behind", "Patched", "Dirty" })
            {
                await GitOk(root, "clone", "--quiet", remote, Node(name));
                await GitOk(Node(name), "config", "user.email", "selftest@example.com");
                await GitOk(Node(name), "config", "user.name", "selftest");
            }

            File.WriteAllText(Path.Combine(Node("Patched"), "patch.txt"), "our fix");
            await GitOk(Node("Patched"), "add", "-A");
            await GitOk(Node("Patched"), "commit", "--quiet", "-m", "local: our patch");

            File.WriteAllText(Path.Combine(Node("Dirty"), "a.txt"), "edited by hand");

            Directory.CreateDirectory(Path.Combine(nodes, "Plain"));

            File.WriteAllText(Path.Combine(up, "b.txt"), "two");
            await GitOk(up, "add", "-A");
            await GitOk(up, "commit", "--quiet", "-m", "two");
            await GitOk(up, "push", "--quiet", "origin", "main");

            await GitOk(root, "clone", "--quiet", remote, Node("UpToDate"));  // cloned after the push: this one is current

            var entries = NodeChecker.Scan(nodes);
            Check(entries.Count == 6, $"scan finds 6 folders (found {entries.Count})");

            foreach (var entry in entries.Where(e => !e.IsNonGit))
            {
                await NodeChecker.CheckAsync(entry);
            }

            NodeEntry Get(string name) => entries.First(e => e.Name == name);

            Check(Get("UpToDate").State == NodeState.UpToDate, "up-to-date repo is up to date");
            Check(Get("Clean").State == NodeState.UpdateAvailable && Get("Clean").Behind == 1,
                  "repo one commit behind is an available update");
            Check(Get("Behind").State == NodeState.UpdateAvailable, "second behind repo is an available update");
            Check(Get("Patched").State == NodeState.Protected && Get("Patched").Ahead == 1 && Get("Patched").Behind == 1,
                  "local commit is protected, though it is behind");
            Check(Get("Dirty").State == NodeState.Protected && Get("Dirty").Dirty == 1,
                  "uncommitted edit is protected");
            Check(Get("Plain").IsNonGit, "a folder without .git is marked non-git");

            var (packages, blockedNames, options) = PipInstaller.Filter(new[]
            {
                "# a comment", "", "numpy>=1.24", "torch==2.0.0", "triton", "pytorch-triton-rocm==3.9",
                "--extra-index-url https://example.invalid/simple", "opencv-python", "-r other.txt",
            });

            Check(packages.Count == 2 && packages[0].StartsWith("numpy") && packages[1].StartsWith("opencv-python"),
                  $"requirements filter keeps the safe packages (kept: {string.Join(", ", packages)})");
            Check(blockedNames.Distinct().Count() == 3 && blockedNames.Contains("torch") && blockedNames.Contains("triton"),
                  $"requirements filter blocks torch, triton and pytorch-triton-rocm (blocked: {string.Join(", ", blockedNames)})");
            Check(options.Count == 2, $"requirements filter drops pip option lines (dropped: {string.Join(", ", options)})");

            var patchedHead = await Git.Line(Node("Patched"), "rev-parse", "HEAD");
            var dirtyText = File.ReadAllText(Path.Combine(Node("Dirty"), "a.txt"));

            Check(await NodeChecker.UpdateAsync(Get("Clean")), "updating a clean node succeeds");
            Check(Get("Clean").State == NodeState.UpToDate, "the updated node is up to date afterwards");
            Check(await Git.Line(Node("Clean"), "rev-parse", "HEAD") == await Git.Line(up, "rev-parse", "HEAD"),
                  "the updated node sits on upstream's commit");

            Check(!await NodeChecker.UpdateAsync(Get("Patched")), "a protected node refuses to update");
            Check(File.Exists(Path.Combine(Node("Patched"), "patch.txt")), "the local patch file is still there");
            Check(await Git.Line(Node("Patched"), "rev-parse", "HEAD") == patchedHead, "the local commit still points where it did");

            Check(!await NodeChecker.UpdateAsync(Get("Dirty")), "a dirty node refuses to update");
            Check(File.ReadAllText(Path.Combine(Node("Dirty"), "a.txt")) == dirtyText, "the uncommitted edit is untouched");
        }
        catch (Exception ex)
        {
            Check(false, $"self-test threw: {ex.Message}");
        }

        foreach (var (pass, label) in results)
        {
            report.AppendLine($"{(pass ? "PASS" : "FAIL")}  {label}");
        }

        report.AppendLine();
        var failed = results.Count(r => !r.Pass);
        report.AppendLine(failed == 0 ? $"all {results.Count} checks passed" : $"{failed} of {results.Count} checks FAILED");

        try
        {
            if (reportPath is not null)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(reportPath, report.ToString());
            }
        }
        catch
        {
            // nothing else to report to
        }

        try { Directory.Delete(root, recursive: true); } catch { /* temp leftovers are harmless */ }

        return results.Count > 0 && failed == 0;
    }

    private static async Task GitOk(string directory, params string[] args)
    {
        var result = await Git.RunAsync(directory, args);
        if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {directory}: {result.Error}");
    }
}
