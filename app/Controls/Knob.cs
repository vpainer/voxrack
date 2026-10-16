using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.System;

namespace Golos.Controls;

// Ручка: тянуть вверх-вниз (Shift — точно), колесо, двойной клик — по умолчанию,
// двойной клик по значению — ввести руками. Нарисована векторами XAML.
sealed class Knob : UserControl
{
    public sealed record Spec(string Label, float Min, float Max, float Step = 0.1f, float Def = float.NaN,
        bool Log = false, bool Bipolar = false, Func<float, string> Fmt = null, string Tip = null);

    const double Arc = 135, Size = 40, R = 17;
    readonly Spec s;
    readonly Path track, arc;
    readonly RotateTransform rot = new() { CenterX = Size / 2, CenterY = Size / 2 };
    readonly TextBlock val;
    readonly Grid valHost;
    readonly Grid dial;
    TextBox editor;
    (double y, float t, bool shift)? drag;
    public float Value { get; private set; }
    public Action<float> OnInput;

    public Knob(Spec spec)
    {
        s = spec;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        var root = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 54 };
        root.Children.Add(new TextBlock { Text = s.Label, Style = (Style)Application.Current.Resources["CtrlLabelStyle"] });
        Ellipse cap = null;
        Line ptr = null;

        dial = new Grid { Width = Size, Height = Size, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Center };
        track = new Path { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        track.Data = ArcGeometry(-Arc, Arc);
        arc = new Path { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        cap = new Ellipse
        {
            Width = 25, Height = 25,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ptr = new Line
        {
            X1 = Size / 2, Y1 = Size / 2, X2 = Size / 2, Y2 = 9.5,
            StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rot,
        };
        dial.Children.Add(track);
        dial.Children.Add(arc);
        dial.Children.Add(cap);
        dial.Children.Add(ptr);
        root.Children.Add(dial);

        val = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // цвета — из темы окна; при смене темы Windows перекрашиваются
        Th.On(() =>
        {
            track.Stroke = Th.B("TrackBrush");
            arc.Stroke = Th.B(active ? "AccentFillColorDefaultBrush" : "TextFillColorDisabledBrush");
            cap.Fill = Th.B("ControlFillColorDefaultBrush");
            cap.Stroke = Th.B("ControlStrokeColorDefaultBrush");
            ptr.Stroke = Th.B("TextFillColorPrimaryBrush");
            val.Foreground = Th.B("TextFillColorPrimaryBrush");
        });
        valHost = new Grid { Height = 20, MinWidth = 54 };
        valHost.Children.Add(val);
        root.Children.Add(valHost);
        Content = root;
        if (s.Tip != null) ToolTipService.SetToolTip(this, s.Tip);

        dial.PointerPressed += Dial_PointerPressed;
        dial.PointerMoved += Dial_PointerMoved;
        dial.PointerReleased += (_, e) => { drag = null; dial.ReleasePointerCapture(e.Pointer); };
        dial.PointerCaptureLost += (_, _) => drag = null;
        dial.DoubleTapped += (_, e) => { if (!float.IsNaN(s.Def)) Commit(s.Def); e.Handled = true; };
        dial.PointerWheelChanged += (_, e) =>
        {
            int d = e.GetCurrentPoint(dial).Properties.MouseWheelDelta;
            float k = Shift(e) ? 0.004f : 0.03f;
            Commit(Denorm(Math.Clamp(Norm(Value) + (d > 0 ? k : -k), 0, 1)));
            e.Handled = true;
        };
        val.DoubleTapped += (_, e) => { Edit(); e.Handled = true; };
        KeyDown += Knob_KeyDown;
        Set(float.IsNaN(s.Def) ? s.Min : s.Def);
    }

    // модуль выключен — дуга гаснет, ручка остаётся рабочей
    bool active = true;
    public bool Active
    {
        set
        {
            active = value;
            arc.Stroke = Th.B(value ? "AccentFillColorDefaultBrush" : "TextFillColorDisabledBrush");
        }
    }

    static bool Shift(PointerRoutedEventArgs e) => (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;

    void Dial_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(dial).Properties.IsLeftButtonPressed) return;
        dial.CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        drag = (e.GetCurrentPoint(dial).Position.Y, Norm(Value), Shift(e));
        e.Handled = true;
    }

    void Dial_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (drag is not var (y0, t0, sh)) return;
        double y = e.GetCurrentPoint(dial).Position.Y;
        bool shift = Shift(e);
        float k = shift ? 1 / 1600f : 1 / 180f;
        Commit(Denorm(Math.Clamp(t0 + (float)(y0 - y) * k, 0, 1)));
        if (shift != sh) drag = (y, Norm(Value), shift);
    }

    void Knob_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (editor != null) return;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        float k = shift ? 0.002f : 0.02f;
        if (e.Key is VirtualKey.Up or VirtualKey.Right) Commit(Denorm(Math.Clamp(Norm(Value) + k, 0, 1)));
        else if (e.Key is VirtualKey.Down or VirtualKey.Left) Commit(Denorm(Math.Clamp(Norm(Value) - k, 0, 1)));
        else return;
        e.Handled = true;
    }

    float Norm(float v) => s.Log ? MathF.Log(v / s.Min) / MathF.Log(s.Max / s.Min) : (v - s.Min) / (s.Max - s.Min);
    float Denorm(float t) => s.Log ? s.Min * MathF.Pow(s.Max / s.Min, t) : s.Min + t * (s.Max - s.Min);

    float Quant(float v)
    {
        double q;
        if (s.Log)
        {
            // логарифмические — три значащие цифры: 0.35 мс, 3.2 : 1, 148 Гц, 6540 Гц
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(v) > 0 ? Math.Abs(v) : 1)) - 2);
            q = Math.Round(v / mag) * mag;
        }
        else q = Math.Round(v / s.Step) * s.Step;
        return (float)Math.Clamp(Math.Round(q, 4), s.Min, s.Max);
    }

    public void Set(float v)
    {
        Value = v;
        double t = Math.Clamp(Norm(v), 0, 1);
        double a = -Arc + t * 2 * Arc;
        double a0 = s.Bipolar ? -Arc + Math.Clamp(Norm(0), 0, 1) * 2 * Arc : -Arc;
        arc.Data = Math.Abs(a - a0) < 0.5 ? null : ArcGeometry(Math.Min(a0, a), Math.Max(a0, a));
        rot.Angle = a;
        if (editor == null) val.Text = (s.Fmt ?? (x => x.ToString("0.0")))(v);
    }

    public void Commit(float v)
    {
        v = Quant(v);
        if (v == Value) return;
        Set(v);
        OnInput?.Invoke(v);
    }

    static Point Pt(double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(Size / 2 + R * Math.Sin(a), Size / 2 - R * Math.Cos(a));
    }

    static Geometry ArcGeometry(double from, double to)
    {
        var fig = new PathFigure { StartPoint = Pt(from), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = Pt(to),
            Size = new Size(R, R),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = to - from > 180,
        });
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    void Edit()
    {
        if (editor != null) return;
        editor = new TextBox
        {
            Text = Math.Round(Value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 11,
            MinWidth = 54, Width = 60, MinHeight = 20, Height = 22,
            Padding = new Thickness(4, 1, 4, 1),
            TextAlignment = TextAlignment.Center,
        };
        val.Visibility = Visibility.Collapsed;
        valHost.Children.Add(editor);
        bool done = false;
        void Done(bool ok)
        {
            if (done) return;
            done = true;
            var text = editor.Text.Trim().Replace(',', '.');
            valHost.Children.Remove(editor);
            editor = null;
            val.Visibility = Visibility.Visible;
            val.Text = (s.Fmt ?? (x => x.ToString("0.0")))(Value);
            if (!ok) return;
            bool kilo = text.Contains('k', StringComparison.OrdinalIgnoreCase);
            text = text.TrimEnd('k', 'K', ' ');
            if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                if (kilo) v *= 1000;
                Commit(Math.Clamp(v, s.Min, s.Max));
            }
        }
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { Done(true); e.Handled = true; Focus(FocusState.Programmatic); }
            else if (e.Key == VirtualKey.Escape) { Done(false); e.Handled = true; Focus(FocusState.Programmatic); }
        };
        editor.LostFocus += (_, _) => Done(true);
        editor.Loaded += (_, _) => { editor?.Focus(FocusState.Programmatic); editor?.SelectAll(); };
    }
}
