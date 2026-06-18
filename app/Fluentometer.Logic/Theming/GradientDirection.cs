namespace Fluentometer.Logic.Theming;

/// <summary>Direction a gauge bar's gradient runs as it fills.</summary>
public enum GradientDirection
{
    /// <summary>Stops as authored: bright start → deep end.</summary>
    BrightToDeep,

    /// <summary>Stops reversed: deep start → bright end.</summary>
    DeepToBright,
}
