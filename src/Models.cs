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
    private sealed record Chips(Brush GreenBg, Brush GreenFg, Brush BlueBg, Brush BlueFg,
                                Brush AmberBg, Brush AmberFg, Brush RedBg, Brush RedFg,
                                Brush GreyBg, Brush GreyFg);

    private static readonly Dictionary<string, Chips> ChipCache = new();

    /// <summary>Status colours of the current theme, built once per theme.</summary>
    private static Chips ChipSet()
    {
        var theme = Themes.Current;

        lock (ChipCache)
        {
            if (ChipCache.TryGetValue(theme.Id, out var cached)) return cached;

            var line = Themes.Color(theme.Line);
            var dim = Themes.Color(theme.Dim);

            var chips = new Chips(
                Tint(Themes.Color(theme.Green), 0x26), Solid(Themes.Color(theme.Green)),
                Tint(Themes.Color(theme.Accent), 0x26), Solid(Themes.Color(theme.Accent)),
                Tint(Themes.Color(theme.Amber), 0x26), Solid(Themes.Color(theme.Amber)),
                Tint(Themes.Color(theme.Red), 0x26), Solid(Themes.Color(theme.Red)),
                Tint(line, 0x55), Solid(dim));

            ChipCache[theme.Id] = chips;
            return chips;
        }
    }

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

    public Brush StatusBg
    {
        get
        {
            var chips = ChipSet();
            return State switch
            {
                NodeState.UpToDate => chips.GreenBg,
                NodeState.UpdateAvailable => chips.BlueBg,
                NodeState.Protected => chips.AmberBg,
                NodeState.Error => chips.RedBg,
                _ => chips.GreyBg,
            };
        }
    }

    public Brush StatusFg
    {
        get
        {
            var chips = ChipSet();
            return State switch
            {
                NodeState.UpToDate => chips.GreenFg,
                NodeState.UpdateAvailable => chips.BlueFg,
                NodeState.Protected => chips.AmberFg,
                NodeState.Error => chips.RedFg,
                _ => chips.GreyFg,
            };
        }
    }

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

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush Tint(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
