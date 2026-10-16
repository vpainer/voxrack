using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Golos.Graphs;

// живые показания для графиков
sealed class LiveState
{
    public float Gate, Comp, Ds, Lim, Mb, Tune;
    public readonly float[] MbIn = { -120, -120, -120, -120 }, MbGr = new float[4];
}

// ── многополосный компрессор, как в FabFilter Pro-MB ──
// Каждая полоса — своя зона и своя форма: пунктир — постоянное усиление полосы, заливка — сколько она
// прижимает прямо сейчас. Точка полосы: вверх-вниз — усиление, вбок — сдвинуть полосу; колесо — порог,
// Ctrl+колесо — степень; двойной клик — вкл/выкл. Границы полос тянутся за край. Сплошная кривая — итог
// всей обработки по частотам (полосы + общий компрессор + де-эссер).
sealed class DynGraph : FreqGraph
{
    readonly Func<float[]> getParams;
    readonly Func<LiveState> getLive;
    public Action OnEdit;
    public Action<int> OnSelect;
    public int Sel;
    FrameworkElement overlay;
    double overlayLeft = double.NaN;

    enum Kind { None, Edge, Node, Zone }
    (Kind kind, int i) hover = (Kind.None, -1);
    (Kind kind, int i) drag = (Kind.None, -1);
    Vector2 dragFrom;
    float dragGain;
    float[] dragEdges;

    static readonly int[] XIdx = { P.MB_X1, P.MB_X2, P.MB_X3 };
    const float NodeR = 6;

    public DynGraph(Func<float[]> getParams, Func<LiveState> getLive) : base(12, -24)
    {
        this.getParams = getParams;
        this.getLive = getLive;
    }

    // панель выбранной полосы висит под ней и едет за ней
    public void SetOverlay(FrameworkElement el)
    {
        overlay = el;
        overlay.HorizontalAlignment = HorizontalAlignment.Left;
        overlay.VerticalAlignment = VerticalAlignment.Bottom;
        AddOverlay(el);
        overlay.SizeChanged += (_, _) => { overlayLeft = double.NaN; PlaceOverlay(); };
    }

    void PlaceOverlay()
    {
        if (overlay == null || W < 10) return;
        double w = overlay.ActualWidth;
        var (lo, hi) = Zone(Sel);
        double cx = (X(lo) + X(hi)) / 2;
        double left = Math.Clamp(cx - w / 2, PL + 4, Math.Max(PL + 4, W - PR - w - 4));
        if (Math.Abs(left - overlayLeft) < 0.5) return;
        overlayLeft = left;
        overlay.Margin = new Thickness(left, 0, 0, PB + 6);
    }

    float[] Xo()
    {
        var p = getParams();
        return new[] { p[P.MB_X1], p[P.MB_X2], p[P.MB_X3] };
    }

    (float lo, float hi) Zone(int k)
    {
        var x = Xo();
        return (k == 0 ? FMin : x[k - 1], k == 3 ? FMax : x[k]);
    }

    // центр полосы на оси (крайние полосы — середина видимой части)
    float CenterX(int k)
    {
        var (lo, hi) = Zone(k);
        return (X(lo) + X(hi)) / 2;
    }

    Vector2 NodePos(int k) => new(CenterX(k), Y(Math.Clamp(Params.Mb(getParams(), k).Gain, DbBot, DbTop)));

    (Kind, int) Hit(Vector2 p)
    {
        for (int k = 0; k < P.MB_BANDS; k++)
            if (Vector2.DistanceSquared(NodePos(k), p) <= 11 * 11) return (Kind.Node, k);
        var xo = Xo();
        for (int i = 0; i < 3; i++) if (MathF.Abs(p.X - X(xo[i])) < 6) return (Kind.Edge, i);
        for (int k = 0; k < P.MB_BANDS; k++)
        {
            var (lo, hi) = Zone(k);
            if (p.X >= X(lo) && p.X < X(hi)) return (Kind.Zone, k);
        }
        return (Kind.None, -1);
    }

    public void Select(int k)
    {
        Sel = k;
        overlayLeft = double.NaN;
        PlaceOverlay();
        Invalidate();
        OnSelect?.Invoke(k);
    }

    protected override void OnResize()
    {
        base.OnResize();
        overlayLeft = double.NaN;
    }

    protected override bool OnDown(Vector2 p, bool shift)
    {
        var (kind, i) = Hit(p);
        if (kind == Kind.None) return false;
        if (kind is Kind.Zone or Kind.Node && i != Sel) Select(i);
        if (kind == Kind.Zone) return false;
        drag = (kind, i);
        dragFrom = p;
        var pp = getParams();
        dragGain = Params.Mb(pp, i).Gain;
        dragEdges = Xo();
        Host.SetCursor(kind == Kind.Edge ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
        return true;
    }

    protected override void OnDrag(Vector2 p, bool shift)
    {
        var pp = getParams();
        float k = shift ? 0.15f : 1;
        if (drag.kind == Kind.Edge)
        {
            int i = drag.i;
            pp[XIdx[i]] = MathF.Round(Math.Clamp(Fx(X(dragEdges[i]) + (p.X - dragFrom.X) * k), EdgeMin(pp, i), EdgeMax(pp, i)));
        }
        else if (drag.kind == Kind.Node)
        {
            int b = drag.i;
            // вверх-вниз — усиление полосы
            float g = DbY(Y(dragGain) + (p.Y - dragFrom.Y) * k);
            pp[P.MB_BASE + b * P.MB_STRIDE + 5] = MathF.Round(Math.Clamp(g, -18, 18) * 10) / 10;
            // вбок — сдвинуть полосу: обе её границы на одну долю октавы (ширина полосы та же)
            var d = dragEdges;
            float ratio = Fx(X(1000) + (p.X - dragFrom.X) * k) / 1000, lo = 0, hi = float.MaxValue;
            if (b > 0)
            {
                int e = b - 1;
                lo = MathF.Max(lo, (e == 0 ? 40 : d[e - 1] * 1.4f) / d[e]);
                if (b == 3) hi = MathF.Min(hi, 18000 / d[e]);
            }
            if (b < 3)
            {
                int e = b;
                hi = MathF.Min(hi, (e == 2 ? 18000 : d[e + 1] / 1.4f) / d[e]);
                if (b == 0) lo = MathF.Max(lo, 40 / d[e]);
            }
            ratio = Math.Clamp(ratio, lo, MathF.Max(lo, hi));
            if (b > 0) pp[XIdx[b - 1]] = MathF.Round(d[b - 1] * ratio);
            if (b < 3) pp[XIdx[b]] = MathF.Round(d[b] * ratio);
        }
        overlayLeft = double.NaN;
        PlaceOverlay();
        Invalidate();
        OnEdit?.Invoke();
    }

    // границы не сходятся ближе, чем в 1.4 раза, и не выходят за 40 Гц – 18 кГц
    static float EdgeMin(float[] pp, int i) => i == 0 ? 40 : pp[XIdx[i - 1]] * 1.4f;
    static float EdgeMax(float[] pp, int i) => i == 2 ? 18000 : pp[XIdx[i + 1]] / 1.4f;

    protected override void OnUp()
    {
        drag = (Kind.None, -1);
        Invalidate();
    }

    protected override void OnHover(Vector2 p)
    {
        var h = Hit(p);
        if (h != hover) { hover = h; Invalidate(); }
        Host.SetCursor(h.Item1 switch
        {
            Kind.Edge => InputSystemCursorShape.SizeWestEast,
            Kind.Node => InputSystemCursorShape.SizeAll,
            Kind.Zone => InputSystemCursorShape.Hand,
            _ => InputSystemCursorShape.Arrow,
        });
    }

    protected override void OnLeave()
    {
        if (hover.Item1 != Kind.None) { hover = (Kind.None, -1); Invalidate(); }
    }

    protected override void OnDouble(Vector2 p)
    {
        var (kind, i) = Hit(p);
        if (kind != Kind.Node) return;
        var pp = getParams();
        int o = P.MB_BASE + i * P.MB_STRIDE;
        pp[o] = pp[o] >= 0.5f ? 0 : 1;
        Invalidate();
        OnEdit?.Invoke();
    }

    // колесо над полосой — порог (Shift — точно), с Ctrl — степень сжатия
    protected override bool OnWheel(Vector2 p, int delta, bool shift)
    {
        var (kind, i) = Hit(p);
        if (kind is not (Kind.Node or Kind.Zone)) return false;
        if (i != Sel) Select(i);
        var pp = getParams();
        int o = P.MB_BASE + i * P.MB_STRIDE;
        int dir = delta > 0 ? 1 : -1;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) pp[o + 2] = MathF.Round(Math.Clamp(pp[o + 2] * MathF.Pow(shift ? 1.02f : 1.1f, dir), 1, 20) * 100) / 100;
        else pp[o + 1] = MathF.Round(Math.Clamp(pp[o + 1] + dir * (shift ? 0.25f : 1f), -60, 0) * 4) / 4;
        Invalidate();
        OnEdit?.Invoke();
        return true;
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        var L = getLive();
        PlaceOverlay();
        DrawFrame(ds);
        bool on = p[P.MB_ON] >= 0.5f;
        float x1 = p[P.MB_X1], x2 = p[P.MB_X2], x3 = p[P.MB_X3];
        var bands = new MbBand[4];
        for (int k = 0; k < 4; k++) bands[k] = Params.Mb(p, k);
        var gr = L.MbGr;
        int n = Freqs.Length;
        var w = new double[n * 4];
        for (int i = 0; i < n; i++) DspMath.MbWeights(Freqs[i], x1, x2, x3, w.AsSpan(i * 4, 4));

        // зоны: выбранная и та, что под мышью
        using (ds.CreateLayer(1, PlotRect))
        {
            for (int k = 0; k < 4; k++)
            {
                bool sel = k == Sel, hov = hover.Item1 is Kind.Zone or Kind.Node && hover.Item2 == k;
                if (!sel && !hov) continue;
                var (lo, hi) = Zone(k);
                ds.FillRectangle(X(lo), PT, X(hi) - X(lo), PlotH, sel ? Pal.AccentSoft : Pal.Lane);
            }
        }

        // форма каждой полосы: что она делает с частотами сама по себе
        for (int k = 0; k < 4; k++)
        {
            bool live = on && bands[k].On;
            bool sel = k == Sel;
            var col = sel ? Pal.Accent : Pal.Text;
            double gs = DspMath.Lin(bands[k].Gain), gd = DspMath.Lin(bands[k].Gain - (live ? gr[k] : 0));
            float Shape(double g, int i) => (float)(20 * Math.Log10(Math.Max(1 + w[i * 4 + k] * (g - 1), 1e-6)));
            var stat = new Vector2[n];
            var dyn = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float x = X(Freqs[i]);
                stat[i] = new Vector2(x, Y(live ? Shape(gs, i) : 0));
                dyn[i] = new Vector2(x, Y(MathF.Max(live ? Shape(gd, i) : 0, DbBot - 6)));
            }
            using var layer = ds.CreateLayer(1, PlotRect);
            // заливка от нуля до того, что полоса делает сейчас (с подавлением)
            var fill = new Vector2[n + 2];
            fill[0] = new Vector2(dyn[0].X, Y(0));
            for (int i = 0; i < n; i++) fill[i + 1] = dyn[i];
            fill[n + 1] = new Vector2(dyn[n - 1].X, Y(0));
            using (var g = Polygon(ds, fill)) ds.FillGeometry(g, Pal.A(col, sel ? 0.3f : 0.14f));
            using (var g = Polyline(ds, dyn)) ds.DrawGeometry(g, Pal.A(col, sel ? 1 : 0.55f), sel ? 1.5f : 1);
            if (live && MathF.Abs(bands[k].Gain) > 0.05f && gr[k] > 0.05f)
                using (var g = Polyline(ds, stat)) ds.DrawGeometry(g, Pal.A(col, 0.6f), 1, Dash4);
        }

        // итог всей обработки по частотам
        bool compOn = p[P.COMP_ON] >= 0.5f, dsOn = p[P.DS_ON] >= 0.5f;
        double fd = p[P.DS_FREQ];
        double comp = compOn ? DspMath.Lin(-L.Comp) : 1;
        double dsG = dsOn ? DspMath.Lin(-L.Ds) : 1;
        Curve(ds, (f, i) =>
        {
            double g = 0;
            for (int k = 0; k < 4; k++) g += w[i * 4 + k] * (on && bands[k].On ? DspMath.Lin(bands[k].Gain - gr[k]) : 1);
            g *= comp;
            if (dsOn) { double u = (f / fd) * (f / fd); g *= 1 - (1 - dsG) * (u / (1 + u)); }
            return (float)(20 * Math.Log10(Math.Max(g, 1e-6)));
        }, Pal.A(Pal.Bright, 0.85f), 1.5f);

        // границы полос
        float[] xs = { x1, x2, x3 };
        for (int i = 0; i < 3; i++)
        {
            bool act = drag.kind == Kind.Edge && drag.i == i || hover.Item1 == Kind.Edge && hover.Item2 == i;
            float x = MathF.Round(X(xs[i])) + 0.5f;
            ds.DrawLine(x, PT, x, PT + PlotH, act ? Pal.Accent : Pal.A(Pal.Text, 0.45f), 1, Dash3);
            ds.FillRoundedRectangle(x - 3, PT + 1, 6, 14, 3, 3, act ? Pal.Accent : Pal.A(Pal.Text, 0.6f));
            if (act)
            {
                bool right = x > W - 70;
                Text(ds, $"{F.HzShort(xs[i])}Hz", x + (right ? -8 : 8), PT + 8, Pal.Accent, right ? 1 : -1, 0);
            }
        }

        // подписи полос и точки
        for (int k = 0; k < P.MB_BANDS; k++)
        {
            var (lo, hi) = Zone(k);
            float zx = X(lo), zw = X(hi) - zx;
            bool live = on && bands[k].On, sel = k == Sel;
            if (zw >= 44)
            {
                Text(ds, Params.MbNames[k], zx + 9, PT + 4, sel ? Pal.Accent : live ? Pal.Text : Pal.Faint, -1, -1);
                if (live && gr[k] > 0.1f && zw > 96) Text(ds, $"−{gr[k]:0.0}", zx + zw - 8, PT + 4, sel ? Pal.Accent : Pal.Text, 1, -1);
            }
            var np = NodePos(k);
            bool hov = hover.Item1 == Kind.Node && hover.Item2 == k || drag.kind == Kind.Node && drag.i == k;
            float r = hov ? NodeR + 1.5f : NodeR;
            if (!live) ds.DrawCircle(np, r, Pal.Faint, 1.5f);
            else if (sel) { ds.FillCircle(np, r, Pal.Accent); ds.DrawCircle(np, r + 2.5f, Pal.A(Pal.Accent, 0.35f), 1); }
            else { ds.FillCircle(np, r, Pal.NodeBg); ds.DrawCircle(np, r, Pal.Text, 1.5f); }
            if (hov || drag.kind == Kind.Node && drag.i == k)
            {
                var b = bands[k];
                string txt = $"{(b.Gain > 0 ? "+" : "")}{b.Gain:0.0} dB · THR {b.T:0.#} · {b.R:0.0}:1";
                float tw = TextWidth(ds, txt) + 12;
                float tx = Math.Clamp(np.X - tw / 2, PL + 2, W - tw - 4), ty = np.Y - 30;
                if (ty < PT + 18) ty = np.Y + 14;
                ds.FillRoundedRectangle(tx, ty, tw, 18, 3, 3, Pal.Panel);
                ds.DrawRoundedRectangle(tx + 0.5f, ty + 0.5f, tw - 1, 17, 3, 3, Pal.Frame);
                Text(ds, txt, tx + 6, ty + 9, Pal.Bright, -1, 0);
            }
        }
    }
}

// ── срез низа ──
sealed class HpfGraph : FreqGraph
{
    readonly Func<float[]> getParams;
    public Action OnEdit;

    public HpfGraph(Func<float[]> getParams) : base(6, -36) => this.getParams = getParams;

    bool Near(Vector2 p) => MathF.Abs(p.X - X(getParams()[P.HPF_FREQ])) <= 10;

    protected override bool OnDown(Vector2 p, bool shift) => Near(p);

    protected override void OnDrag(Vector2 p, bool shift)
    {
        getParams()[P.HPF_FREQ] = MathF.Round(Math.Clamp(Fx(p.X), 20, 400));
        Invalidate();
        OnEdit?.Invoke();
    }

    protected override void OnHover(Vector2 p) => Host.SetCursor(Near(p) ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        DrawFrame(ds);
        var cf = DspMath.HpfCoefs(p, Fs, force: true);
        bool on = p[P.HPF_ON] >= 0.5f;
        Curve(ds, (f, _) => { double s = 0; foreach (var c in cf) s += DspMath.MagDb(c, f, Fs); return (float)s; },
            Pal.A(Pal.Accent, on ? 1 : 0.4f), 1.75f, 0, Pal.A(Pal.AccentSoft, on ? 1 : 0.4f));
        Handle(ds, p[P.HPF_FREQ], on, $"{F.HzShort(p[P.HPF_FREQ])}Hz");
    }
}

// ── де-эссер ──
sealed class DsGraph : FreqGraph
{
    readonly Func<float[]> getParams;
    readonly Func<LiveState> getLive;
    public Action OnEdit;

    public DsGraph(Func<float[]> getParams, Func<LiveState> getLive) : base(6, -24)
    {
        this.getParams = getParams;
        this.getLive = getLive;
    }

    bool Near(Vector2 p) => MathF.Abs(p.X - X(getParams()[P.DS_FREQ])) <= 10;

    protected override bool OnDown(Vector2 p, bool shift) => Near(p);

    protected override void OnDrag(Vector2 p, bool shift)
    {
        getParams()[P.DS_FREQ] = MathF.Round(Math.Clamp(Fx(p.X), 2000, 16000) / 50) * 50;
        Invalidate();
        OnEdit?.Invoke();
    }

    protected override void OnHover(Vector2 p) => Host.SetCursor(Near(p) ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        DrawFrame(ds);
        float fd = p[P.DS_FREQ];
        bool on = p[P.DS_ON] >= 0.5f;
        Func<float, int, float> Shape(double g) => (f, _) =>
        {
            double u = (f / fd) * (f / fd);
            return (float)(20 * Math.Log10(Math.Max(1 - (1 - g) * (u / (1 + u)), 1e-6)));
        };
        Curve(ds, Shape(DspMath.Lin(-p[P.DS_RANGE])), Pal.Text, 1, dash: Dash4);
        double gl = on ? DspMath.Lin(-getLive().Ds) : 1;
        Curve(ds, Shape(gl), Pal.Accent, 1.75f, 0, Pal.GrSoft);
        Handle(ds, fd, on, $"{F.HzShort(fd)}Hz");
    }
}

// ── история уровня и подавления (гейт, компрессор, лимитер, вход/выход) ──
sealed class TimeGraph : GraphView
{
    const int N = 1024;
    const float PT = 8, PB = 18;
    readonly string mode;   // io | gate | comp | lim
    readonly Func<float[]> getParams;
    readonly int thrIdx = -1;
    readonly float[] hin = new float[N], hout = new float[N], hgr = new float[N];
    int head;
    float grNow;
    public Action OnEdit;

    public TimeGraph(string mode, Func<float[]> getParams)
    {
        this.mode = mode;
        this.getParams = getParams;
        Live = true;
        Array.Fill(hin, -120);
        Array.Fill(hout, -120);
        thrIdx = mode switch { "gate" => P.GATE_THRESH, "comp" => P.COMP_THRESH, "lim" => P.LIM_CEIL, _ => -1 };
    }

    float Pl => 30;
    float Pr => mode == "comp" ? 118 : 8;
    float Pw => W - Pl - Pr;
    float Ph => H - PT - PB;
    float Y(float db) => PT + Math.Clamp(-db, 0, 60) / 60 * Ph;

    public void Push(float inDb, float outDb, float gr)
    {
        head = (head + 1) % N;
        hin[head] = inDb;
        hout[head] = outDb;
        hgr[head] = gr;
        grNow = gr;
    }

    bool Near(Vector2 p) => thrIdx >= 0 && MathF.Abs(p.Y - Y(getParams()[thrIdx])) < 7;

    protected override bool OnDown(Vector2 p, bool shift) => Near(p);

    protected override void OnDrag(Vector2 p, bool shift)
    {
        float lo = mode == "lim" ? -24 : mode == "gate" ? -90 : -60;
        getParams()[thrIdx] = MathF.Round(Math.Clamp(-((p.Y - PT) / Ph) * 60, lo, 0) * 2) / 2;
        Invalidate();
        OnEdit?.Invoke();
    }

    protected override void OnHover(Vector2 p) => Host.SetCursor(Near(p) ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.Arrow);

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        float pl = Pl, pw = Pw, ph = Ph;
        if (pw < 4 || ph < 4) return;
        ds.FillRoundedRectangle(pl, PT, pw, ph, 3, 3, Pal.Bg);
        for (int d = -12; d > -60; d -= 12) HLine(ds, pl, pl + pw, Y(d), Pal.Grid);
        for (int d = 0; d >= -60; d -= 12) Text(ds, $"{d}", pl - 5, Math.Clamp(Y(d), PT + 5, PT + ph - 5), Pal.Faint, 1, 0);

        int cols = Math.Min(N - 1, (int)(pw / 2));
        float Xc(int i) => pl + pw - i * 2;
        float At(float[] a, int i) => a[(head - i + N) % N];
        var clip = new Windows.Foundation.Rect(pl, PT, pw, ph);
        using (ds.CreateLayer(1, clip))
        {
            // вход — серая заливка
            var poly = new Vector2[cols + 3];
            poly[0] = new Vector2(Xc(0), PT + ph);
            for (int i = 0; i <= cols; i++) poly[i + 1] = new Vector2(Xc(i), Y(At(hin, i)));
            poly[cols + 2] = new Vector2(Xc(cols), PT + ph);
            using (var g = Polygon(ds, poly)) ds.FillGeometry(g, Pal.Spec);
            // выход — линия
            var line = new Vector2[cols + 1];
            for (int i = 0; i <= cols; i++) line[i] = new Vector2(Xc(i), Y(At(hout, i)));
            using (var g = Polyline(ds, line)) ds.DrawGeometry(g, Pal.A(Pal.Bright, 0.7f));
            // подавление — свисает сверху
            if (mode != "io")
            {
                poly[0] = new Vector2(Xc(0), PT);
                for (int i = 0; i <= cols; i++) poly[i + 1] = line[i] = new Vector2(Xc(i), PT + Math.Clamp(At(hgr, i), 0, 60) / 60 * ph);
                poly[cols + 2] = new Vector2(Xc(cols), PT);
                using (var g = Polygon(ds, poly)) ds.FillGeometry(g, Pal.GrSoft);
                using (var g = Polyline(ds, line)) ds.DrawGeometry(g, Pal.Gr);
            }
        }

        // порог / потолок
        if (thrIdx >= 0)
        {
            float v = p[thrIdx];
            float y = MathF.Round(Y(v)) + 0.5f;
            int onIdx = mode == "gate" ? P.GATE_ON : mode == "comp" ? P.COMP_ON : P.LIM_ON;
            bool on = p[onIdx] >= 0.5f;
            var col = on ? Pal.Accent : Pal.Faint;
            ds.DrawLine(pl, y, pl + pw, y, col, 1, Dash5);
            Text(ds, $"{(mode == "lim" ? "CEIL" : "THR")} {v:0.0}", pl + 6, y - 3, col, -1, 1);
        }

        // характеристика компрессора справа
        if (mode == "comp")
        {
            float s = 96, bx = W - s - 8, by = PT;
            double t = p[P.COMP_THRESH], r = p[P.COMP_RATIO], k = p[P.COMP_KNEE];
            bool on = p[P.COMP_ON] >= 0.5f;
            float XX(double db) => bx + (float)((db + 60) / 60) * s;
            float YY(double db) => by + s - (float)((db + 60) / 60) * s;
            ds.FillRoundedRectangle(bx, by, s, s, 3, 3, Pal.Bg);
            ds.DrawLine(XX(-60), YY(-60), XX(0), YY(0), Pal.Grid);
            var pts = new Vector2[61];
            for (int i = 0; i <= 60; i++) { double x = -60 + i; pts[i] = new Vector2(XX(x), YY(DspMath.CompCurve(x, t, r, k))); }
            using (var g = Polyline(ds, pts)) ds.DrawGeometry(g, on ? Pal.Accent : Pal.Faint, 1.5f);
            if (on && grNow > 0.05f)
            {
                double a = t - k / 2, b = 0;
                for (int i = 0; i < 30; i++) { double m = (a + b) / 2; if (m - DspMath.CompCurve(m, t, r, k) < grNow) a = m; else b = m; }
                double x = (a + b) / 2;
                ds.FillCircle(XX(x), YY(DspMath.CompCurve(x, t, r, k)), 3.5f, Pal.Gr);
            }
            Text(ds, "IN → OUT", bx, by + s + 6, Pal.Faint, -1, -1);
            Text(ds, $"GR {(grNow > 0.05f ? "−" : "")}{grNow:0.0}", bx, by + s + 20, on && grNow > 0.05f ? Pal.Gr : Pal.Faint, -1, -1);
        }
    }
}

// ── индикаторы IN / OUT (сегменты) ──
sealed class MetersView : GraphView
{
    public sealed class M { public float Rms = -120, Pk = -120, Hold = -120; public long HoldT; public bool Clip; }
    public readonly M[] Ch = { new(), new() };
    long last = Environment.TickCount64;

    public MetersView()
    {
        Live = true;
        Host.PointerPressed += (_, _) => ResetClip();
    }

    public void Update(float inPk, float inRms, float outPk, float outRms)
    {
        long now = Environment.TickCount64;
        float dt = MathF.Min((now - last) / 1000f, 0.2f);
        last = now;
        (float pk, float rms)[] v = { (inPk, inRms), (outPk, outRms) };
        for (int i = 0; i < 2; i++)
        {
            var m = Ch[i];
            var (pk, rms) = v[i];
            m.Pk = pk > m.Pk ? pk : MathF.Max(pk, m.Pk - 26 * dt);
            m.Rms = rms > m.Rms ? m.Rms + (rms - m.Rms) * 0.55f : MathF.Max(rms, m.Rms - 20 * dt);
            if (pk >= m.Hold) { m.Hold = pk; m.HoldT = now + 1500; }
            else if (now > m.HoldT) m.Hold = MathF.Max(-120, m.Hold - 18 * dt);
            if (pk > -0.3f) m.Clip = true;
        }
    }

    public void ResetClip()
    {
        foreach (var m in Ch) m.Clip = false;
        Invalidate();
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        float top = 16, bottom = H - 34, hh = bottom - top;
        float bw = 12, gap = 18, x0 = (W - bw * 2 - gap) / 2;
        float Yd(float db) => top + Math.Clamp(-db, 0, 60) / 60 * hh;
        const float seg = 3, step = 4;
        for (int i = 0; i < 2; i++)
        {
            var m = Ch[i];
            float x = x0 + i * (bw + gap);
            ds.FillRoundedRectangle(x, 4, bw, 6, 1.5f, 1.5f, m.Clip ? Pal.Red : Pal.Off);
            for (float y = bottom - seg; y >= top; y -= step)
            {
                float db = -((y - top) / hh) * 60;
                float lit = db <= m.Pk ? (db <= m.Rms ? 1 : 0.45f) : 0;
                var col = db > -3 ? Pal.Red : db > -12 ? Pal.Yellow : Pal.Green;
                ds.FillRectangle(x, y, bw, seg, lit > 0 ? Pal.A(col, lit) : Pal.Off);
            }
            if (m.Hold > -60) ds.FillRectangle(x, Yd(m.Hold) - 1, bw, 2, m.Hold > -3 ? Pal.Red : Pal.Bright);
            Text(ds, i == 1 ? "OUT" : "IN", x + bw / 2, bottom + 4, Pal.Text, 0, -1);
            // ниже −10 дБ хватает целых: две колонки цифр должны помещаться под полосками
            string hv = m.Hold <= -90 ? "—" : m.Hold <= -10 ? $"{MathF.Round(m.Hold):0}" : $"{m.Hold:0.0}";
            Text(ds, hv, x + bw / 2, bottom + 18, m.Hold > -3 ? Pal.Red : Pal.Bright, 0, -1, 9.5f);
        }
        // шкала посередине
        foreach (int d in new[] { 0, -6, -12, -24, -36, -48 }) Text(ds, $"{-d}", x0 + bw + gap / 2, Yd(d), Pal.Faint, 0, 0, 9);
    }
}
