using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ComfyNodeUpdater;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly Settings _settings;
    private readonly ObservableCollection<NodeEntry> _nodes = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly ICollectionView _view;
    private readonly bool _ephemeral;
    private bool _busy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TargetDir
    {
        get => _settings.TargetDir;
        set
        {
            _settings.TargetDir = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetDir)));
        }
    }

    public ObservableCollection<string> Activity => _log;

    public string PythonPath
    {
        get => _settings.PythonPath;
        set
        {
            _settings.PythonPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PythonPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PythonLabel)));
        }
    }

    public string PythonLabel => $"python: {_settings.PythonPath}";

    public MainWindow(string? folder = null)
    {
        _settings = Settings.Load();

        if (folder is not null && Directory.Exists(folder))
        {
            _settings.TargetDir = folder;
            _ephemeral = true;  // a one-off run: do not make it the saved folder
        }

        InitializeComponent();
        DataContext = this;

        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);

        _view = CollectionViewSource.GetDefaultView(_nodes);
        _view.Filter = o => OnlyUpdatesBox.IsChecked != true || ((NodeEntry)o).State == NodeState.UpdateAvailable;
        NodeList.ItemsSource = _view;
        LogList.ItemsSource = _log;

        OnlyUpdatesBox.IsChecked = _settings.OnlyUpdates;
        InstallReqBox.IsChecked = _settings.InstallRequirements;

        NodeEntry.SelectionChanged += UpdateFooter;
        Loaded += async (_, _) => await CheckAsync();
        Closed += (_, _) =>
        {
            NodeEntry.SelectionChanged -= UpdateFooter;
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.OnlyUpdates = OnlyUpdatesBox.IsChecked == true;
            _settings.InstallRequirements = InstallReqBox.IsChecked == true;
            if (!_ephemeral) _settings.Save();
        };
    }

    private async Task CheckAsync()
    {
        if (_busy) return;

        SetBusy(true);

        try
        {
            var dir = TargetDir;

            if (!Directory.Exists(dir))
            {
                Log($"Folder not found: {dir}");
                Log("Use Change folder to point at your ComfyUI custom_nodes folder.");
                return;
            }

            foreach (var old in _nodes) old.PropertyChanged -= EntryChanged;
            _nodes.Clear();

            foreach (var entry in NodeChecker.Scan(dir))
            {
                entry.PropertyChanged += EntryChanged;
                _nodes.Add(entry);
            }

            var gitNodes = _nodes.Where(n => !n.IsNonGit).ToList();
            Log($"Found {_nodes.Count} folders in {dir} ({gitNodes.Count} git repos)");

            if (gitNodes.Count == 0)
            {
                Log("Nothing to check: no folder has a .git.");
                return;
            }

            var done = 0;
            using var gate = new SemaphoreSlim(6);

            await Task.WhenAll(gitNodes.Select(async entry =>
            {
                await gate.WaitAsync();
                try
                {
                    await NodeChecker.CheckAsync(entry);
                }
                finally
                {
                    gate.Release();
                    var count = Interlocked.Increment(ref done);
                    Progress.Text = $"checking {count}/{gitNodes.Count}...";
                }
            }));

            Progress.Text = "";

            var updates = _nodes.Count(n => n.State == NodeState.UpdateAvailable);
            var left = _nodes.Count(n => n.State == NodeState.Protected);
            var errors = _nodes.Count(n => n.State == NodeState.Error);

            if (updates > 0) Log($"{updates} node{(updates == 1 ? "" : "s")} have{(updates == 1 ? "s" : "")} updates waiting.");
            else Log("No updates available: everything is on its upstream commit.");

            if (left > 0) Log($"{left} node{(left == 1 ? "" : "s")} left alone: local commits or uncommitted files.");
            if (errors > 0) Log($"{errors} node{(errors == 1 ? "" : "s")} could not be checked (see the rows).");
        }
        catch (Exception ex)
        {
            Log($"Check failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UpdateSelectedAsync()
    {
        if (_busy) return;

        var chosen = _nodes.Where(n => n.IsSelected && n.State == NodeState.UpdateAvailable).ToList();

        if (chosen.Count == 0)
        {
            Log("Nothing selected.");
            return;
        }

        SetBusy(true);

        try
        {
            Log($"Updating {chosen.Count} node{(chosen.Count == 1 ? "" : "s")}...");

            var index = 0;

            foreach (var entry in chosen)
            {
                index++;
                Progress.Text = $"updating {index}/{chosen.Count}...";

                var ok = await NodeChecker.UpdateAsync(entry);

                if (ok)
                {
                    entry.IsSelected = false;
                    Log($"  {entry.Name}: now {entry.StatusText} ({entry.ShaText})");

                    if (InstallReqBox.IsChecked == true && entry.HasRequirements)
                    {
                        await InstallRequirementsAsync(entry, confirm: false);
                    }
                }
                else
                {
                    Log($"  {entry.Name}: {entry.DetailText}");
                }
            }

            Progress.Text = "";
            Log("Done. Restart ComfyUI so the updated nodes load.");
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        ChangeButton.IsEnabled = !busy;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        if (FooterText is null || UpdateButton is null) return;

        var updates = _nodes.Count(n => n.State == NodeState.UpdateAvailable);
        var selected = _nodes.Count(n => n.IsSelected && n.State == NodeState.UpdateAvailable);
        var left = _nodes.Count(n => n.State == NodeState.Protected);

        var text = $"{selected} selected   |   {updates} update{(updates == 1 ? "" : "s")} available";
        if (left > 0) text += $"   |   {left} left alone";
        FooterText.Text = text;

        UpdateButton.IsEnabled = !_busy && selected > 0;
    }

    private void EntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateFooter();
        if (OnlyUpdatesBox.IsChecked == true) _view.Refresh();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Windows draws a white title bar over the dark window unless it is told not to.
        var handle = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
    }

    private void Log(string text)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {text}");

        while (_log.Count > 400) _log.RemoveAt(0);

        LogList.ScrollIntoView(_log[^1]);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void Update_Click(object sender, RoutedEventArgs e) => await UpdateSelectedAsync();

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick the ComfyUI custom_nodes folder",
            InitialDirectory = Directory.Exists(TargetDir) ? TargetDir : "",
        };

        if (dialog.ShowDialog(this) != true) return;

        TargetDir = dialog.FolderName;
        _settings.Save();
        await CheckAsync();
    }

    private async Task InstallRequirementsAsync(NodeEntry entry, bool confirm)
    {
        var python = PythonPath;

        if (confirm)
        {
            var preview = PipInstaller.Describe(entry.FullPath);

            var answer = MessageBox.Show(this,
                $"Install {entry.Name}'s requirements.txt into:\n{python}\n\n{preview}",
                "Install requirements", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) return;
        }

        var own = !_busy;
        if (own) SetBusy(true);

        entry.SetRequirements("installing requirements...");
        Log($"pip: {entry.Name} -> {python}");

        try
        {
            var outcome = await PipInstaller.InstallAsync(entry.FullPath, python,
                line => Dispatcher.Invoke(() => Log("  " + line)));

            entry.SetRequirements(outcome.Ok
                ? (outcome.Message == "installed" ? "requirements installed" : $"requirements: {outcome.Message}")
                : $"requirements failed: {outcome.Message}");

            Log($"  {entry.Name}: {outcome.Message}");
        }
        catch (Exception ex)
        {
            entry.SetRequirements($"requirements failed: {ex.Message}");
            Log($"  {entry.Name}: {ex.Message}");
        }
        finally
        {
            Progress.Text = "";
            if (own) SetBusy(false);
        }
    }

    private void InstallReq_Changed(object sender, RoutedEventArgs e)
    {
        _settings.InstallRequirements = InstallReqBox.IsChecked == true;
        if (!_ephemeral) _settings.Save();
    }

    private void ChangePython_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(PythonPath);

        var dialog = new OpenFileDialog
        {
            Title = "Pick the python.exe that runs ComfyUI",
            Filter = "python.exe|python.exe|Executables (*.exe)|*.exe",
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : "",
        };

        if (dialog.ShowDialog(this) != true) return;

        PythonPath = dialog.FileName;
        _settings.Save();
        Log($"Requirements install into {PythonPath}");
    }

    private async void InstallRequirements_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (((FrameworkElement)sender).DataContext is not NodeEntry entry) return;

        await InstallRequirementsAsync(entry, confirm: true);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _nodes.Where(n => n.CanSelect)) entry.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _nodes) entry.IsSelected = false;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _view.Refresh();
        _settings.OnlyUpdates = OnlyUpdatesBox.IsChecked == true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TargetDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Could not open the folder: {ex.Message}");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();
}
