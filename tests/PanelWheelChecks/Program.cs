using SlopFactory.Models;
using SlopFactory.Services;

static void Near(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 1e-8)
        throw new Exception($"Expected {expected}, got {actual}");
}
static double Scale(PanelLayer layer) => Math.Sqrt(layer.Matrix[0] * layer.Matrix[0] + layer.Matrix[1] * layer.Matrix[1]);
var layer = new PanelLayer { Matrix = [1, 0, 0, 1, 23, -17] };
for (var i = 0; i < 100; i++) PanelWheelTransform.Apply(layer, 0, -1, 0, false);
Near(Math.Exp(.2), Scale(layer));
PanelWheelTransform.Apply(layer, 0, 100, 0, false);
Near(1, Scale(layer));
Near(23, layer.Matrix[4]);
Near(-17, layer.Matrix[5]);

PanelWheelTransform.Apply(layer, 200, 0, 0, true);
Near(-Math.PI / 12, Math.Atan2(layer.Matrix[1], layer.Matrix[0]));
Near(1, Scale(layer));

var pixels = new PanelLayer();
var lines = new PanelLayer();
PanelWheelTransform.Apply(pixels, 0, -48, 0, false);
PanelWheelTransform.Apply(lines, 0, -3, 1, false);
Near(Scale(pixels), Scale(lines));
var pages = new PanelLayer();
PanelWheelTransform.Apply(pages, 0, -1, 2, false);
Near(Math.Exp(1.6), Scale(pages));

PanelWheelTransform.Apply(layer, 0, -100000, 0, false);
Near(10, Scale(layer));
PanelWheelTransform.Apply(layer, 0, 120, 0, false);
if (Scale(layer) >= 10) throw new Exception("Reversing from the upper limit must respond");
PanelWheelTransform.Apply(layer, 0, 100000, 0, false);
Near(.01, Scale(layer));
PanelWheelTransform.Apply(layer, 0, -120, 0, false);
if (Scale(layer) <= .01) throw new Exception("Reversing from the lower limit must respond");
if (PanelWheelTransform.Apply(layer, 0, double.NaN, 0, false)) throw new Exception("Invalid input accepted");
layer.Visible = false;
if (PanelWheelTransform.Apply(layer, 0, 100, 0, false)) throw new Exception("Hidden layer changed");
Console.WriteLine("PASS: rapid input, reverse scaling, horizontal rotation, units, translation, limits, invalid input and hidden layers.");
