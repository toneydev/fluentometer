using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Logic.Ipc;

public interface IUsageClient
{
    event Action<UsageSnapshot>? SnapshotReceived;
    event Action<bool>? ConnectionChanged;
    Task StartAsync(CancellationToken ct);
    Task SendAsync(ClientCommand cmd);
}
