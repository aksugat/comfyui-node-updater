using System.IO;
using System.Text;
using System.Windows;

namespace ComfyNodeUpdater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;

        // Headless modes exist so the packaged exe can be checked without a window on screen.
        if (args.Length >= 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            var report = args.Length >= 2 ? args[1] : null;
            var ok = Task.Run(() => SelfTest.RunAsync(report)).GetAwaiter().GetResult();
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--check", StringComparison.OrdinalIgnoreCase))
        {
            var report = args.Length >= 3 ? args[2] : null;
            var ok = Task.Run(() => Headless.RunCheckAsync(args[1], report)).GetAwaiter().GetResult();
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--pip", StringComparison.OrdinalIgnoreCase))
        {
            var node = args[1];
            var report = args.Length >= 3 ? args[2] : null;
            var settings = Settings.Load();
            var python = args.Length >= 4 ? args[3] : settings.PythonPath;
            var launcher = args.Length >= 5 ? args[4] : settings.PipLauncher;

            var ok = Task.Run(async () =>
            {
                var lines = new StringBuilder();
                var outcome = await PipInstaller.InstallAsync(node, python, launcher, line => lines.AppendLine(line));

                if (report is not null)
                {
                    try
                    {
                        File.WriteAllText(report,
                            $"{(outcome.Ok ? "OK" : "FAIL")}  {outcome.Message}\npython: {python}\nlauncher: {launcher}\nnode: {node}\n\n{lines}");
                    }
                    catch
                    {
                        // report only
                    }
                }

                return outcome.Ok;
            }).GetAwaiter().GetResult();

            Shutdown(ok ? 0 : 1);
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "version.txt"),
                                  typeof(App).Assembly.GetName().Version?.ToString() ?? "?");
            }
            catch
            {
                // no console to print to
            }

            Shutdown(0);
            return;
        }

        var appSettings = Settings.Load();

        string? folder = null;
        string? themeId = null;

        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals("--folder", StringComparison.OrdinalIgnoreCase)) folder = args[i + 1];
            else if (args[i].Equals("--theme", StringComparison.OrdinalIgnoreCase)) themeId = args[i + 1];
        }

        Themes.Apply(Themes.ById(themeId ?? appSettings.Theme));
        new MainWindow(folder).Show();
    }
}
