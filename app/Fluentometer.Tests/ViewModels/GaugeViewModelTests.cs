using System.Collections.Generic;
using System.ComponentModel;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.ViewModels;
using Xunit;

/// <summary>
/// Locks the invariant that a gauge's displayed VALUE comes from the SAME object
/// as its LABEL. The dashboard's "Sonnet shows the 5-hour value" bug came from
/// rendering the value on a separate, imperative channel (FindNamedChild +
/// leaked PropertyChanged closures) that desynced from the data-bound label under
/// ItemsRepeater recycling. Binding ValueText/BarValue/IsEstimate as derived
/// properties on the ViewModel makes value-and-label inseparable.
/// </summary>
public class GaugeViewModelTests
{
    [Fact]
    public void ValueTextIsPercentWhenUtilizationKnown()
    {
        var vm = new GaugeViewModel();
        vm.Apply(new Gauge("weekly_scoped", "Claude Weekly (Sonnet)", 0.03, "3%", null, "normal"));

        Assert.Equal("3%", vm.ValueText);
        Assert.Equal(0.03, vm.BarValue);
        Assert.False(vm.IsEstimate);
    }

    [Fact]
    public void ValueTextFallsBackToUsedLabelWhenEstimate()
    {
        var vm = new GaugeViewModel();
        vm.Apply(new Gauge("session", "Claude 5-hour", null, "~2M tokens", null, "local estimate"));

        Assert.Equal("~2M tokens", vm.ValueText);
        Assert.Equal(0.0, vm.BarValue);
        Assert.True(vm.IsEstimate);
    }

    [Fact]
    public void BarValueClampsAboveOne()
    {
        var vm = new GaugeViewModel();
        vm.Apply(new Gauge("session", "Claude 5-hour", 1.7, "170%", null, "normal"));
        Assert.Equal(1.0, vm.BarValue);
    }

    [Fact]
    public void ApplyRaisesPropertyChangedForDerivedDisplayProperties()
    {
        var vm = new GaugeViewModel();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Apply(new Gauge("weekly_scoped", "Claude Weekly (Sonnet)", 0.03, "3%", null, "normal"));

        // OneWay x:Bind targets only refresh if these notify on change.
        Assert.Contains(nameof(GaugeViewModel.ValueText), changed);
        Assert.Contains(nameof(GaugeViewModel.BarValue), changed);
        Assert.Contains(nameof(GaugeViewModel.IsEstimate), changed);
    }
}
