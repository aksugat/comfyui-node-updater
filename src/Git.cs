using System.Diagnostics;
using System.Text;

namespace ComfyNodeUpdater;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Error => StdErr.Length > 0 ? StdErr : StdOut;
}

/// <summary>Runs git with no prompts and no console window, so a private or dead remote fails instead of hanging.</summary>
public static class Git
{
    public static async Task<GitResult> RunAsync(string directory, params string[] args) =>
        await RunAsync(directory, CancellationToken.None, args);

    public static async Task<GitResult> RunAsync(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started (is it on PATH?)");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return new GitResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    public static async Task<string> Line(string directory, params string[] args)
    {
        var result = await RunAsync(directory, args);
        return result.Ok ? result.StdOut : "";
    }
}
