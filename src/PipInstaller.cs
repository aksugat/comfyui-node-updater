using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyNodeUpdater;

public sealed record PipOutcome(bool Ok, string Message, List<string> Installed, List<string> Blocked);

/// <summary>
/// Installs a node's requirements.txt into the portable Python. Two rules:
/// never install a package that would replace the ROCm torch/triton build, and dry-run first so a
/// dependency that would drag one in is caught before anything is written.
/// </summary>
public static class PipInstaller
{
    /// <summary>Packages whose PyPI wheels would replace the ROCm build of this environment.</summary>
    public static readonly string[] Blocked =
    {
        "torch", "torchvision", "torchaudio", "torchtext",
        "triton", "triton-windows", "pytorch-triton", "pytorch-triton-rocm",
        "xformers", "flash-attn", "flash_attn", "tensorrt", "onnxruntime-gpu",
    };

    public static bool IsBlocked(string name) =>
        Blocked.Contains(name, StringComparer.OrdinalIgnoreCase);

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

    public static async Task<PipOutcome> InstallAsync(string nodePath, string pythonPath, Action<string> onLine,
                                                       CancellationToken ct = default)
    {
        var requirement = Path.Combine(nodePath, "requirements.txt");

        if (!File.Exists(requirement)) return new PipOutcome(false, "no requirements.txt", new List<string>(), new List<string>());
        if (!File.Exists(pythonPath)) return new PipOutcome(false, $"python not found: {pythonPath}", new List<string>(), new List<string>());

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
            onLine($"dry run: {packages.Count} package(s) into {pythonPath}");
            var dry = await RunAsync(pythonPath, nodePath, onLine, ct,
                "-m", "pip", "install", "-r", temp, "--dry-run", "--report", report,
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
            else if (dry.ExitCode != 0)
            {
                // an older pip without --dry-run: install without dependencies instead of guessing
                onLine("pip has no --dry-run; installing with --no-deps");
                var noDeps = await RunAsync(pythonPath, nodePath, onLine, ct,
                    "-m", "pip", "install", "-r", temp, "--no-deps", "--disable-pip-version-check", "--no-input",
                    "--progress-bar", "off");
                return new PipOutcome(noDeps.ExitCode == 0,
                    noDeps.ExitCode == 0 ? "installed (without dependencies)" : $"pip failed: {LastLine(noDeps.StandardError)}",
                    packages, blocked);
            }
            else
            {
                return new PipOutcome(false, "pip dry run produced no report", new List<string>(), blocked);
            }

            onLine("installing...");
            var install = await RunAsync(pythonPath, nodePath, onLine, ct,
                "-m", "pip", "install", "-r", temp, "--disable-pip-version-check", "--no-input", "--progress-bar", "off");

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

    private sealed record ProcessResult(int ExitCode, string StandardError);

    private static async Task<ProcessResult> RunAsync(string pythonPath, string workingDirectory, Action<string> onLine,
                                                      CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("python could not be started");

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

        return new ProcessResult(process.ExitCode, errors.ToString());
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : text.Trim();
    }
}
