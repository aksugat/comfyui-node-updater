using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyNodeUpdater;

public sealed record PipOutcome(bool Ok, string Message, List<string> Installed, List<string> Blocked);

/// <summary>
/// Installs a node's requirements.txt. Two rules: never install a package that would replace the
/// ROCm torch/triton build, and dry-run first so a dependency that would drag one in is caught
/// before anything is written.
///
/// The pip command comes from the launcher setting, so a conda user can run it in their env:
///   {python} -m pip                                              (default: the chosen python.exe)
///   conda run -n comfy --no-capture-output python -m pip
///   call C:\miniconda3\Scripts\activate.bat comfy &amp;&amp; python -m pip
/// </summary>
public static class PipInstaller
{
    public const string DefaultLauncher = "{python} -m pip";

    /// <summary>Packages whose PyPI wheels would replace the ROCm build of this environment.</summary>
    public static readonly string[] Blocked =
    {
        "torch", "torchvision", "torchaudio", "torchtext",
        "triton", "triton-windows", "pytorch-triton", "pytorch-triton-rocm",
        "xformers", "flash-attn", "flash_attn", "tensorrt", "onnxruntime-gpu",
    };

    public static bool IsBlocked(string name) =>
        Blocked.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsDefaultLauncher(string launcher) =>
        string.Equals(launcher.Trim(), DefaultLauncher, StringComparison.OrdinalIgnoreCase);

    /// <summary>The package a requirement line names, or null for a comment, blank or pip option.</summary>
    public static string? PackageName(string line)
    {
        var text = line.Trim();

        if (text.Length == 0) return null;
        if (text.StartsWith('#')) return null;
        if (text.StartsWith('-')) return null;                       // --index-url, -r, -e, ...
        if (text.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) return null;

        var end = text.IndexOfAny(new[] { '[', '=', '<', '>', '!', '~', ';', ' ', '\t', '@', '#' });
        var name = (end < 0 ? text : text[..end]).Trim();

        return name.Length == 0 ? null : name;
    }

    /// <summary>(packages to install, blocked names, dropped option lines).</summary>
    public static (List<string> Packages, List<string> Blocked, List<string> Options) Filter(IEnumerable<string> lines)
    {
        var packages = new List<string>();
        var blocked = new List<string>();
        var options = new List<string>();

        foreach (var line in lines)
        {
            var name = PackageName(line);

            if (name is null)
            {
                if (line.Trim().Length > 0 && !line.Trim().StartsWith('#')) options.Add(line.Trim());
                continue;
            }

            if (IsBlocked(name)) blocked.Add(name);
            else packages.Add(line.Trim());
        }

        return (packages, blocked, options);
    }

    public static string Describe(string nodePath)
    {
        var (packages, blocked, options) = Filter(ReadLines(nodePath));
        var text = new StringBuilder();

        text.AppendLine(packages.Count == 0 ? "nothing left to install" : $"{packages.Count} package(s) to install:");
        foreach (var package in packages) text.AppendLine($"  {package}");
        if (blocked.Count > 0) text.AppendLine($"skipped (would replace the ROCm build): {string.Join(", ", blocked.Distinct())}");
        if (options.Count > 0) text.AppendLine($"dropped pip option line(s): {string.Join(", ", options)}");

        return text.ToString();
    }

    public static async Task<PipOutcome> InstallAsync(string nodePath, string pythonPath, string launcher,
                                                       Action<string> onLine, CancellationToken ct = default)
    {
        var requirement = Path.Combine(nodePath, "requirements.txt");

        if (!File.Exists(requirement)) return new PipOutcome(false, "no requirements.txt", new List<string>(), new List<string>());

        if (IsDefaultLauncher(launcher) && !File.Exists(pythonPath))
            return new PipOutcome(false, $"python not found: {pythonPath}", new List<string>(), new List<string>());

        var (packages, blocked, options) = Filter(ReadLines(nodePath));

        if (packages.Count == 0)
        {
            var why = blocked.Count > 0
                ? $"only blocked packages ({string.Join(", ", blocked.Distinct())}): nothing installed"
                : "requirements.txt has no installable packages";
            return new PipOutcome(true, why, new List<string>(), blocked);
        }

        var temp = Path.Combine(Path.GetTempPath(), $"cnupd_req_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(temp, packages);
        var report = temp + ".json";

        try
        {
            onLine($"dry run: {packages.Count} package(s) via {(IsDefaultLauncher(launcher) ? pythonPath : launcher)}");
            var dry = await RunPipAsync(pythonPath, launcher, nodePath, onLine, ct,
                "install", "-r", temp, "--dry-run", "--report", report,
                "--disable-pip-version-check", "--no-input", "--progress-bar", "off");

            if (dry.ExitCode == 0 && File.Exists(report))
            {
                var wouldInstall = ReportNames(report);

                var dangerous = wouldInstall.Where(IsBlocked).Distinct().ToList();
                if (dangerous.Count > 0)
                {
                    return new PipOutcome(false,
                        $"refused: pip would install {string.Join(", ", dangerous)}, which would replace the ROCm build",
                        wouldInstall, blocked);
                }

                if (wouldInstall.Count == 0)
                    return new PipOutcome(true, "everything already satisfied", new List<string>(), blocked);

                onLine($"will install: {string.Join(", ", wouldInstall)}");
            }
            else if (dry.ExitCode != 0 && IsNoDryRun(dry.StandardError))
            {
                // an older pip without --dry-run: install without dependencies instead of guessing
                onLine("pip has no --dry-run; installing with --no-deps");
                var noDeps = await RunPipAsync(pythonPath, launcher, nodePath, onLine, ct,
                    "install", "-r", temp, "--no-deps", "--disable-pip-version-check", "--no-input",
                    "--progress-bar", "off");
                return new PipOutcome(noDeps.ExitCode == 0,
                    noDeps.ExitCode == 0 ? "installed (without dependencies)" : $"pip failed: {LastLine(noDeps.StandardError)}",
                    packages, blocked);
            }
            else if (dry.ExitCode != 0)
            {
                return new PipOutcome(false, $"pip failed: {LastLine(dry.StandardError)}", new List<string>(), blocked);
            }
            else
            {
                return new PipOutcome(false, "pip dry run produced no report", new List<string>(), blocked);
            }

            onLine("installing...");
            var install = await RunPipAsync(pythonPath, launcher, nodePath, onLine, ct,
                "install", "-r", temp, "--disable-pip-version-check", "--no-input", "--progress-bar", "off");

            if (install.ExitCode != 0)
                return new PipOutcome(false, $"pip failed: {LastLine(install.StandardError)}", new List<string>(), blocked);

            return new PipOutcome(true, "installed", packages, blocked);
        }
        catch (Exception ex)
        {
            return new PipOutcome(false, ex.Message, new List<string>(), blocked);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp */ }
            try { File.Delete(report); } catch { /* temp */ }
        }
    }

    private static List<string> ReadLines(string nodePath)
    {
        var path = Path.Combine(nodePath, "requirements.txt");
        return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    private static bool IsNoDryRun(string stderr) =>
        stderr.Contains("--dry-run", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("no such option", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReportNames(string reportPath)
    {
        var names = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));

            if (!document.RootElement.TryGetProperty("install", out var install)) return names;

            foreach (var item in install.EnumerateArray())
            {
                if (item.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("name", out var name) &&
                    name.GetString() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }
        }
        catch
        {
            // an unreadable report means the dry run told us nothing: the real install still runs
        }

        return names;
    }

    /// <summary>Runs "install ..." through the launcher: the python directly, or a shell command.</summary>
    private static async Task<ProcessResult> RunPipAsync(string pythonPath, string launcher, string workingDirectory,
                                                         Action<string> onLine, CancellationToken ct, params string[] pipArgs)
    {
        var all = new[] { "-m", "pip" }.Concat(pipArgs).ToArray();

        if (IsDefaultLauncher(launcher))
        {
            return await RunAsync(pythonPath, null, workingDirectory, onLine, ct, all);
        }

        var command = launcher.Replace("{python}", Quote(pythonPath)).Trim()
                      + " " + string.Join(' ', pipArgs.Select(Quote));

        // Run it from a temporary .cmd: cmd's own parsing then handles the quotes, including a
        // launcher under a path with spaces ("C:\...\my scripts\pip.cmd") and && chains.
        var shell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        var script = Path.Combine(Path.GetTempPath(), $"cnupd_pip_{Guid.NewGuid():N}.cmd");

        try
        {
            File.WriteAllText(script, "@echo off\r\n" + command + "\r\n");
            return await RunAsync(shell, $"/d /c \"{script}\"", workingDirectory, onLine, ct, null);
        }
        finally
        {
            try { File.Delete(script); } catch { /* temp */ }
        }
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')
            ? argument
            : $"\"{argument}\"";

    private sealed record ProcessResult(int ExitCode, string StandardError);

    private static async Task<ProcessResult> RunAsync(string fileName, string? arguments, string workingDirectory,
                                                      Action<string> onLine, CancellationToken ct, string[]? argv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (arguments is not null) psi.Arguments = arguments;
        else foreach (var arg in argv ?? Array.Empty<string>()) psi.ArgumentList.Add(arg);

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} could not be started");

        var errors = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errors.AppendLine(e.Data);
            onLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        // let the async output handlers finish before the reader is disposed
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, errors.ToString());
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : text.Trim();
    }
}
