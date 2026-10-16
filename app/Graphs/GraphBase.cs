using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Golos.Graphs;

// Цвета графиков: тёмная палитра — как в прежнем окне, светлая — под светлую тему Windows.
// Подложки полупрозрачные: под ними Mica. Акцент — системный цвет (как AccentFillColorDefault в этой теме),
// подавление рисуется им же.
static class Pal
{
    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static readonly Windows.UI.ViewManagement.UISettings ui = new();

    public static bool Light { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color Gr { get; private set; }
    public static Color GrSoft { get; private set; }
    public static Color Text { get; private set; }
    public static Color Faint { get; private set; }
    public static Color Bright { get; private set; }
    public static Color Grid { get; private set; }
    public static Color Grid2 { get; private set; }
    public static Color Zero { get; private set; }
    public static Color Spec { get; private set; }
    public static Color SpecLine { get; private set; }
    public static Color Pre { get; private set; }
    public static Color Bg { get; private set; }
    public static Color Panel { get; private set; }
    public static Color Frame { get; private set; }
    public static Color Off { get; private set; }
    public static Color Lane { get; private set; }      // дорожки нот, «пустая» заливка
    public static Color NodeBg { get; private set; }    // кружки полос эквалайзера
    public static Color Veil { get; private set; }      // плашка поверх графика
    public static Color Yellow { get; private set; }
    public static readonly Color Red = C(0xFFFF5148), Green = C(0xFF3DB866);

    static Pal() => Refresh(false);

    public static void Refresh(bool light)
    {
        Light = light;
        Color a;
        try { a = ui.GetColorValue(light ? Windows.UI.ViewManagement.UIColorType.AccentDark1 : Windows.UI.ViewManagement.UIColorType.AccentLight2); }
        catch { a = light ? C(0xFF005FB8) : C(0xFF60CDFF); }
        Accent = Gr = a;
        AccentSoft = A(a, light ? 0.12f : 0.09f);
        GrSoft = A(a, light ? 0.22f : 0.2f);
        if (light)
        {
            Text = C(0xFF5F646B); Faint = C(0xFF8E939A); Bright = C(0xFF1C1F23);
            Grid = C(0x14000000); Grid2 = C(0x09000000); Zero = C(0x40000000);
            Spec = C(0x1A2E3742); SpecLine = C(0x592E3742); Pre = C(0x732E3742);
            Bg = C(0x0C000000); Panel = C(0xF7FBFBFB); Frame = C(0x24000000); Off = C(0x17000000);
            Lane = C(0x0A000000); NodeBg = C(0xFFFFFFFF); Veil = C(0xE0F6F6F6); Yellow = C(0xFFE0A800);
        }
        else
        {
            Text = C(0xFF8A8F96); Faint = C(0xFF5E636B); Bright = C(0xFFD9DBDE);
            Grid = C(0x0FFFFFFF); Grid2 = C(0x07FFFFFF); Zero = C(0x33FFFFFF);
            Spec = C(0x1ABEC8D7); SpecLine = C(0x4DBEC8D7); Pre = C(0x59BEC8D7);
            Bg = C(0x59000000); Panel = C(0xF02B2C30); Frame = C(0x14FFFFFF); Off = C(0x14FFFFFF);
            Lane = C(0x09FFFFFF); NodeBg = C(0xFF121315); Veil = C(0xD6101113); Yellow = C(0xFFF0C23B);
        }
    }

    // системный акцент сменили — сообщаем окну (событие приходит не на потоке окна)
    public static event Action Changed
    {
        add => ui.ColorValuesChanged += (_, _) => value();
        remove { }
    }

    public static Color A(Color c, float a) => Color.FromArgb((byte)(c.A * Math.Clamp(a, 0, 1)), c.R, c.G, c.B);
}

// подложка холста: через неё ставим курсор (у CanvasControl своего нет) и ловим мышь
sealed class GraphHost : Grid
{
    InputSystemCursorShape shape = InputSystemCursorShape.Arrow;

    public void SetCursor(InputSystemCursorShape s)
    {
        if (s == shape) return;
        shape = s;
        ProtectedCursor = InputSystemCursor.Create(s);
    }
}

// холст с перерисовкой по требованию
abstract class GraphView
{
    public readonly GraphHost Host = new() { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    public readonly CanvasControl Canvas = new() { ClearColor = Microsoft.UI.Colors.Transparent };
    public float Fs = 48000;
    public bool Live;     // перерисовывать каждый кадр (живой спектр, история)
    protected float W = 10, H = 10;
    static readonly Dictionary<(float, int, int, bool), CanvasTextFormat> formats = new();
    protected static readonly CanvasStrokeStyle Dash3 = new() { CustomDashStyle = new[] { 3f, 3f } };
    protected static readonly CanvasStrokeStyle Dash4 = new() { CustomDashStyle = new[] { 4f, 3f } };
    protected static readonly CanvasStrokeStyle Dash5 = new() { CustomDashStyle = new[] { 5f, 4f } };
    bool dragging;

    protected GraphView()
    {
        Host.Children.Add(Canvas);
        Canvas.Draw += (s, e) =>
        {
            W = (float)s.ActualWidth;
            H = (float)s.ActualHeight;
            if (W > 2 && H > 2) Draw(e.DrawingSession);
        };
        Canvas.SizeChanged += (_, _) =>
        {
            W = (float)Canvas.ActualWidth;
            H = (float)Canvas.ActualHeight;
            OnResize();
            Canvas.Invalidate();
        };
        Host.PointerPressed += (s, e) =>
        {
            var pt = e.GetCurrentPoint(Host);
            if (pt.Properties.IsRightButtonPressed) { OnRight(Pos(pt.Position)); e.Handled = true; return; }
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (OnDown(Pos(pt.Position), Shift(e)))
            {
                dragging = Host.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        };
        Host.PointerMoved += (s, e) =>
        {
            var p = Pos(e.GetCurrentPoint(Host).Position);
            if (dragging) OnDrag(p, Shift(e));
            else OnHover(p);
        };
        Host.PointerReleased += (s, e) => EndDrag(e);
        Host.PointerCaptureLost += (s, e) => { if (dragging) { dragging = false; OnUp(); } };
        Host.PointerExited += (s, e) => { if (!dragging) OnLeave(); };
        Host.DoubleTapped += (s, e) => OnDouble(Pos(e.GetPosition(Host)));
        Host.PointerWheelChanged += (s, e) =>
        {
            var pt = e.GetCurrentPoint(Host);
            if (OnWheel(Pos(pt.Position), pt.Properties.MouseWheelDelta, Shift(e))) e.Handled = true;
        };
    }

    // элемент поверх холста (плавающая панель): его мышь не должна доходить до графика
    public void AddOverlay(FrameworkElement el)
    {
        Host.Children.Add(el);
        el.PointerPressed += (_, e) => e.Handled = true;
        el.PointerMoved += (_, e) => e.Handled = true;
        el.PointerWheelChanged += (_, e) => e.Handled = true;
        el.DoubleTapped += (_, e) => e.Handled = true;
        el.RightTapped += (_, e) => e.Handled = true;
    }

    void EndDrag(PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Host.ReleasePointerCapture(e.Pointer);
        OnUp();
    }

    static Vector2 Pos(Point p) => new((float)p.X, (float)p.Y);
    static bool Shift(PointerRoutedEventArgs e) => (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;

    public void Invalidate() => Canvas.Invalidate();

    protected virtual void OnResize() { }
    protected virtual bool OnDown(Vector2 p, bool shift) => false;
    protected virtual void OnDrag(Vector2 p, bool shift) { }
    protected virtual void OnUp() { }
    protected virtual void OnHover(Vector2 p) { }
    protected virtual void OnLeave() { }
    protected virtual void OnDouble(Vector2 p) { }
    protected virtual void OnRight(Vector2 p) { }
    protected virtual bool OnWheel(Vector2 p, int delta, bool shift) => false;
    protected abstract void Draw(CanvasDrawingSession ds);

    // ── рисование ──

    protected static CanvasTextFormat Fmt(float size, CanvasHorizontalAlignment ha, CanvasVerticalAlignment va, bool ui = false)
    {
        var key = (size, (int)ha, (int)va, ui);
        if (!formats.TryGetValue(key, out var f))
        {
            f = new CanvasTextFormat
            {
                FontFamily = ui ? "Segoe UI Variable Text" : "Cascadia Mono",
                FontSize = size,
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                WordWrapping = CanvasWordWrapping.NoWrap,
                FontWeight = ui && size >= 14 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            formats[key] = f;
        }
        return f;
    }

    // ha: -1 влево, 0 по центру, 1 вправо; va: -1 сверху, 0 по центру, 1 снизу
    protected static void Text(CanvasDrawingSession ds, string s, float x, float y, Color c, int ha = -1, int va = 0, float size = 10, bool ui = false)
    {
        var f = Fmt(size,
            ha < 0 ? CanvasHorizontalAlignment.Left : ha > 0 ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Center,
            va < 0 ? CanvasVerticalAlignment.Top : va > 0 ? CanvasVerticalAlignment.Bottom : CanvasVerticalAlignment.Center, ui);
        ds.DrawText(s, x, y, c, f);
    }

    protected static float TextWidth(CanvasDrawingSession ds, string s, float size = 10)
    {
        using var l = new CanvasTextLayout(ds, s, Fmt(size, CanvasHorizontalAlignment.Left, CanvasVerticalAlignment.Top), 0, 0);
        return (float)l.LayoutBounds.Width;
    }

    protected static CanvasGeometry Polyline(CanvasDrawingSession ds, ReadOnlySpan<Vector2> pts)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(pts[0]);
        for (int i = 1; i < pts.Length; i++) pb.AddLine(pts[i]);
        pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    protected static CanvasGeometry Polygon(CanvasDrawingSession ds, ReadOnlySpan<Vector2> pts)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(pts[0]);
        for (int i = 1; i < pts.Length; i++) pb.AddLine(pts[i]);
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
    }

    protected static void HLine(CanvasDrawingSession ds, float x0, float x1, float y, Color c, CanvasStrokeStyle st = null)
    {
        y = MathF.Round(y) + 0.5f;
        if (st != null) ds.DrawLine(x0, y, x1, y, c, 1, st);
        else ds.DrawLine(x0, y, x1, y, c);
    }

    protected static void VLine(CanvasDrawingSession ds, float x, float y0, float y1, Color c, CanvasStrokeStyle st = null)
    {
        x = MathF.Round(x) + 0.5f;
        if (st != null) ds.DrawLine(x, y0, x, y1, c, 1, st);
        else ds.DrawLine(x, y0, x, y1, c);
    }

    protected static void Box(CanvasDrawingSession ds, float x, float y, float w, float h, Color c) =>
        ds.DrawRectangle(x + 0.5f, y + 0.5f, w - 1, h - 1, c);
}

// сглаженный спектр — один на все графики
static class Spectrum
{
    public const float Top = -6, Bot = -96;   // шкала спектра (дБFS с наклоном +3 дБ/окт)
    public static readonly float[] Post = Fill(), PreDb = Fill();
    public static bool ShowPre;
    static float[] Fill() { var a = new float[Analyzer.Bins]; Array.Fill(a, Bot); return a; }

    public static void Update(float[] post, float[] pre)
    {
        Step(Post, post);
        Step(PreDb, pre);
    }

    static void Step(float[] disp, float[] src)
    {
        for (int i = 0; i < disp.Length; i++)
        {
            float t = src != null ? src[i] : Bot - 10;
            disp[i] = t > disp[i] ? disp[i] * 0.35f + t * 0.65f : MathF.Max(t, disp[i] - 1.3f);
        }
    }
}

// частотный график: лог. ось 20 Гц – 20 кГц, сетка, живой спектр
abstract class FreqGraph : GraphView
{
    public const float FMin = 20, FMax = 20000;
    protected const float PL = 30, PR = 8, PT = 8, PB = 18;
    static readonly float[] GridF = { 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
    static readonly float[] MinorF = { 30, 40, 60, 70, 80, 90, 300, 400, 600, 700, 800, 900, 3000, 4000, 6000, 7000, 8000, 9000 };
    protected float DbTop, DbBot;
    protected float[] Freqs = Array.Empty<float>();
    protected bool NoSpectrum;
    protected float PlotH => (FrameH > 0 ? FrameH : H) - PT - PB;
    protected float FrameH;   // MATCH рисует частотный график только в верхней части холста
    Vector2[] pts = Array.Empty<Vector2>();

    protected FreqGraph(float top, float bot)
    {
        DbTop = top;
        DbBot = bot;
        Live = true;
        OnResize();
    }

    protected override void OnResize()
    {
        int n = Math.Max(64, (int)MathF.Round(PW / 2));
        if (Freqs.Length == n) return;
        Freqs = new float[n];
        for (int i = 0; i < n; i++) Freqs[i] = FMin * MathF.Pow(FMax / FMin, i / (float)(n - 1));
        pts = new Vector2[n + 2];
    }

    protected float PW => W - PL - PR;
    protected float X(float f) => PL + MathF.Log(f / FMin) / MathF.Log(FMax / FMin) * PW;
    protected float Fx(float x) => FMin * MathF.Pow(FMax / FMin, (x - PL) / PW);
    protected float Y(float db) => PT + (DbTop - db) / (DbTop - DbBot) * PlotH;
    protected float DbY(float y) => DbTop - (y - PT) / PlotH * (DbTop - DbBot);
    protected float Ys(float db) => PT + (Spectrum.Top - db) / (Spectrum.Top - Spectrum.Bot) * PlotH;
    protected Rect PlotRect => new(PL, PT, Math.Max(1, PW), Math.Max(1, PlotH));

    static string FLabel(float f) => f >= 1000 ? $"{f / 1000}k" : $"{f}";

    protected void DrawFrame(CanvasDrawingSession ds)
    {
        float ph = PlotH, bottom = PT + ph;
        ds.FillRoundedRectangle(PL, PT, PW, ph, 3, 3, Pal.Bg);
        foreach (var f in MinorF) VLine(ds, X(f), PT, bottom, Pal.Grid2);
        foreach (var f in GridF) VLine(ds, X(f), PT, bottom, Pal.Grid);
        for (float d = MathF.Ceiling(DbBot / 6) * 6; d <= DbTop; d += 6)
        {
            if (d == 0 || d == DbTop || d == DbBot) continue;
            HLine(ds, PL, PL + PW, Y(d), Pal.Grid);
        }
        if (0 < DbTop && 0 > DbBot) HLine(ds, PL, PL + PW, Y(0), Pal.Zero);
        foreach (var f in GridF) Text(ds, FLabel(f), X(f), bottom + PB / 2 + 1, Pal.Faint, 0, 0);
        for (float d = MathF.Ceiling(DbBot / 12) * 12; d <= DbTop; d += 12)
        {
            if (d == DbTop || d == DbBot) continue;
            Text(ds, d > 0 ? $"+{d}" : $"{d}", PL - 5, Y(d), Pal.Faint, 1, 0);
        }

        if (NoSpectrum) return;
        int n = Spectrum.Post.Length;
        float Xs(int i) => PL + i / (float)(n - 1) * PW;
        using (ds.CreateLayer(1, PlotRect))
        {
            var line = new Vector2[n];
            if (Spectrum.ShowPre)
            {
                for (int i = 0; i < n; i++) line[i] = new Vector2(Xs(i), Ys(Spectrum.PreDb[i]));
                using var g = Polyline(ds, line);
                ds.DrawGeometry(g, Pal.Pre);
            }
            var poly = new Vector2[n + 2];
            poly[0] = new Vector2(PL, bottom);
            for (int i = 0; i < n; i++) poly[i + 1] = line[i] = new Vector2(Xs(i), Ys(Spectrum.Post[i]));
            poly[n + 1] = new Vector2(PL + PW, bottom);
            using (var g = Polygon(ds, poly)) ds.FillGeometry(g, Pal.Spec);
            using (var g = Polyline(ds, line)) ds.DrawGeometry(g, Pal.SpecLine);
        }
    }

    // кривая по сетке частот; fillTo — залить до уровня (дБ)
    protected void Curve(CanvasDrawingSession ds, Func<float, int, float> dbAt, Color color, float width = 1.5f, float? fillTo = null, Color? fill = null, CanvasStrokeStyle dash = null)
    {
        int n = Freqs.Length;
        var line = pts.AsSpan(1, n);
        for (int i = 0; i < n; i++)
        {
            float f = Freqs[i];
            line[i] = new Vector2(X(f), Y(Math.Clamp(dbAt(f, i), DbBot - 6, DbTop + 6)));
        }
        using var layer = ds.CreateLayer(1, PlotRect);
        if (fillTo is float y0 && fill is Color fc)
        {
            pts[0] = new Vector2(line[0].X, Y(y0));
            pts[n + 1] = new Vector2(line[n - 1].X, Y(y0));
            using var g = Polygon(ds, pts);
            ds.FillGeometry(g, fc);
        }
        if (width > 0 && color.A > 0)
        {
            using var g = Polyline(ds, line);
            if (dash != null) ds.DrawGeometry(g, color, width, dash);
            else ds.DrawGeometry(g, color, width);
        }
    }

    // вертикальная ручка частоты (срез низа, де-эссер, кроссоверы)
    protected void Handle(CanvasDrawingSession ds, float f, bool active, string label)
    {
        float x = MathF.Round(X(f)) + 0.5f;
        var col = active ? Pal.Accent : Pal.A(Pal.Text, 0.55f);
        ds.DrawLine(x, PT, x, PT + PlotH, col, 1, Dash3);
        ds.FillRectangle(x - 5, PT + 1, 10, 12, active ? Pal.Accent : Pal.Panel);
        ds.DrawRectangle(x - 5, PT + 1, 10, 12, active ? Pal.Accent : Pal.Text);
        if (label != null)
        {
            bool right = x > W - 70;
            Text(ds, label, x + (right ? -9 : 9), PT + 7, active ? Pal.Accent : Pal.Text, right ? 1 : -1, 0);
        }
    }
}
