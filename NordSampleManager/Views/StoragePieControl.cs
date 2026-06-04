using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NordSampleManager.Views;

/// <summary>
/// Simple two-slice pie chart: used (orange-red) and free (dark gray).
/// Bind <see cref="UsedFraction"/> to a value in [0, 1].
/// </summary>
public sealed class StoragePieControl : Control
{
    public static readonly StyledProperty<double> UsedFractionProperty =
        AvaloniaProperty.Register<StoragePieControl, double>(nameof(UsedFraction));

    public double UsedFraction
    {
        get => GetValue(UsedFractionProperty);
        set => SetValue(UsedFractionProperty, value);
    }

    static StoragePieControl()
    {
        AffectsRender<StoragePieControl>(UsedFractionProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var r  = Math.Min(Bounds.Width, Bounds.Height) / 2.0 - 3;
        if (r <= 0) return;
        var cx = Bounds.Width  / 2.0;
        var cy = Bounds.Height / 2.0;

        var used = Math.Clamp(UsedFraction, 0.0, 1.0);

        IBrush usedBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x40, 0x20));
        IBrush freeBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));

        if (used >= 1.0) { ctx.DrawEllipse(usedBrush, null, new Point(cx, cy), r, r); return; }
        if (used <= 0.0) { ctx.DrawEllipse(freeBrush, null, new Point(cx, cy), r, r); return; }

        DrawSlice(ctx, cx, cy, r, -90,              used * 360,        usedBrush);
        DrawSlice(ctx, cx, cy, r, -90 + used * 360, (1 - used) * 360,  freeBrush);
    }

    private static void DrawSlice(DrawingContext ctx, double cx, double cy, double r,
        double startDeg, double sweepDeg, IBrush brush)
    {
        if (sweepDeg <= 0) return;
        var s  = startDeg        * Math.PI / 180.0;
        var e  = (startDeg + sweepDeg) * Math.PI / 180.0;
        var sx = cx + r * Math.Cos(s);
        var sy = cy + r * Math.Sin(s);
        var ex = cx + r * Math.Cos(e);
        var ey = cy + r * Math.Sin(e);

        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(new Point(cx, cy), isFilled: true);
            gc.LineTo(new Point(sx, sy));
            gc.ArcTo(new Point(ex, ey), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }
}
