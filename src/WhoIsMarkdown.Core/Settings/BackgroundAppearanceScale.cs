namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Converts between the user-facing background visibility percentage and the
/// WPF opacity stored in application settings.
/// </summary>
/// <remarks>
/// Bug fix: the original UI exposed a percentage but inverted it as
/// <c>1 - percentage</c>, so moving the slider toward 100% hid the image. Keeping
/// this mapping in Core gives every UI path one explicit 0%-hidden/100%-visible
/// contract and makes the boundary behavior independently testable.
/// </remarks>
public static class BackgroundAppearanceScale
{
    public static double FromPercentage(double percentage)
    {
        return Math.Clamp(percentage, 0, 100) / 100;
    }

    public static double ToPercentage(double opacity)
    {
        return Math.Clamp(opacity, 0, 1) * 100;
    }
}
