using CommunityToolkit.Mvvm.ComponentModel;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Ui;

namespace Fluentometer.Logic.ViewModels;

public partial class GaugeViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _label = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(BarValue))]
    [NotifyPropertyChangedFor(nameof(IsEstimate))]
    private double? _utilization;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private string _usedLabel = "";

    [ObservableProperty] private string _limitLabel = "";
    [ObservableProperty] private long? _resetsAt;

    // Derived display properties. The dashboard binds these (x:Bind) so the value,
    // bar fill, and estimate state ride the SAME data channel as the label and can
    // never desync from it under ItemsRepeater container recycling.
    public string ValueText => Format.PercentOrEstimate(Utilization, UsedLabel);
    public double BarValue => Format.BarValue(Utilization);
    public bool IsEstimate => Utilization is null;

    public void Apply(Gauge g)
    {
        Id = g.Id;
        Label = g.Label;
        Utilization = g.Utilization;
        UsedLabel = g.UsedLabel;
        LimitLabel = g.LimitLabel;
        ResetsAt = g.ResetsAt;
    }
}
