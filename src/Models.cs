using System.ComponentModel;
using System.Windows.Media;

namespace ComfyNodeUpdater;

public enum NodeState
{
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,
    Protected,
    NoUpstream,
    Error,
    Updating,
}

/// <summary>One folder under custom_nodes: what upstream has, what this checkout has, and whether it is safe to touch.</summary>
public sealed class NodeEntry : INotifyPropertyChanged
{
    private static readonly Brush GreenBg = Frozen(0x1B, 0x36, 0x25);
    private static readonly Brush GreenFg = Frozen(0x56, 0xD3, 0x64);
    private static readonly Brush AmberBg = Frozen(0x3A, 0x2C, 0x12);
    private static readonly Brush AmberFg = Frozen(0xE3, 0xB3, 0x41);
    private static readonly Brush BlueBg = Frozen(0x14, 0x2A, 0x4C);
    private static readonly Brush BlueFg = Frozen(0x7A, 0xB0, 0xFF);
    private static readonly Brush RedBg = Frozen(0x3D, 0x1A, 0x1F);
    private static readonly Brush RedFg = Frozen(0xFF, 0x7B, 0x84);
    private static readonly Brush GreyBg = Frozen(0x1C, 0x23, 0x2F);
    private static readonly Brush GreyFg = Frozen(0x9A, 0xA7, 0xB8);

    private NodeState _state = NodeState.Unknown;
    private bool _selected;
    private string _detail = "";
    private string _currentSha = "";
    private string _upstreamSha = "";
    private string _upstreamRef = "";
    private int _behind;
    private int _ahead;
    private int _dirty;
    private List<string> _subjects = new();
    private string _requirements = "";

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsNonGit { get; init; }
    public bool HasRequirements { get; init; }
    public bool IsDisabled => Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

    public NodeState State
    {
        get => _state;
        set { _state = value; Refresh(); }
    }

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Refresh(); SelectionChanged?.Invoke(); }
    }

    public bool CanSelect => State == NodeState.UpdateAvailable;
    public int Behind => _behind;
    public int Ahead => _ahead;
    public int Dirty => _dirty;
    public string UpstreamRef => _upstreamRef;

    /// <summary>A local commit or an uncommitted edit: a reset would drop it, so this one is left alone.</summary>
    public bool HasLocalWork => _ahead > 0 || _dirty > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? SelectionChanged;

    public void SetResult(string currentSha, string upstreamRef, string upstreamSha, int behind, int ahead, int dirty,
                          List<string> subjects, string detail)
    {
        _currentSha = currentSha;
        _upstreamRef = upstreamRef;
        _upstreamSha = upstreamSha;
        _behind = behind;
        _ahead = ahead;
        _dirty = dirty;
        _subjects = subjects;
        _detail = detail;
        Refresh();
    }

    public void SetDetail(string detail)
    {
        _detail = detail;
        Refresh();
    }

    public void SetRequirements(string note)
    {
        _requirements = note;
        Refresh();
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public string StatusText => State switch
    {
        NodeState.Checking => "checking...",
        NodeState.Updating => "updating...",
        NodeState.UpToDate => "up to date",
        NodeState.UpdateAvailable => _behind == 1 ? "1 update" : $"{_behind} updates",
        NodeState.Protected => "left alone",
        NodeState.NoUpstream => "no upstream",
        NodeState.Error => "error",
        _ => IsNonGit ? "not a git repo" : "not checked",
    };

    public Brush StatusBg => State switch
    {
        NodeState.UpToDate => GreenBg,
        NodeState.UpdateAvailable => BlueBg,
        NodeState.Protected => AmberBg,
        NodeState.Error => RedBg,
        _ => GreyBg,
    };

    public Brush StatusFg => State switch
    {
        NodeState.UpToDate => GreenFg,
        NodeState.UpdateAvailable => BlueFg,
        NodeState.Protected => AmberFg,
        NodeState.Error => RedFg,
        _ => GreyFg,
    };

    public string ShaText
    {
        get
        {
            if (IsNonGit) return "";
            if (_currentSha.Length == 0) return "";
            if (State is NodeState.UpToDate or NodeState.Unknown) return _currentSha;
            if (_upstreamSha.Length == 0) return _currentSha;
            return $"{_currentSha}  ->  {_upstreamSha}";
        }
    }

    public string DetailText
    {
        get
        {
            if (IsNonGit) return "no .git folder: this one cannot be updated from upstream.";
            if (State == NodeState.Error || State == NodeState.NoUpstream) return _detail;
            if (State is NodeState.Checking or NodeState.Updating) return _detail;

            var parts = new List<string>();
            if (_dirty > 0) parts.Add($"{_dirty} uncommitted file{(_dirty == 1 ? "" : "s")} - a reset would drop them");
            if (_ahead > 0) parts.Add($"{_ahead} local commit{(_ahead == 1 ? "" : "s")} - a reset would drop it");
            if (State == NodeState.Protected && _behind > 0) parts.Add($"{_behind} upstream commit{(_behind == 1 ? "" : "s")} waiting");
            if (State == NodeState.UpdateAvailable && _subjects.Count > 0) parts.Add(string.Join("  |  ", _subjects));
            if (State == NodeState.UpToDate) parts.Add(_detail.Length > 0 ? _detail : "nothing new upstream");
            if (_requirements.Length > 0) parts.Add(_requirements);
            return string.Join("  |  ", parts.Where(p => p.Length > 0));
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
