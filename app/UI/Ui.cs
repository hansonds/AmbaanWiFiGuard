using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D = System.Drawing;

namespace Ambaan
{
    public static class Ui
    {
        static readonly Dictionary<string, SolidColorBrush> cache = new Dictionary<string, SolidColorBrush>();
        public static SolidColorBrush B(string hex)
        {
            SolidColorBrush b;
            if (!cache.TryGetValue(hex, out b)) { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); cache[hex] = b; }
            return b;
        }
        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static T Res<T>(string key) { return (T)Application.Current.FindResource(key); }

        static ImageSource icon;
        public static ImageSource AppIcon
        {
            get
            {
                if (icon == null)
                {
                    try { using (var s = Assets.Open("assets/ambaan.ico")) { var d = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); icon = d.Frames.OrderByDescending(f => f.PixelWidth).First(); } }
                    catch { }
                }
                return icon;
            }
        }

        public static TextBlock Text(string t, double size = 12.5, string color = null, bool bold = false, bool wrap = false)
        {
            var tb = new TextBlock { Text = t, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
            if (color != null) tb.Foreground = B(color);
            if (bold) tb.FontWeight = FontWeights.SemiBold;
            if (wrap) { tb.TextWrapping = TextWrapping.Wrap; tb.TextTrimming = TextTrimming.None; }
            return tb;
        }

        public static TextBlock Label(string t) { var x = new TextBlock { Text = t.ToUpperInvariant(), Style = Res<Style>("Label"), Margin = new Thickness(0, 0, 0, 4) }; return x; }

        public static Button Btn(string text, string tint, Action click, double width = double.NaN)
        {
            var b = new Button { Content = text, Style = Res<Style>("Pill"), Background = B(tint), Width = width };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { Log.Error("button " + text, ex); } };
            return b;
        }

        public static Border Card(UIElement child, double top = 0)
        {
            return new Border { Style = Res<Style>("Card"), Child = child, Margin = new Thickness(0, top, 0, 0) };
        }

        public static TextBox Input(string text, double width = double.NaN)
        {
            return new TextBox
            {
                Text = text, Width = width, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Background = B("#22FFFFFF"),
                Foreground = B("#F7F5FF"), BorderBrush = B("#44FFFFFF"), CaretBrush = B("#FFFFFF"), VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        public static ComboBox Combo(IEnumerable<string> items, int selected, double width = 170)
        {
            var c = new ComboBox { Width = width, FontSize = 12.5, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = Math.Max(0, Math.Min(c.Items.Count - 1, selected));
            return c;
        }

        public static CheckBox Check(string text, bool on)
        {
            return new CheckBox { Content = text, IsChecked = on, Foreground = B("#F7F5FF"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        }

        public static Grid Row(UIElement left, UIElement right, double top = 8)
        {
            var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left is FrameworkElement fl) fl.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(left);
            Grid.SetColumn(right, 1); g.Children.Add(right);
            return g;
        }

        public static UniformGrid Buttons(params Button[] bs)
        {
            var g = new UniformGrid { Columns = bs.Length, Margin = new Thickness(0, 14, 0, 0) };
            for (int i = 0; i < bs.Length; i++) { bs[i].Margin = new Thickness(i == 0 ? 0 : 4, 0, i == bs.Length - 1 ? 0 : 4, 0); g.Children.Add(bs[i]); }
            return g;
        }

        /// <summary>A glassy dialog: rounded, see-through, draggable by its title.</summary>
        public static Window Dialog(string title, double width, out StackPanel body, bool topmost = true)
        {
            var w = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = topmost, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true, FontFamily = new FontFamily("Segoe UI"), Foreground = B("#F7F5FF"), UseLayoutRounding = true
            };
            w.Icon = AppIcon;
            var shell = new Border { Style = Res<Style>("GlassShell"), Background = Res<Brush>("GlassFillSolid"), Margin = new Thickness(14) };
            shell.Effect = new DropShadowEffect { BlurRadius = 22, Opacity = 0.5, ShadowDepth = 3, Color = Colors.Black };
            var root = new StackPanel { Margin = new Thickness(20, 12, 20, 20) };
            var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, -8, 8) };
            var close = new Button { Content = "", Style = Res<Style>("IconBtn"), ToolTip = "Close" };
            close.Click += (s, e) => w.Close();
            DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
            var logo = new Image { Source = AppIcon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
            head.Children.Add(logo);
            head.Children.Add(Text(title, 15, null, true));
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            body = new StackPanel();
            root.Children.Add(head); root.Children.Add(body);
            shell.Child = root;
            w.Content = shell;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            return w;
        }

        public static void Info(string title, string text)
        {
            StackPanel body;
            var w = Dialog(title, 420, out body);
            body.Children.Add(Text(text, 13, null, false, true));
            body.Children.Add(Buttons(Btn("OK", "#605B4BC4", () => w.Close())));
            w.ShowDialog();
        }

        public static bool Confirm(string title, string text, string yes = "Yes")
        {
            StackPanel body; bool ok = false;
            var w = Dialog(title, 420, out body);
            body.Children.Add(Text(text, 13, null, false, true));
            body.Children.Add(Buttons(Btn(yes, "#605B4BC4", () => { ok = true; w.Close(); }), Btn("Cancel", "#303A3270", () => w.Close())));
            w.ShowDialog();
            return ok;
        }

        public static void Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open " + target, ex); }
        }

        /// <summary>Screen work area (taskbar excluded) in WPF units, for the screen that holds the given point.</summary>
        public static Rect WorkArea(Visual v, Point dip)
        {
            var src = PresentationSource.FromVisual(v);
            Matrix toDev = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            Matrix fromDev = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var px = toDev.Transform(dip);
            var scr = System.Windows.Forms.Screen.FromPoint(new D.Point((int)px.X, (int)px.Y)).WorkingArea;
            var tl = fromDev.Transform(new Point(scr.Left, scr.Top));
            var br = fromDev.Transform(new Point(scr.Right, scr.Bottom));
            return new Rect(tl, br);
        }
    }

    /// <summary>Real blur behind the see-through panels (Windows 10 and 11). Optional: Settings > Glass effect.</summary>
    public static class Glass
    {
        public static void Apply(Window w, bool on)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                var ap = new Native.AccentPolicy { AccentState = on ? 3 : 0 };   // 3 = blur behind
                int size = Marshal.SizeOf(ap);
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(ap, p, false);
                    var d = new Native.WCAData { Attribute = 19, Data = p, SizeOfData = size };
                    Native.SetWindowCompositionAttribute(h, ref d);
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch (Exception ex) { Log.Error("glass", ex); }
        }

        /// <summary>Clip the window (and its blur) to rounded corners.</summary>
        public static void Round(Window w, double radius, bool on)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                if (!on) { Native.SetWindowRgn(h, IntPtr.Zero, true); return; }
                var src = PresentationSource.FromVisual(w);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                int pw = (int)Math.Ceiling(w.ActualWidth * sx), ph = (int)Math.Ceiling(w.ActualHeight * sy);
                int r = (int)(radius * 2 * sx);
                var rgn = Native.CreateRoundRectRgn(0, 0, pw + 1, ph + 1, r, r);
                Native.SetWindowRgn(h, rgn, true);   // Windows owns the region now
            }
            catch (Exception ex) { Log.Error("round", ex); }
        }

        public static void ToolWindow(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int ex = Native.GetWindowLong(h, -20);
                Native.SetWindowLong(h, -20, ex | 0x80);   // WS_EX_TOOLWINDOW: not in Alt+Tab
            }
            catch { }
        }
    }

    /// <summary>Animated GIF frames (GDI+ decodes and composes them), turned into WPF images once.</summary>
    public class GifFrames
    {
        public BitmapSource[] Frames;
        public int[] Delays;
        public int Total;
        static readonly Dictionary<string, GifFrames> cache = new Dictionary<string, GifFrames>();

        public static GifFrames Load(string path)
        {
            if (path == null) return null;
            GifFrames g;
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (cache.TryGetValue(key, out g)) return g;
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var img = D.Image.FromStream(ms))
                {
                    var fd = new D.Imaging.FrameDimension(img.FrameDimensionsList[0]);
                    int n = img.GetFrameCount(fd);
                    var delays = new int[n];
                    try
                    {
                        var raw = img.GetPropertyItem(0x5100).Value;
                        for (int i = 0; i < n; i++) { int v = BitConverter.ToInt32(raw, i * 4) * 10; delays[i] = v <= 0 ? 100 : v; }
                    }
                    catch { for (int i = 0; i < n; i++) delays[i] = 100; }
                    var frames = new BitmapSource[n];
                    for (int i = 0; i < n; i++)
                    {
                        img.SelectActiveFrame(fd, i);
                        using (var bmp = new D.Bitmap(img.Width, img.Height, D.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (var gr = D.Graphics.FromImage(bmp)) { gr.Clear(D.Color.Transparent); gr.DrawImage(img, 0, 0, img.Width, img.Height); }
                            var data = bmp.LockBits(new D.Rectangle(0, 0, bmp.Width, bmp.Height), D.Imaging.ImageLockMode.ReadOnly, D.Imaging.PixelFormat.Format32bppArgb);
                            try
                            {
                                var bs = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
                                bs.Freeze(); frames[i] = bs;
                            }
                            finally { bmp.UnlockBits(data); }
                        }
                    }
                    g = new GifFrames { Frames = frames, Delays = delays, Total = delays.Sum() };
                }
                cache[key] = g;
                return g;
            }
            catch (Exception ex) { Log.Error("gif " + path, ex); return null; }
        }

        public BitmapSource At(long ms)
        {
            if (Frames.Length <= 1 || Total <= 0) return Frames[0];
            long t = ms % Total; long acc = 0;
            for (int i = 0; i < Frames.Length; i++) { acc += Delays[i]; if (t < acc) return Frames[i]; }
            return Frames[Frames.Length - 1];
        }
    }

    /// <summary>Line / bar chart drawn by hand: light, crisp at any scale.</summary>
    public class ChartView : FrameworkElement
    {
        public class Series { public double?[] Values; public Color Color; public bool Fill; public string Name; }
        public List<Series> Data = new List<Series>();
        public string[] XLabels;
        public bool Bars, ShowAxis = true, Dots;
        public double MinMax = 1;
        public string Empty = "No data yet";
        public string Unit = "";

        public void Set(params Series[] s) { Data = s.ToList(); InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var tf = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double l = ShowAxis ? 34 : 0, r = 4, t = 6, b = (ShowAxis && XLabels != null) ? 18 : 4;
            double cw = w - l - r, ch = h - t - b;
            var vals = Data.SelectMany(s => s.Values ?? new double?[0]).Where(v => v != null).Select(v => v.Value).ToList();
            double max = Math.Max(MinMax, vals.Count > 0 ? vals.Max() : 0) * 1.15;
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1);
            var muted = new SolidColorBrush(Color.FromArgb(170, 205, 200, 240));
            if (ShowAxis)
            {
                for (int i = 0; i <= 3; i++)
                {
                    double y = t + ch * i / 3.0;
                    dc.DrawLine(grid, new Point(l, y), new Point(w - r, y));
                    var ft = new FormattedText((max - max * i / 3.0).ToString(max < 10 ? "0.0" : "0"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 9.5, muted, dpi);
                    dc.DrawText(ft, new Point(l - ft.Width - 5, y - ft.Height / 2));
                }
                if (XLabels != null && XLabels.Length > 0)
                {
                    int n = XLabels.Length;
                    int step = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, cw / 60)));
                    for (int i = 0; i < n; i += step)
                    {
                        double x = Bars ? l + cw * (i + 0.5) / n : l + (n < 2 ? cw / 2 : cw * i / (n - 1));
                        var ft = new FormattedText(XLabels[i] ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 9.5, muted, dpi);
                        dc.DrawText(ft, new Point(Math.Max(l, Math.Min(w - ft.Width, x - ft.Width / 2)), h - ft.Height));
                    }
                }
            }
            if (vals.Count == 0)
            {
                var ft = new FormattedText(Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, muted, dpi);
                dc.DrawText(ft, new Point(l + (cw - ft.Width) / 2, t + (ch - ft.Height) / 2));
                return;
            }
            int sIndex = 0;
            foreach (var s in Data)
            {
                if (s.Values == null || s.Values.Length == 0) continue;
                int n = s.Values.Length;
                var brush = new SolidColorBrush(s.Color);
                if (Bars)
                {
                    int count = Data.Count;
                    double slot = cw / n, bw = Math.Max(2, slot * 0.7 / count);
                    for (int i = 0; i < n; i++)
                    {
                        if (s.Values[i] == null) continue;
                        double bh = ch * s.Values[i].Value / max;
                        double x = l + slot * i + (slot - bw * count) / 2 + bw * sIndex;
                        dc.DrawRoundedRectangle(brush, null, new Rect(x, t + ch - bh, bw, Math.Max(0, bh)), 2, 2);
                    }
                }
                else
                {
                    var pts = new List<Point>();
                    var segs = new List<List<Point>>();
                    var cur = new List<Point>();
                    for (int i = 0; i < n; i++)
                    {
                        if (s.Values[i] == null) { if (cur.Count > 0) { segs.Add(cur); cur = new List<Point>(); } continue; }
                        double x = l + (n < 2 ? cw / 2 : cw * i / (n - 1));
                        double y = t + ch - ch * s.Values[i].Value / max;
                        var p = new Point(x, y); cur.Add(p); pts.Add(p);
                    }
                    if (cur.Count > 0) segs.Add(cur);
                    foreach (var seg in segs)
                    {
                        if (s.Fill && seg.Count > 1)
                        {
                            var fg = new StreamGeometry();
                            using (var c = fg.Open())
                            {
                                c.BeginFigure(new Point(seg[0].X, t + ch), true, true);
                                foreach (var p in seg) c.LineTo(p, false, false);
                                c.LineTo(new Point(seg[seg.Count - 1].X, t + ch), false, false);
                            }
                            var fill = new LinearGradientBrush(Color.FromArgb(110, s.Color.R, s.Color.G, s.Color.B), Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 90);
                            dc.DrawGeometry(fill, null, fg);
                        }
                        if (seg.Count > 1)
                        {
                            var g = new StreamGeometry();
                            using (var c = g.Open()) { c.BeginFigure(seg[0], false, false); for (int i = 1; i < seg.Count; i++) c.LineTo(seg[i], true, true); }
                            var pen = new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round };
                            dc.DrawGeometry(null, pen, g);
                        }
                        if (Dots || seg.Count == 1) foreach (var p in seg) dc.DrawEllipse(brush, null, p, 2.6, 2.6);
                    }
                }
                sIndex++;
            }
        }
    }
}
