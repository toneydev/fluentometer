using System;
using Fluentometer.Logic.Ui;
using Microsoft.UI.Dispatching;

namespace Fluentometer.Ui;

/// <summary>
/// Concrete IUiDispatcher that marshals actions onto the WinUI DispatcherQueue.
/// Constructed with the queue from the main window's thread so all UI updates
/// land on the correct thread without blocking the caller.
/// </summary>
public sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action) => queue.TryEnqueue(action.Invoke);
}
