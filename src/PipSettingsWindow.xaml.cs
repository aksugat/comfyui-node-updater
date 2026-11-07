using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ComfyNodeUpdater;

public partial class PipSettingsWindow : Window
{
    public string PythonPath => PythonBox.Text.Trim();
    public string PipLauncher => LauncherBox.Text.Trim();

    public PipSettingsWindow(string python, string launcher)
    {
        InitializeComponent();

        PythonBox.Text = python;
        LauncherBox.Text = launcher;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Apply(this);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(PythonBox.Text.Trim());

        var dialog = new OpenFileDialog
        {
            Title = "Pick the python.exe that runs ComfyUI",
            Filter = "python.exe|python.exe|Executables (*.exe)|*.exe",
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : "",
        };

        if (dialog.ShowDialog(this) == true) PythonBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "The command can't be empty. Use {python} -m pip for the python above.",
                            "pip settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!LauncherBox.Text.Contains("pip", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The command should end with -m pip, e.g. conda run -n comfy python -m pip.",
                            "pip settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
