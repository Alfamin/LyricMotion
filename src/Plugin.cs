using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Plugins;

namespace LyricMotion;

/// <summary>
/// Lyric Motion: calm, even motion for word-synced lyrics. Every word floats up as it is
/// sung and its letters grow a touch as the highlight reaches them; held words grow more,
/// glow, and settle gently afterwards. It adds motion on top of Noctis' own colour sweep, and lays
/// right-to-left lyrics out from the right; it never touches the lyrics or the timing.
/// </summary>
public sealed class LyricMotionPlugin : INoctisPlugin
{
    private Director? _director;

    public PluginInfo Info { get; } = new(
        Id: "dev.moshi.lyricmotion",
        Name: "Lyric Motion",
        Version: "1.4.0",
        Author: "moshi",
        Description: "Smooth motion for word-synced lyrics: each word floats up as it is sung and its letters grow a touch as the highlight reaches them; held words grow more, glow, and settle gently. Persian, Arabic and Hebrew lyrics are laid out right to left.");

    public void Initialize(IPluginHost host)
    {
        _director = new Director(host);
        _director.Start();
    }

    public void Shutdown()
    {
        _director?.Dispose();
        _director = null;
    }
}

/// <summary>
/// Finds the lyric surfaces Noctis has on screen and keeps one <see cref="Surface"/> per
/// list of lines. Noctis' plugin kit has no hook into lyric rendering, so the plugin
/// recognises the lyric lists by how Noctis builds them (word rows carry the
/// "word-layer" class) on the lyrics page, the side panel and the mini player alike.
/// </summary>
internal sealed class Director : IDisposable
{
    private const string SidePanelListName = "PanelItemsControl";
    private const int MaxFailures = 3;

    private readonly IPluginHost _host;
    private readonly Dictionary<ItemsControl, Surface> _surfaces = new();
    private readonly List<IDisposable> _hooks = new();
    private DispatcherTimer? _scout;
    private int _failures;
    private bool _disposed;

    public Director(IPluginHost host)
    {
        _host = host;
        Tuning = ReadTuning();
    }

    public Tuning Tuning { get; private set; }

    public bool IsPlaying
    {
        get
        {
            try { return _host.NowPlaying.IsPlaying; }
            catch { return true; }
        }
    }

    public void Start()
    {
        _hooks.Add(Control.LoadedEvent.AddClassHandler<ItemsControl>(OnListLoaded));
        _hooks.Add(Control.UnloadedEvent.AddClassHandler<ItemsControl>(OnListUnloaded));

        try { _host.Settings.Changed += OnSettingsChanged; }
        catch { /* Noctis without declared settings: defaults apply. */ }

        // Play / pause is told to us directly, so the motion picks up on the very next
        // frame after un-pausing; the timer below only covers what has no event (a seek
        // while paused, a list that appears).
        try { _host.NowPlaying.IsPlayingChanged += OnPlayingChanged; }
        catch { /* the timer alone still works */ }

        _scout = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, OnScout);

        // Lyrics that are already on screen when the plugin is switched on.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            foreach (var visual in window.GetVisualDescendants())
                if (visual is ItemsControl list) Consider(list);
        }

        _host.Log("ready");
    }

    private void OnListLoaded(ItemsControl list, RoutedEventArgs e) => Guard(() => Consider(list));

    private void OnListUnloaded(ItemsControl list, RoutedEventArgs e) => Guard(() =>
    {
        if (!_surfaces.Remove(list, out var surface)) return;
        surface.Dispose();
        UpdateScout();
    });

    /// <summary>A word row was loaded: make sure the list of lines it belongs to has a surface.</summary>
    private void Consider(ItemsControl list)
    {
        if (_disposed || !list.Classes.Contains("word-layer")) return;

        ItemsControl? lines = null;
        foreach (var ancestor in list.GetVisualAncestors())
        {
            if (ancestor is ItemsControl candidate && !candidate.Classes.Contains("word-layer"))
            {
                lines = candidate;
                break;
            }
        }
        if (lines is null || _surfaces.ContainsKey(lines)) return;

        // The side panel with its motion switched off still gets a surface: right-to-left
        // lyrics are turned round there too.
        _surfaces[lines] = new Surface(this, lines, motion: () => Tuning.Motion && (Tuning.SidePanel || lines.Name != SidePanelListName));
        UpdateScout();
    }

    private void UpdateScout()
    {
        if (_scout is null) return;
        if (_surfaces.Count > 0 && !_scout.IsEnabled) _scout.Start();
        else if (_surfaces.Count == 0 && _scout.IsEnabled) _scout.Stop();
    }

    private void OnPlayingChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // May arrive from the playback thread.
        Dispatcher.UIThread.Post(() => OnScout(null, EventArgs.Empty), DispatcherPriority.Render);
    }

    private void OnScout(object? sender, EventArgs e) => Guard(() =>
    {
        List<ItemsControl>? gone = null;
        foreach (var (lines, surface) in _surfaces)
        {
            if (TopLevel.GetTopLevel(lines) is null) (gone ??= new()).Add(lines);
            else surface.Scout();
        }
        if (gone is null) return;
        foreach (var lines in gone)
        {
            if (_surfaces.Remove(lines, out var surface)) surface.Dispose();
        }
        UpdateScout();
    });

    private void OnSettingsChanged(object? sender, string key) => Guard(() =>
    {
        Tuning = ReadTuning();
        // Rebuild from scratch so the new settings apply to the line being sung too.
        foreach (var surface in _surfaces.Values) surface.Release();
    });

    private Tuning ReadTuning()
    {
        try { return Tuning.From(_host.Settings); }
        catch { return Tuning.From(null); }
    }

    /// <summary>
    /// Runs plugin code that Noctis did not call itself (frame callbacks, the timer,
    /// load events). Noctis contains failures in the calls it makes into a plugin, but
    /// not in these, so an exception here would otherwise reach the app. After a few
    /// failures the plugin undoes everything it changed and stays out of the way.
    /// </summary>
    public void Guard(Action action, Action? onFailure = null)
    {
        if (_disposed) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try { onFailure?.Invoke(); } catch { /* nothing more to do */ }
            _failures++;
            try { _host.Log($"error {_failures}/{MaxFailures}: {ex.GetType().Name}: {ex.Message}"); } catch { }
            if (_failures >= MaxFailures)
            {
                try { _host.Log("too many errors: lyric motion switched itself off until the plugin is reloaded"); } catch { }
                Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _scout?.Stop();
        _scout = null;
        foreach (var hook in _hooks)
        {
            try { hook.Dispose(); } catch { }
        }
        _hooks.Clear();
        try { _host.Settings.Changed -= OnSettingsChanged; } catch { }
        try { _host.NowPlaying.IsPlayingChanged -= OnPlayingChanged; } catch { }

        foreach (var surface in _surfaces.Values)
        {
            try { surface.Dispose(); } catch { }
        }
        _surfaces.Clear();
    }
}
