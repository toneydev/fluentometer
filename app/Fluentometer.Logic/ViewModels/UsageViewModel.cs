using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Ui;

namespace Fluentometer.Logic.ViewModels;

public partial class UsageViewModel : ObservableObject
{
    private readonly IUsageClient _client;
    private readonly IUiDispatcher _dispatcher;

    [ObservableProperty] private string _plan = "";
    [ObservableProperty] private string _health = "ok";
    [ObservableProperty] private bool _isConnected;

    // When true, live snapshots from the pipe are ignored and the gauges are driven
    // by DemoDriver via ApplyDemoSnapshot. Session-only; never persisted.
    [ObservableProperty] private bool _isDemoMode;

    public ObservableCollection<GaugeViewModel> Gauges { get; } = new();

    public UsageViewModel(IUsageClient client, IUiDispatcher dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        _client.SnapshotReceived += OnSnapshot;
        _client.ConnectionChanged += c => _dispatcher.Post(() => IsConnected = c);
    }

    private void OnSnapshot(UsageSnapshot snap)
    {
        if (IsDemoMode) return;                 // demo data owns the gauges right now
        _dispatcher.Post(() => ApplySnapshot(snap));
    }

    /// <summary>Applies a synthetic demo snapshot through the same path as live data.</summary>
    public void ApplyDemoSnapshot(UsageSnapshot snap) => _dispatcher.Post(() => ApplySnapshot(snap));

    private void ApplySnapshot(UsageSnapshot snap)
    {
        Plan = snap.Plan;
        Health = snap.Health;
        for (var i = 0; i < snap.Gauges.Count; i++)
        {
            if (i < Gauges.Count) Gauges[i].Apply(snap.Gauges[i]);
            else { var vm = new GaugeViewModel(); vm.Apply(snap.Gauges[i]); Gauges.Add(vm); }
        }
        while (Gauges.Count > snap.Gauges.Count) Gauges.RemoveAt(Gauges.Count - 1);
    }

    [RelayCommand]
    private Task Refresh() => _client.SendAsync(ClientCommand.RefreshNow());
}
