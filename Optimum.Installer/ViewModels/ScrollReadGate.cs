namespace Optimum.Installer.ViewModels;

/// <summary>
/// Decides whether a scrollable notice has been read to the end: either it fits
/// without scrolling, or the viewport is at the bottom. Pure so it is tested
/// directly rather than through a rendered ScrollViewer.
/// </summary>
public static class ScrollReadGate
{
    public static bool ReadToEnd(double extentHeight, double viewportHeight, double offsetY)
    {
        if (extentHeight <= 0)
            return false;
        bool fits = extentHeight <= viewportHeight + 1;
        bool atBottom = offsetY >= extentHeight - viewportHeight - 6;
        return fits || atBottom;
    }
}
