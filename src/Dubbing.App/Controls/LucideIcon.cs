using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Dubbing.App.Controls;

public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<LucideIcon, string>(nameof(Kind), "play");
    public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<LucideIcon, IBrush>(nameof(Stroke), Brushes.LightSteelBlue);
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private static readonly Dictionary<string, XElement> Icons = [];
    static LucideIcon() => AffectsRender<LucideIcon>(KindProperty, StrokeProperty);
    public LucideIcon() { Width = 19; Height = 19; }
    public override void Render(DrawingContext context)
    {
        if (!Icons.TryGetValue(Kind, out var root))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://OpenDub/Assets/Icons/{Kind}.svg"));
            Icons[Kind] = root = XElement.Load(stream);
        }
        using var transform = context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24));
        var pen = new Pen(Stroke, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var element in root.Elements())
        {
            double N(string key) => double.Parse(element.Attribute(key)?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            switch (element.Name.LocalName)
            {
                case "path": context.DrawGeometry(null, pen, Geometry.Parse(element.Attribute("d")!.Value)); break;
                case "circle": context.DrawEllipse(null, pen, new Point(N("cx"), N("cy")), N("r"), N("r")); break;
                case "rect": context.DrawRectangle(null, pen, new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), N("rx")); break;
                case "line": context.DrawLine(pen, new(N("x1"), N("y1")), new(N("x2"), N("y2"))); break;
                case "polyline": case "polygon":
                    string points = element.Attribute("points")!.Value;
                    context.DrawGeometry(null, pen, Geometry.Parse("M " + points + (element.Name.LocalName == "polygon" ? " Z" : ""))); break;
            }
        }
    }
}
