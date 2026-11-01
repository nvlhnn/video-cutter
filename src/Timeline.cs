// Video Cutter - lightweight custom-drawn timeline (one OnRender pass, no per-tick elements).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoCutter
{
    public class Timeline : FrameworkElement
    {
        public double Duration, Position, In, Out;
        public double Fade;                       // crossfade-loop zone at the end of the selection (0 = none)
        public double[] Keyframes;
        public bool Enabled;

        public event Action<double> Scrub;        // playhead dragged
        public event Action<double> InMoved;
        public event Action<double> OutMoved;
        public event Action<int> Wheel;           // +1 / -1
        public event Action DragStarted, DragEnded;

        enum Drag { None, Head, In, Out }
        Drag drag = Drag.None;

        const double Pad = 14, TrackTop = 24, TrackH = 30;
        static readonly Typeface Face = new Typeface("Segoe UI");
        static readonly Brush TrackBg = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1B)));
        static readonly Brush SelBg = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x4F, 0x8C, 0xFF)));
        static readonly Brush Tick = Freeze(new SolidColorBrush(Color.FromRgb(0x5D, 0x64, 0x75)));
        static readonly Brush Label = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0xA3)));
        static readonly Brush KeyBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xE0, 0xB0, 0x50)));
        static readonly Brush InBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0x8B)));
        static readonly Brush OutBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
        static readonly Brush FadeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x4F, 0xD1, 0x8B)));
        static readonly Pen TickPen = FreezePen(new Pen(Tick, 1));
        static readonly Pen KeyPen = FreezePen(new Pen(KeyBrush, 1));
        static readonly Pen HeadPen = FreezePen(new Pen(Brushes.White, 2));
        static readonly double[] Steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };

        static Brush Freeze(Brush b) { b.Freeze(); return b; }
        static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        public Timeline()
        {
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
        }

        double W { get { return Math.Max(1, ActualWidth - 2 * Pad); } }
        double X(double t) { return Pad + (Duration > 0 ? t / Duration : 0) * W; }
        double T(double x) { return Math.Max(0, Math.Min(Duration, (x - Pad) / W * Duration)); }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            var track = new Rect(Pad, TrackTop, W, TrackH);
            dc.DrawRoundedRectangle(TrackBg, null, track, 4, 4);
            if (!Enabled || Duration <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Ruler
            double step = Steps[Steps.Length - 1];
            foreach (var s in Steps) if (s / Duration * W >= 90) { step = s; break; }
            for (double t = 0; t <= Duration + 1e-9; t += step)
            {
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(TickPen, new Point(x, TrackTop - 6), new Point(x, TrackTop - 1));
                var ft = new FormattedText(RulerText(t, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Label, dpi);
                double tx = Math.Min(Math.Max(0, x - ft.Width / 2), ActualWidth - ft.Width);
                dc.DrawText(ft, new Point(tx, 2));
            }

            // Keyframes (only when they're far enough apart to be useful)
            if (Keyframes != null && Keyframes.Length > 0 && W / Keyframes.Length >= 3)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                    foreach (var k in Keyframes)
                    {
                        double x = Math.Round(X(k)) + 0.5;
                        c.BeginFigure(new Point(x, TrackTop + TrackH - 7), false, false);
                        c.LineTo(new Point(x, TrackTop + TrackH), true, false);
                    }
                g.Freeze();
                dc.DrawGeometry(null, KeyPen, g);
            }

            // Selection
            double xi = X(In), xo = X(Out);
            dc.DrawRectangle(SelBg, null, new Rect(xi, TrackTop, Math.Max(0, xo - xi), TrackH));
            if (Fade > 0)
            {   // crossfade zone: a ramp that rises toward END (the start fades in over it)
                double xf = X(Math.Max(In, Out - Fade));
                var ramp = new StreamGeometry();
                using (var c = ramp.Open())
                {
                    c.BeginFigure(new Point(xf, TrackTop + TrackH), true, true);
                    c.LineTo(new Point(xo, TrackTop), true, false);
                    c.LineTo(new Point(xo, TrackTop + TrackH), true, false);
                }
                ramp.Freeze();
                dc.DrawGeometry(FadeBrush, null, ramp);
            }
            DrawHandle(dc, xi, InBrush, true);
            DrawHandle(dc, xo, OutBrush, false);

            // Playhead
            double xp = Math.Round(X(Position)) + 0.5;
            dc.DrawLine(HeadPen, new Point(xp, TrackTop - 4), new Point(xp, TrackTop + TrackH + 4));
            var tri = new StreamGeometry();
            using (var c = tri.Open())
            {
                c.BeginFigure(new Point(xp - 6, TrackTop - 10), true, true);
                c.LineTo(new Point(xp + 6, TrackTop - 10), true, false);
                c.LineTo(new Point(xp, TrackTop - 3), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(Brushes.White, null, tri);
        }

        static void DrawHandle(DrawingContext dc, double x, Brush b, bool left)
        {
            dc.DrawRectangle(b, null, new Rect(left ? x - 3 : x, TrackTop - 2, 3, TrackH + 4));
            dc.DrawRectangle(b, null, new Rect(left ? x : x - 9, TrackTop + TrackH + 2, 9, 6));
        }

        static string RulerText(double t, double step)
        {
            int s = (int)Math.Floor(t + 1e-9);
            string r = s >= 3600 ? string.Format("{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60)
                                 : string.Format("{0}:{1:00}", s / 60, s % 60);
            if (step < 1) r += "." + ((int)Math.Round((t - s) * 10) % 10);
            return r;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!Enabled) return;
            double x = e.GetPosition(this).X;
            if (Math.Abs(x - X(In)) <= 8 && Math.Abs(x - X(In)) <= Math.Abs(x - X(Out))) drag = Drag.In;
            else if (Math.Abs(x - X(Out)) <= 8) drag = Drag.Out;
            else drag = Drag.Head;
            CaptureMouse();
            if (DragStarted != null) DragStarted();
            Apply(x);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            double x = e.GetPosition(this).X;
            if (drag != Drag.None) { Apply(x); return; }
            if (!Enabled) return;
            bool near = Math.Abs(x - X(In)) <= 8 || Math.Abs(x - X(Out)) <= 8;
            Cursor = near ? Cursors.SizeWE : Cursors.Hand;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (drag == Drag.None) return;
            drag = Drag.None;
            ReleaseMouseCapture();
            if (DragEnded != null) DragEnded();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (Enabled && Wheel != null) Wheel(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        void Apply(double x)
        {
            double t = T(x);
            if (drag == Drag.Head && Scrub != null) Scrub(t);
            else if (drag == Drag.In && InMoved != null) InMoved(t);
            else if (drag == Drag.Out && OutMoved != null) OutMoved(t);
        }
    }
}
