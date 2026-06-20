namespace Fluentometer.Logic.Density;

/// <summary>How a gauge card paints its usage fill at a given density.
/// <see cref="Wipe"/> is the signature full-card gradient wipe (Comfortable/Compact);
/// <see cref="Track"/> is a slim gradient bar pinned to the card's bottom edge (Mini).</summary>
public enum GaugeBarLayout
{
    Wipe,
    Track,
}
