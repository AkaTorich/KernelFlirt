using System.Windows;
using System.Windows.Media;

namespace KernelFlirt.UI.Controls;

/// <summary>
/// Lightweight FrameworkElement that renders x64dbg/OllyDbg-style jump arrows
/// next to a disassembly view. Uses DrawingContext directly (no Shape visuals)
/// so hundreds of arrows cost nothing at scroll/zoom time. Arrows are assigned
/// to lanes so parallel jumps do not overlap.
/// </summary>
public sealed class JumpArrowsCanvas : FrameworkElement
{
    public enum ArrowKind { Normal, Rip, Taken, NotTaken }

    public readonly record struct JumpArrow(
        double SrcY,
        double? DstY,   // null if target is off-screen
        bool DownOffScreen,
        ArrowKind Kind);

    private readonly List<JumpArrow> _arrows = new();

    public void SetArrows(IEnumerable<JumpArrow> arrows)
    {
        _arrows.Clear();
        _arrows.AddRange(arrows);
        InvalidateVisual();
    }

    public void Clear()
    {
        if (_arrows.Count == 0) return;
        _arrows.Clear();
        InvalidateVisual();
    }

    // Pens are built per-render from the current theme resources so a theme
    // switch at runtime is picked up automatically.
    private static Brush ResBrush(string key, Color fallback)
    {
        var md = Application.Current.Resources.MergedDictionaries;
        foreach (var d in md)
            if (d.Contains(key) && d[key] is SolidColorBrush b) return b;
        if (Application.Current.Resources.Contains(key) &&
            Application.Current.Resources[key] is SolidColorBrush b2) return b2;
        return new SolidColorBrush(fallback);
    }

    private static Pen MakePen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 2 }, 0);
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0 || _arrows.Count == 0) return;

        var NormalPen   = MakePen(ResBrush("JumpArrowBrush",        Color.FromRgb(0x4C, 0xAF, 0x50)), 1.4, false);
        var TakenPen    = MakePen(ResBrush("JumpArrowTakenBrush",   Color.FromRgb(0xFF, 0x6B, 0x6B)), 1.6, false);
        var NotTakenPen = MakePen(ResBrush("JumpArrowNotTakenBrush",Color.FromRgb(0x80, 0x80, 0x80)), 1.2, true);
        var RipPen      = MakePen(ResBrush("JumpArrowRipBrush",     Colors.Yellow), 1.8, false);

        // Lane assignment: arrows that overlap vertically get different lanes.
        // Lane 0 is closest to the right edge (where the address column starts).
        var laned = AssignLanes(_arrows);
        int maxLane = 0;
        foreach (var (_, lane) in laned) if (lane > maxLane) maxLane = lane;

        const double laneStep = 6;
        const double rightEdge = 2;      // gap from the right border
        const double headSize  = 4;

        foreach (var (a, lane) in laned)
        {
            var pen = a.Kind switch
            {
                ArrowKind.Rip      => RipPen,
                ArrowKind.Taken    => TakenPen,
                ArrowKind.NotTaken => NotTakenPen,
                _ => NormalPen,
            };

            double xRight = w - rightEdge;                  // where it plugs into the row
            double xVert  = w - rightEdge - laneStep * (lane + 1);
            if (xVert < 1) xVert = 1;

            // RIP = horizontal arrow from left edge into the row
            if (a.Kind == ArrowKind.Rip)
            {
                double y = a.SrcY;
                dc.DrawLine(pen, new Point(0, y), new Point(xRight - headSize, y));
                DrawArrowHead(dc, pen.Brush, new Point(xRight, y), dir: 1, headSize);
                continue;
            }

            double ySrc = a.SrcY;
            if (a.DstY is double yDst)
            {
                // Full U-shape: src → vertical track → dst
                dc.DrawLine(pen, new Point(xRight, ySrc), new Point(xVert, ySrc));
                dc.DrawLine(pen, new Point(xVert, ySrc), new Point(xVert, yDst));
                dc.DrawLine(pen, new Point(xVert, yDst), new Point(xRight - headSize, yDst));
                DrawArrowHead(dc, pen.Brush, new Point(xRight, yDst), dir: 1, headSize);
            }
            else
            {
                // Off-screen — short stub in the direction of the jump
                bool down = a.DownOffScreen;
                double yEnd = down ? Math.Min(ySrc + 14, h - 2) : Math.Max(ySrc - 14, 2);
                dc.DrawLine(pen, new Point(xRight, ySrc), new Point(xVert, ySrc));
                dc.DrawLine(pen, new Point(xVert, ySrc), new Point(xVert, yEnd));
                DrawArrowHead(dc, pen.Brush, new Point(xVert, yEnd), dir: down ? 2 : 0, headSize);
            }
        }
    }

    /// <summary>Assigns each arrow the smallest lane index that doesn't collide.</summary>
    private static List<(JumpArrow Arrow, int Lane)> AssignLanes(List<JumpArrow> arrows)
    {
        // Sort by vertical span to give priority to longer arrows (inner lanes).
        var items = new List<(JumpArrow a, double y1, double y2)>(arrows.Count);
        foreach (var a in arrows)
        {
            double y1 = a.SrcY;
            double y2 = a.DstY ?? y1;
            if (y1 > y2) (y1, y2) = (y2, y1);
            items.Add((a, y1, y2));
        }
        items.Sort((x, y) => (y.y2 - y.y1).CompareTo(x.y2 - x.y1));

        var laneRanges = new List<List<(double y1, double y2)>>();
        var result = new List<(JumpArrow, int)>(arrows.Count);
        foreach (var (a, y1, y2) in items)
        {
            if (a.Kind == ArrowKind.Rip) { result.Add((a, 0)); continue; }
            int lane = 0;
            for (; lane < laneRanges.Count; lane++)
            {
                bool collides = false;
                foreach (var (ly1, ly2) in laneRanges[lane])
                    if (y1 <= ly2 + 1 && y2 >= ly1 - 1) { collides = true; break; }
                if (!collides) break;
            }
            if (lane == laneRanges.Count) laneRanges.Add(new());
            laneRanges[lane].Add((y1, y2));
            result.Add((a, lane));
        }
        return result;
    }

    /// <summary>dir: 0=up, 1=right, 2=down</summary>
    private static void DrawArrowHead(DrawingContext dc, Brush fill, Point tip, int dir, double s)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(tip, isFilled: true, isClosed: true);
            switch (dir)
            {
                case 0: // up
                    ctx.LineTo(new Point(tip.X - s, tip.Y + s), true, false);
                    ctx.LineTo(new Point(tip.X + s, tip.Y + s), true, false);
                    break;
                case 2: // down
                    ctx.LineTo(new Point(tip.X - s, tip.Y - s), true, false);
                    ctx.LineTo(new Point(tip.X + s, tip.Y - s), true, false);
                    break;
                default: // right
                    ctx.LineTo(new Point(tip.X - s, tip.Y - s), true, false);
                    ctx.LineTo(new Point(tip.X - s, tip.Y + s), true, false);
                    break;
            }
        }
        g.Freeze();
        dc.DrawGeometry(fill, null, g);
    }
}
