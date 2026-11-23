// The fixed-format layout drawn behind the text:
//   a light band over columns 1-6 (sequence) and 73+ (identification),
//   a line where area A starts (column 8), a dotted line at area B (column 12),
//   and a firmer line after column 72, where code must end.
// Free-format files get none of this.
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CobolDotNet.Editor.Core;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CobolDotNet.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(CobolContentType.Name)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ColumnGuidesFactory : IWpfTextViewCreationListener
    {
        public const string LayerName = "CobolColumnGuides";

        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        internal AdornmentLayerDefinition Layer = null;

        public void TextViewCreated(IWpfTextView view) => new ColumnGuides(view);
    }

    internal sealed class ColumnGuides
    {
        readonly IWpfTextView view;
        readonly IAdornmentLayer layer;

        static readonly Brush Band = Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0x80, 0x80, 0x80)));
        static readonly Brush Guide = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)));
        static readonly Brush Edge = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0xC0, 0x60, 0x60)));

        static Brush Frozen(Brush b) { b.Freeze(); return b; }

        public ColumnGuides(IWpfTextView view)
        {
            this.view = view;
            layer = view.GetAdornmentLayer(ColumnGuidesFactory.LayerName);
            view.LayoutChanged += (s, e) => Draw();
            view.ViewportWidthChanged += (s, e) => Draw();
            view.ViewportHeightChanged += (s, e) => Draw();
        }

        void Draw()
        {
            layer.RemoveAllAdornments();
            if (view.IsClosed || !CobolBuffer.For(view.TextBuffer).IsFixed(view.TextSnapshot)) return;

            double w = view.FormattedLineSource.ColumnWidth;
            double left = view.TextViewLines.Count > 0 ? view.TextViewLines.FirstVisibleLine.TextLeft : 0;
            double top = view.ViewportTop, height = view.ViewportHeight;
            double right = view.ViewportRight;

            double X(int column0) => left + column0 * w;

            AddRect(X(0), X(6), top, height);                                  // sequence area
            AddRect(X(CobolScanner.CodeEnd), right, top, height);              // identification area
            AddLine(X(CobolScanner.AreaA), top, height, Guide, null);          // area A, column 8
            AddLine(X(CobolScanner.AreaB), top, height, Guide, new DoubleCollection { 2, 3 }); // area B, column 12
            AddLine(X(CobolScanner.CodeEnd), top, height, Edge, null);         // after column 72
        }

        void AddRect(double x1, double x2, double top, double height)
        {
            if (x2 <= x1) return;
            var r = new Rectangle { Width = x2 - x1, Height = height, Fill = Band, IsHitTestVisible = false };
            Canvas.SetLeft(r, x1);
            Canvas.SetTop(r, top);
            layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, r, null);
        }

        void AddLine(double x, double top, double height, Brush brush, DoubleCollection dashes)
        {
            x = System.Math.Floor(x) + 0.5;   // crisp one-pixel line
            var l = new Line
            {
                X1 = x, X2 = x, Y1 = top, Y2 = top + height,
                Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            if (dashes != null) l.StrokeDashArray = dashes;
            layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, l, null);
        }
    }
}
