namespace Fluentometer.Logic.Density;

/// <summary>How much vertical space each gauge card occupies. Persisted as a
/// lowercase id (see <see cref="DensityCatalog.ToId"/>). Comfortable is the
/// pre-density baseline so existing users see no change until they opt in.</summary>
public enum GaugeDensity
{
    Comfortable,
    Compact,
    Mini,
}
