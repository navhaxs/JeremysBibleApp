using System;

namespace MyBibleApp.Helpers;

/// <summary>
/// Pure math for tracking journal ink pan offset across device rotation:
/// classifying portrait/landscape from bounds, and clamping a remembered
/// offset to fit the new orientation's scroll range.
/// </summary>
public static class OrientationPanHelper
{
    public static bool IsPortrait(double width, double height) => height >= width;

    public static double ClampPanX(double stored, double extentWidth, double viewportWidth)
    {
        var maxX = Math.Max(0, extentWidth - viewportWidth);
        return Math.Clamp(stored, 0, maxX);
    }
}
