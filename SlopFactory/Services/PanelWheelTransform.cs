using SlopFactory.Models;

namespace SlopFactory.Services;

public static class PanelWheelTransform
{
    public static bool Apply(PanelLayer layer, double deltaX, double deltaY, double deltaMode, bool rotate)
    {
        if (!layer.Visible || layer.Matrix is not { Length: 6 }) return false;
        // Shift+wheel may be reported on the horizontal axis by the browser.
        var delta = deltaY != 0 ? deltaY : deltaX;
        if (!double.IsFinite(delta) || delta == 0) return false;
        var pixels = delta * (deltaMode == 1 ? 16 : deltaMode == 2 ? 800 : 1);
        var matrix = layer.Matrix;
        var scale = Math.Sqrt(matrix[0] * matrix[0] + matrix[1] * matrix[1]);
        var angle = Math.Atan2(matrix[1], matrix[0]);
        if (rotate)
            angle -= pixels * Math.PI / 2400; // 15 degrees per 200 pixels.
        else
            scale = Math.Clamp(scale * Math.Exp(Math.Clamp(-pixels * .002, -20, 20)), .01, 10);
        var cosine = Math.Cos(angle) * scale;
        var sine = Math.Sin(angle) * scale;
        layer.Matrix = [cosine, sine, -sine, cosine, matrix[4], matrix[5]];
        return true;
    }
}
