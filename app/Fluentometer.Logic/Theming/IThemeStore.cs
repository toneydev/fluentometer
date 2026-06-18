namespace Fluentometer.Logic.Theming;

public interface IThemeStore
{
    string? LoadThemeId();
    void SaveThemeId(string id);
    GradientDirection LoadGradientDirection();
    void SaveGradientDirection(GradientDirection direction);
}
