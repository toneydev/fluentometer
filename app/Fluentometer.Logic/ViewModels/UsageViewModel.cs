using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Ui;

namespace Fluentometer.Logic.ViewModels;

/// <summary>
/// Root view model for the dashboard. Receives <see cref="UsageSnapshot"/> events from
/// <see cref="IUsageClient"/> — ONE per provider per poll cycle — and routes each snapshot
/// to the matching <see cref="ProviderGroupViewModel"/> by <see cref="UsageSnapshot.Provider"/>.
///
/// <para>
/// <b>Group ordering:</b> groups appear in insertion order. The first provider to emit a
/// snapshot becomes the first group. In practice Claude always emits first, so the ordering
/// is stable: Claude, then any additional providers in detection order.
/// </para>
///
/// <para>
/// <b>Health rollup:</b> <see cref="Health"/> is a worst-of aggregate across all groups.
/// Severity order (highest → lowest): error > needs-signin > degraded > ok.
/// The dashboard uses this rollup for the top-level banners (degraded / needs-signin)
/// that exist today. Per-group health is exposed via <see cref="ProviderGroupViewModel.Health"/>.
/// </para>
///
/// <para>
/// <b>Flat <see cref="Gauges"/> collection:</b> maintained as a real
/// <see cref="ObservableCollection{T}"/> — a SelectMany-flattened mirror of all groups'
/// gauges — so that <see cref="TrayIconManager"/> can subscribe to CollectionChanged and
/// enumerate gauges without modification. The flat list is rebuilt whenever any group's
/// gauge collection changes or when a new group is added.
/// </para>
/// </summary>
public partial class UsageViewModel : ObservableObject
{
    private readonly IUsageClient _client;
    private readonly IUiDispatcher _dispatcher;

    // -------------------------------------------------------------------------
    // Observable properties (unchanged surface — backward-compatible with tray,
    // code-behind and tests that read Plan / Health / IsConnected / IsDemoMode)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Human-readable plan from the first group (e.g. "Max 20x", "Demo").
    /// Legacy property kept for backward-compat with any consumer that reads it.
    /// </summary>
    [ObservableProperty] private string _plan = "";

    /// <summary>
    /// Worst-of health rollup across all provider groups.
    /// Severity: error > needs-signin > degraded > ok.
    /// The dashboard's degraded/needs-signin banners read this property.
    /// </summary>
    [ObservableProperty] private string _health = "ok";

    [ObservableProperty] private bool _isConnected;

    /// <summary>
    /// When true, live snapshots from the client are ignored; the demo driver
    /// pushes synthetic data through <see cref="ApplyDemoSnapshot"/>. Session-only.
    /// </summary>
    [ObservableProperty] private bool _isDemoMode;

    // -------------------------------------------------------------------------
    // Groups — the primary multi-provider collection
    // -------------------------------------------------------------------------

    /// <summary>
    /// One entry per detected provider, in insertion (first-seen) order.
    /// The dashboard outer ItemsRepeater binds to this collection.
    /// </summary>
    public ObservableCollection<ProviderGroupViewModel> Groups { get; } = new();

    // -------------------------------------------------------------------------
    // Flat Gauges — maintained for TrayIconManager backward-compat
    // -------------------------------------------------------------------------

    /// <summary>
    /// Flattened SelectMany mirror of all groups' gauges, kept in sync as group
    /// gauge collections change. TrayIconManager subscribes to CollectionChanged
    /// on this collection; DashboardPage code-behind reads Count for empty-state.
    ///
    /// <para>
    /// Rebuild strategy: on any group's CollectionChanged (or new group added),
    /// clear and re-populate from Groups in order. This is O(N) in total gauge
    /// count — small and bounded (typically 3-6 gauges) — and avoids the complexity
    /// of incremental splicing into the flat list.
    /// </para>
    /// </summary>
    public ObservableCollection<GaugeViewModel> Gauges { get; } = new();

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public UsageViewModel(IUsageClient client, IUiDispatcher dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        _client.SnapshotReceived += OnSnapshot;
        _client.ConnectionChanged += c => _dispatcher.Post(() => IsConnected = c);
    }

    // -------------------------------------------------------------------------
    // Snapshot routing
    // -------------------------------------------------------------------------

    private void OnSnapshot(UsageSnapshot snap)
    {
        if (IsDemoMode) return;                // demo data owns the gauges right now
        _dispatcher.Post(() => ApplySnapshot(snap));
    }

    /// <summary>Applies a synthetic demo snapshot through the same group path as live data.</summary>
    public void ApplyDemoSnapshot(UsageSnapshot snap) => _dispatcher.Post(() => ApplySnapshot(snap));

    private void ApplySnapshot(UsageSnapshot snap)
    {
        // Locate or create the group for this provider.
        var group = FindOrCreateGroup(snap.Provider);
        group.ApplySnapshot(snap);

        // Keep Plan on the first group's snapshot for backward-compat.
        if (Groups.Count > 0 && Groups[0] == group)
            Plan = snap.Plan;

        // Recompute the worst-of health rollup.
        Health = ComputeRollupHealth();

        // Rebuild the flat gauge mirror (tray + empty-state).
        RebuildFlatGauges();
    }

    // -------------------------------------------------------------------------
    // Group management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Removes all provider groups and clears the flat gauge mirror.
    /// Called by <see cref="Demo.DemoDriver.End"/> before repopulating from live data,
    /// so demo-only groups (e.g. Gemini shown in demo but not installed) do not
    /// persist stale on the dashboard after exiting demo mode.
    /// Must be called on the UI thread.
    /// </summary>
    internal void ClearGroups()
    {
        // Unsubscribe from all group gauge-change events to avoid leaks.
        foreach (var g in Groups)
            g.Gauges.CollectionChanged -= OnGroupGaugesChanged;

        Groups.Clear();
        Gauges.Clear();
    }

    /// <summary>
    /// Posts <see cref="ClearGroups"/> through the UI dispatcher.
    /// Called by <see cref="Demo.DemoDriver.End"/> so the clear happens on the same
    /// thread as all other collection mutations.
    /// </summary>
    internal void PostClearGroups() => _dispatcher.Post(ClearGroups);

    private ProviderGroupViewModel FindOrCreateGroup(string providerId)
    {
        foreach (var g in Groups)
            if (g.ProviderId == providerId) return g;

        // New provider seen — create group and subscribe to its gauge changes.
        var newGroup = new ProviderGroupViewModel(providerId);
        newGroup.Gauges.CollectionChanged += OnGroupGaugesChanged;
        Groups.Add(newGroup);
        return newGroup;
    }

    private void OnGroupGaugesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Any group's gauge list changed → rebuild the flat mirror and empty-state signal.
        RebuildFlatGauges();
    }

    // -------------------------------------------------------------------------
    // Flat gauge mirror rebuild
    // -------------------------------------------------------------------------

    private void RebuildFlatGauges()
    {
        // Clear and repopulate in Groups order.  O(N) where N = total gauge count.
        Gauges.Clear();
        foreach (var g in Groups)
            foreach (var gauge in g.Gauges)
                Gauges.Add(gauge);
    }

    // -------------------------------------------------------------------------
    // Health rollup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes worst-of health across all groups.
    /// Severity precedence: error (4) > needs-signin (3) > degraded (2) > ok (1).
    /// An empty Groups collection returns "ok".
    /// </summary>
    private string ComputeRollupHealth()
    {
        var worst = 0;
        foreach (var g in Groups)
            worst = System.Math.Max(worst, HealthSeverity(g.Health));
        return worst switch
        {
            4 => "error",
            3 => "needs-signin",
            2 => "degraded",
            _ => "ok",
        };
    }

    private static int HealthSeverity(string health) => health switch
    {
        "error" => 4,
        "needs-signin" => 3,
        "degraded" => 2,
        "ok" => 1,
        _ => 0,
    };

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private Task Refresh() => _client.SendAsync(ClientCommand.RefreshNow());
}
