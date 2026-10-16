using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;

namespace Golos.Graphs;

// Эквалайзер: спектр + кривая всей цепочки (срез низа + полосы) + узлы, которые тянут мышью.
sealed class EqGraph : FreqGraph
{
    const float DB = 18;
    readonly Func<float[]> getParams;
    public Action OnEdit;
    public Action<int> OnSelect;
    public Action<string> OnHint;
    public int Sel = -1;
    int hover = -1;
    (int b, Vector2 p, float f, float g)? drag;
    float[] resp = Array.Empty<float>();

    public EqGraph(Func<float[]> getParams) : base(DB, -DB) => this.getParams = getParams;

    IEnumerable<int> ActiveBands()
    {
        var p = getParams();
        for (int b = 0; b < P.EQ_BANDS; b++) if (Params.Band(p, b).On) yield return b;
    }

    Vector2 NodePos(int b)
    {
        var x = Params.Band(getParams(), b);
        return new Vector2(X(x.Freq), Y(T.HasGain(x.Type) ? x.Gain : 0));
    }

    int Hit(Vector2 p)
    {
        int best = -1;
        float bd = 12 * 12;
        foreach (var b in ActiveBands())
        {
            float d = Vector2.DistanceSquared(NodePos(b), p);
            if (d < bd) { bd = d; best = b; }
        }
        return best;
    }

    public void Select(int b)
    {
        Sel = b;
        Invalidate();
        OnSelect?.Invoke(b);
    }

    public void Edit(int b, bool? on = null, int? type = null, float? freq = null, float? gain = null, float? q = null)
    {
        var p = getParams();
        int o = P.EQ_BASE + b * P.EQ_STRIDE;
        if (on != null) p[o] = on.Value ? 1 : 0;
        if (type != null) p[o + 1] = type.Value;
        if (freq != null) p[o + 2] = MathF.Round(Math.Clamp(freq.Value, 20, 20000));
        if (gain != null) p[o + 3] = MathF.Round(Math.Clamp(gain.Value, -DB, DB) * 10) / 10;
        if (q != null) p[o + 4] = MathF.Round(Math.Clamp(q.Value, 0.1f, 18) * 100) / 100;
        Invalidate();
        OnEdit?.Invoke();
    }

    public int AddAt(float f, float gain)
    {
        var p = getParams();
        int free = -1;
        for (int b = 0; b < P.EQ_BANDS; b++) if (!Params.Band(p, b).On) { free = b; break; }
        if (free < 0) { OnHint?.Invoke(L.S("Все 10 полос заняты", "All 10 bands are in use")); return -1; }
        int type = f < 40 ? T.LOW_CUT : f > 16000 ? T.HIGH_CUT : T.BELL;
        p[P.EQ_ON] = 1;
        Edit(free, on: true, type: type, freq: f, gain: T.HasGain(type) ? gain : 0, q: type == T.BELL ? 1 : 0.71f);
        Select(free);
        return free;
    }

    public void Remove(int b)
    {
        Edit(b, on: false);
        if (Sel == b) Select(-1);
    }

    protected override bool OnDown(Vector2 p, bool shift)
    {
        int b = Hit(p);
        if (b < 0) return false;
        Select(b);
        var x = Params.Band(getParams(), b);
        drag = (b, p, x.Freq, x.Gain);
        Host.SetCursor(InputSystemCursorShape.SizeAll);
        return true;
    }

    protected override void OnDrag(Vector2 p, bool shift)
    {
        if (drag is not var (b, p0, f0, g0)) return;
        float k = shift ? 0.15f : 1;
        var x = Params.Band(getParams(), b);
        Edit(b, freq: Fx(X(f0) + (p.X - p0.X) * k), gain: T.HasGain(x.Type) ? DbY(Y(g0) + (p.Y - p0.Y) * k) : null);
    }

    protected override void OnUp() => drag = null;

    protected override void OnHover(Vector2 p)
    {
        int h = Hit(p);
        if (h != hover) { hover = h; Invalidate(); }
        Host.SetCursor(h >= 0 ? InputSystemCursorShape.Hand : InputSystemCursorShape.Cross);
    }

    protected override void OnLeave()
    {
        if (hover != -1) { hover = -1; Invalidate(); }
    }

    protected override void OnDouble(Vector2 p)
    {
        int b = Hit(p);
        if (b >= 0) Remove(b);
        else if (p.X > PL && p.X < PL + PW && p.Y > PT && p.Y < PT + PlotH) AddAt(Fx(p.X), DbY(p.Y));
    }

    protected override void OnRight(Vector2 p)
    {
        int b = Hit(p);
        if (b >= 0) Remove(b);
    }

    protected override bool OnWheel(Vector2 p, int delta, bool shift)
    {
        int b = Hit(p);
        if (b < 0) b = Sel;
        if (b < 0) return false;
        var x = Params.Band(getParams(), b);
        Edit(b, q: x.Q * MathF.Pow(shift ? 1.02f : 1.12f, delta > 0 ? 1 : -1));
        return true;
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        DrawFrame(ds);
        bool eqOn = p[P.EQ_ON] >= 0.5f;

        if (Sel >= 0 && Params.Band(p, Sel).On && DspMath.BandCoefs(p, Sel, Fs) is DspMath.Coefs cf)
            Curve(ds, (f, _) => (float)DspMath.MagDb(cf, f, Fs), Pal.Accent, 0, 0, Pal.AccentSoft);
        if (resp.Length != Freqs.Length) resp = new float[Freqs.Length];
        DspMath.Chain(p, Freqs, Fs, resp);
        Curve(ds, (_, i) => resp[i], Pal.A(Pal.Accent, eqOn || p[P.HPF_ON] >= 0.5f ? 1 : 0.4f), 1.75f);

        foreach (var b in ActiveBands())
        {
            var n = NodePos(b);
            bool s = b == Sel, hv = b == hover;
            float a = eqOn ? 1 : 0.45f;
            float r = s || hv ? 8 : 7;
            ds.FillCircle(n, r, s ? Pal.A(Pal.Accent, a) : Pal.A(Pal.NodeBg, a));
            ds.DrawCircle(n, r, Pal.A(Pal.Accent, a), 1.25f);
            Text(ds, (b + 1).ToString(), n.X, n.Y, s ? Pal.NodeBg : Pal.A(Pal.Accent, a), 0, 0);
        }

        if (hover >= 0)
        {
            var x = Params.Band(p, hover);
            var n = NodePos(hover);
            string fs = x.Freq >= 1000 ? $"{x.Freq / 1000:0.00}k" : $"{MathF.Round(x.Freq):0}";
            string txt = T.HasGain(x.Type) ? $"{fs}Hz {(x.Gain > 0 ? "+" : "")}{x.Gain:0.0}dB Q{x.Q:0.00}" : $"{fs}Hz Q{x.Q:0.00}";
            float tw = TextWidth(ds, txt) + 12;
            float tx = Math.Clamp(n.X - tw / 2, PL + 2, W - tw - 4), ty = n.Y - 28;
            if (ty < PT + 2) ty = n.Y + 14;
            ds.FillRoundedRectangle(tx, ty, tw, 18, 3, 3, Pal.Panel);
            ds.DrawRoundedRectangle(tx + 0.5f, ty + 0.5f, tw - 1, 17, 3, 3, Pal.Frame);
            Text(ds, txt, tx + 6, ty + 9, Pal.Bright, -1, 0);
        }
    }
}
