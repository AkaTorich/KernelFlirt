using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace GraphViewPlugin;

/// <summary>
/// Renders a CFG onto a WPF Canvas using MSAGL for layout computation.
/// MSAGL computes node/edge positions, we draw them as WPF shapes.
/// </summary>
public sealed class GraphRenderer
{
    private const double FontSize = 11;
    private const double NodePaddingX = 12;
    private const double NodePaddingY = 6;
    private const double LineHeight = 14;

    private static readonly Typeface MonoTypeface = new(
        new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Colors
    private static readonly Brush NodeBgBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly Brush NodeBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush NodeHeaderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly Brush AddrBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush MnemonicBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly Brush JmpMnemonicBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0));
    private static readonly Brush CallMnemonicBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    private static readonly Brush CommentBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x8B, 0x4E)); // green comment
    private static readonly Brush TrueEdgeBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)); // green
    private static readonly Brush FalseEdgeBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x6B, 0x6B)); // red
    private static readonly Brush UnconditionalEdgeBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x90, 0xD0)); // blue
    private static readonly Brush CurrentBlockBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x26));

    /// <summary>
    /// Clickable call target: screen rect → target address.
    /// </summary>
    public sealed class CallTargetHit
    {
        public Rect Rect { get; init; }
        public ulong TargetAddress { get; init; }
        public string Symbol { get; init; } = "";
    }

    /// <summary>
    /// Render the CFG blocks onto the given Canvas.
    /// Returns a map of block address → Rect for hit testing.
    /// callTargets is populated with clickable call operand regions.
    /// </summary>
    public Dictionary<ulong, Rect> Render(Canvas canvas, List<BasicBlock> blocks, bool is32Bit,
        ulong currentRip = 0,
        Dictionary<ulong, Color>? blockColors = null,
        HashSet<ulong>? collapsedBlocks = null,
        IReadOnlyDictionary<ulong, string>? annotations = null,
        List<CallTargetHit>? callTargets = null)
    {
        canvas.Children.Clear();
        var hitMap = new Dictionary<ulong, Rect>();

        if (blocks.Count == 0) return hitMap;

        // ── Step 1: Compute node sizes ──────────────────────────────────────
        var nodeSizes = new Dictionary<ulong, (double w, double h, int lines)>();
        foreach (var block in blocks)
        {
            bool collapsed = collapsedBlocks?.Contains(block.StartAddress) == true;

            if (collapsed)
            {
                // Collapsed: just header + summary line
                var headerText = $" {(is32Bit ? $"{block.StartAddress:X8}" : $"{block.StartAddress:X16}")}  [{block.Instructions.Count} instr]";
                double nodeW = MeasureText(headerText) + NodePaddingX * 2;
                double nodeH = LineHeight * 2 + NodePaddingY * 2;
                nodeSizes[block.StartAddress] = (Math.Max(nodeW, 120), nodeH, 2);
            }
            else
            {
                int lines = block.Instructions.Count + 1; // +1 for header
                double maxWidth = 0;
                foreach (var instr in block.Instructions)
                {
                    var displayText = $" {instr.AddressHex(is32Bit)}  {instr.Text}";
                    // Include annotation width if present
                    if (annotations != null && annotations.TryGetValue(instr.Address, out var note))
                        displayText += $"  ; {note}";
                    var text = displayText;
                    var width = MeasureText(text);
                    if (width > maxWidth) maxWidth = width;
                }

                var headerText = $" {(is32Bit ? $"{block.StartAddress:X8}" : $"{block.StartAddress:X16}")}";
                var headerW = MeasureText(headerText);
                if (headerW > maxWidth) maxWidth = headerW;

                double nodeW = maxWidth + NodePaddingX * 2;
                double nodeH = lines * LineHeight + NodePaddingY * 2;
                nodeSizes[block.StartAddress] = (nodeW, nodeH, lines);
            }
        }

        // ── Step 2: MSAGL layout ────────────────────────────────────────────
        var graph = new GeometryGraph();
        var msaglNodes = new Dictionary<ulong, Microsoft.Msagl.Core.Layout.Node>();

        foreach (var block in blocks)
        {
            var (w, h, _) = nodeSizes[block.StartAddress];
            var node = new Microsoft.Msagl.Core.Layout.Node(
                CurveFactory.CreateRectangle(w, h, new MsaglPoint(0, 0)),
                block.StartAddress.ToString());
            graph.Nodes.Add(node);
            msaglNodes[block.StartAddress] = node;
        }

        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Successors.Count; i++)
            {
                var targetAddr = block.Successors[i];
                if (msaglNodes.TryGetValue(block.StartAddress, out var srcNode) &&
                    msaglNodes.TryGetValue(targetAddr, out var tgtNode))
                {
                    var edge = new Edge(srcNode, tgtNode);
                    graph.Edges.Add(edge);
                }
            }
        }

        // Run Sugiyama layered layout
        var settings = new SugiyamaLayoutSettings
        {
            LayerSeparation = 40,
            NodeSeparation = 30,
            EdgeRoutingSettings = { EdgeRoutingMode = Microsoft.Msagl.Core.Routing.EdgeRoutingMode.Rectilinear }
        };
        var layout = new LayeredLayout(graph, settings);
        layout.Run();

        // ── Step 3: Flip Y axis (MSAGL Y grows up, WPF Y grows down) ───────
        // Simple transform: wpfX = msaglX - left + margin
        //                   wpfY = top - msaglY + margin  (flip)
        var bbox = graph.BoundingBox;
        double offsetX = -bbox.Left + 20;
        double flipY = bbox.Top;
        double offsetY = 20;

        // ── Step 4: Draw edges (behind nodes) ───────────────────────────────
        int edgeIdx = 0;
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Successors.Count; i++)
            {
                var edgeType = block.EdgeTypes[i];
                var brush = edgeType switch
                {
                    true => TrueEdgeBrush,
                    false => FalseEdgeBrush,
                    null => UnconditionalEdgeBrush
                };

                // Find the MSAGL edge
                var targetAddr = block.Successors[i];
                Edge? msaglEdge = null;
                foreach (var e in graph.Edges)
                {
                    if (e.Source.UserData.ToString() == block.StartAddress.ToString() &&
                        e.Target.UserData.ToString() == targetAddr.ToString())
                    {
                        msaglEdge = e;
                        break;
                    }
                }

                if (msaglEdge?.Curve != null)
                {
                    DrawEdge(canvas, msaglEdge, brush, offsetX, offsetY, flipY);
                }

                edgeIdx++;
            }
        }

        // ── Step 5: Draw nodes ──────────────────────────────────────────────
        foreach (var block in blocks)
        {
            var node = msaglNodes[block.StartAddress];
            var (w, h, _) = nodeSizes[block.StartAddress];

            double left = node.Center.X - w / 2 + offsetX;
            double top = (flipY - node.Center.Y) - h / 2 + offsetY;

            // Is this the block containing current RIP?
            bool isCurrent = currentRip != 0 && block.Instructions.Any(
                instr => instr.Address == currentRip);

            bool collapsed = collapsedBlocks?.Contains(block.StartAddress) == true;
            Color? customColor = blockColors?.TryGetValue(block.StartAddress, out var c) == true ? c : null;
            DrawNode(canvas, block, left, top, w, h, is32Bit, isCurrent, collapsed, customColor, annotations, callTargets);
            hitMap[block.StartAddress] = new Rect(left, top, w, h);
        }

        // Set canvas size
        double maxRight = 0, maxBottom = 0;
        foreach (var rect in hitMap.Values)
        {
            if (rect.Right > maxRight) maxRight = rect.Right;
            if (rect.Bottom > maxBottom) maxBottom = rect.Bottom;
        }
        canvas.Width = maxRight + 40;
        canvas.Height = maxBottom + 40;

        return hitMap;
    }

    private static void DrawNode(Canvas canvas, BasicBlock block, double left, double top,
        double width, double height, bool is32Bit, bool isCurrent,
        bool collapsed = false, Color? customColor = null,
        IReadOnlyDictionary<ulong, string>? annotations = null,
        List<CallTargetHit>? callTargets = null)
    {
        // Background color
        Brush bgFill;
        if (customColor.HasValue)
            bgFill = new SolidColorBrush(customColor.Value);
        else if (isCurrent)
            bgFill = CurrentBlockBrush;
        else
            bgFill = NodeBgBrush;

        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = bgFill,
            Stroke = isCurrent ? TrueEdgeBrush : NodeBorderBrush,
            StrokeThickness = isCurrent ? 2 : 1,
            RadiusX = 3,
            RadiusY = 3
        };
        Canvas.SetLeft(bg, left);
        Canvas.SetTop(bg, top);
        canvas.Children.Add(bg);

        // Header bar
        var header = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = LineHeight + 2,
            Fill = NodeHeaderBrush,
            RadiusX = 3,
            RadiusY = 3
        };
        Canvas.SetLeft(header, left);
        Canvas.SetTop(header, top);
        canvas.Children.Add(header);

        // Header text
        var headerAddr = is32Bit ? $"{block.StartAddress:X8}" : $"{block.StartAddress:X16}";
        if (collapsed)
        {
            AddText(canvas, $"{headerAddr}  [{block.Instructions.Count} instr]",
                left + NodePaddingX, top + 1, TextBrush, FontWeights.Bold);

            // Collapsed indicator
            var lastInstr = block.Instructions[^1];
            var summary = $"... {lastInstr.Text}";
            AddText(canvas, summary, left + NodePaddingX, top + LineHeight + NodePaddingY, AddrBrush);
        }
        else
        {
            AddText(canvas, headerAddr, left + NodePaddingX, top + 1, TextBrush, FontWeights.Bold);

            // Instructions
            double y = top + LineHeight + NodePaddingY;
            foreach (var instr in block.Instructions)
            {
                double x = left + NodePaddingX;
                var addrText = is32Bit ? $"{instr.Address:X8}" : $"{instr.Address:X16}";
                var addrW = AddText(canvas, addrText, x, y, AddrBrush);
                x += addrW + 8;

                Brush mnBrush;
                if (instr.IsCall) mnBrush = CallMnemonicBrush;
                else if (instr.IsBranch || instr.IsRet) mnBrush = JmpMnemonicBrush;
                else mnBrush = MnemonicBrush;

                var mnW = AddText(canvas, instr.Mnemonic, x, y, mnBrush);
                x += mnW + 6;

                if (!string.IsNullOrEmpty(instr.DisplayOperands))
                {
                    bool isClickable = instr.IsCall && instr.ResolvedSymbol != null && instr.BranchTarget != 0;
                    var opBrush = instr.ResolvedSymbol != null ? CallMnemonicBrush : TextBrush;
                    var opW = AddText(canvas, instr.DisplayOperands, x, y, opBrush,
                        underline: isClickable);

                    // Register clickable area for call targets
                    if (isClickable && callTargets != null)
                    {
                        callTargets.Add(new CallTargetHit
                        {
                            Rect = new Rect(x, y, opW, LineHeight),
                            TargetAddress = instr.BranchTarget,
                            Symbol = instr.ResolvedSymbol!
                        });
                    }

                    x += opW + 6;
                }

                // User annotation from Bookmarks plugin (green comment)
                if (annotations != null && annotations.TryGetValue(instr.Address, out var note))
                    AddText(canvas, $"; {note}", x, y, CommentBrush);

                y += LineHeight;
            }
        }
    }

    /// <summary>Convert MSAGL point to WPF point (flip Y axis).</summary>
    private static System.Windows.Point ToWpf(MsaglPoint p, double offsetX, double offsetY, double flipY)
        => new(p.X + offsetX, (flipY - p.Y) + offsetY);

    private static void DrawEdge(Canvas canvas, Edge edge, Brush brush,
        double offsetX, double offsetY, double flipY)
    {
        var curve = edge.Curve;
        if (curve == null) return;

        var path = new System.Windows.Shapes.Path
        {
            Stroke = brush,
            StrokeThickness = 1.5,
            Fill = null
        };

        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = ToWpf(curve.Start, offsetX, offsetY, flipY),
            IsClosed = false
        };

        if (curve is Curve compositeCurve)
        {
            foreach (var seg in compositeCurve.Segments)
            {
                if (seg is CubicBezierSegment bezier)
                {
                    figure.Segments.Add(new BezierSegment(
                        ToWpf(bezier.B(1), offsetX, offsetY, flipY),
                        ToWpf(bezier.B(2), offsetX, offsetY, flipY),
                        ToWpf(bezier.B(3), offsetX, offsetY, flipY),
                        true));
                }
                else if (seg is Microsoft.Msagl.Core.Geometry.Curves.LineSegment lineSeg)
                {
                    figure.Segments.Add(new System.Windows.Media.LineSegment(
                        ToWpf(lineSeg.End, offsetX, offsetY, flipY), true));
                }
                else
                {
                    for (double t = 0.1; t <= 1.0; t += 0.1)
                    {
                        var pt = seg[seg.ParStart + t * (seg.ParEnd - seg.ParStart)];
                        figure.Segments.Add(new System.Windows.Media.LineSegment(
                            ToWpf(pt, offsetX, offsetY, flipY), true));
                    }
                }
            }
        }
        else
        {
            for (double t = 0.1; t <= 1.0; t += 0.1)
            {
                var pt = curve[curve.ParStart + t * (curve.ParEnd - curve.ParStart)];
                figure.Segments.Add(new System.Windows.Media.LineSegment(
                    ToWpf(pt, offsetX, offsetY, flipY), true));
            }
        }

        geometry.Figures.Add(figure);
        path.Data = geometry;
        canvas.Children.Add(path);

        // Arrowhead at the end
        var endPt = ToWpf(curve.End, offsetX, offsetY, flipY);
        // Get direction by sampling near the end
        var nearEnd = curve[curve.ParEnd - (curve.ParEnd - curve.ParStart) * 0.02];
        var nearEndWpf = ToWpf(nearEnd, offsetX, offsetY, flipY);
        var dirX = endPt.X - nearEndWpf.X;
        var dirY = endPt.Y - nearEndWpf.Y;
        var len = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len > 0.1)
        {
            dirX /= len; dirY /= len;
            double perpX = -dirY, perpY = dirX;
            double arrowLen = 8, arrowW = 4;

            var polygon = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    endPt,
                    new System.Windows.Point(endPt.X - dirX * arrowLen + perpX * arrowW,
                                             endPt.Y - dirY * arrowLen + perpY * arrowW),
                    new System.Windows.Point(endPt.X - dirX * arrowLen - perpX * arrowW,
                                             endPt.Y - dirY * arrowLen - perpY * arrowW)
                }
            };
            canvas.Children.Add(polygon);
        }
    }

    private static double AddText(Canvas canvas, string text, double x, double y,
        Brush brush, FontWeight? weight = null, bool underline = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = FontSize,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            Cursor = underline ? System.Windows.Input.Cursors.Hand : null
        };
        if (underline)
            tb.TextDecorations = TextDecorations.Underline;
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);

        return MeasureText(text);
    }

    private static double MeasureText(string text)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            FontSize,
            Brushes.White,
            1.0);
        return ft.Width;
    }
}
