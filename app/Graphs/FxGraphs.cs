using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace Golos.Graphs;

// Графики эффектов: автотюн (высота голоса по нотам), делей (где будут повторы), реверб (как гаснет зал).
static class Notes
{
    public static string Name(float m)
    {
        int r = (int)MathF.Round(m);
        return $"{Params.NoteNames[((r % 12) + 12) % 12]}{(int)MathF.Floor(r / 12f) - 1}";
    }
}

// ── автотюн ──
sealed class TuneGraph : GraphView
{
    const int TN = 900;
    const float PT = 8, PB = 18;
    readonly Func<float[]> getParams;
    readonly float[] inp = new float[TN], outp = new float[TN];
    int head;
    float center = 57;
    (float midi, float target, float hz) last;

    public TuneGraph(Func<float[]> getParams)
    {
        this.getParams = getParams;
        Live = true;
        Array.Fill(inp, float.NaN);
        Array.Fill(outp, float.NaN);
    }

    public void Push(float hz, float midi, float target, float shift)
    {
        head = (head + 1) % TN;
        bool voiced = hz > 0;
        inp[head] = voiced ? midi : float.NaN;
        outp[head] = voiced ? midi + shift : float.NaN;
        if (voiced)
        {
            center += (midi - center) * 0.03f;
            last = (midi, target, hz);
        }
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        float pl = 36, pr = 96, pw = W - pl - pr, ph = H - PT - PB;
        if (pw < 4 || ph < 4) return;
        const int span = 16;
        float lo = MathF.Round(center) - span / 2;
        float lane = ph / (span + 1);
        float Yn(float m) => PT + ph - (m - lo + 0.5f) * lane;
        int key = (((int)MathF.Round(p[P.TUNE_KEY]) % 12) + 12) % 12;
        int si = (int)MathF.Round(p[P.TUNE_SCALE]);
        int mask = si >= 0 && si < Params.Scales.Length ? Params.Scales[si] : 0xfff;
        bool on = p[P.TUNE_ON] >= 0.5f;
        ds.FillRoundedRectangle(pl, PT, pw, ph, 3, 3, Pal.Bg);
        for (float m = lo; m <= lo + span; m++)
        {
            int deg = ((((int)m - key) % 12) + 12) % 12;
            if (((mask >> deg) & 1) == 0) continue;
            float y = Yn(m);
            ds.FillRectangle(pl, y - lane / 2 + 1, pw, MathF.Max(1, lane - 2), deg == 0 ? Pal.A(Pal.Accent, 0.07f) : Pal.Lane);
            Text(ds, Notes.Name(m), pl - 5, y, deg == 0 ? Pal.Bright : Pal.Faint, 1, 0);
        }

        int cols = Math.Min(TN - 1, (int)(pw / 2));
        float Xc(int i) => pl + pw - i * 2;
        float At(float[] a, int i) => a[(head - i + TN) % TN];
        void Line(float[] arr, Color color, float width)
        {
            using var pb = new CanvasPathBuilder(ds);
            bool pen = false, any = false;
            for (int i = 0; i <= cols; i++)
            {
                float v = At(arr, i);
                if (float.IsNaN(v)) { if (pen) pb.EndFigure(CanvasFigureLoop.Open); pen = false; continue; }
                var pt = new Vector2(Xc(i), Yn(v));
                if (pen) pb.AddLine(pt);
                else { pb.BeginFigure(pt); pen = true; any = true; }
            }
            if (pen) pb.EndFigure(CanvasFigureLoop.Open);
            if (!any) return;
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, color, width);
        }
        using (ds.CreateLayer(1, new Windows.Foundation.Rect(pl, PT, pw, ph)))
        {
            Line(inp, Pal.Text, 1.25f);
            if (on) Line(outp, Pal.Accent, 2);
        }

        // справа: нота, к которой тянем, и что пришло
        float bx = W - pr + 14;
        var L = last;
        bool fresh = !float.IsNaN(At(inp, 0));
        Text(ds, on ? "TARGET" : "NOTE", bx, PT + 2, Pal.Faint, -1, -1);
        Text(ds, L.hz > 0 ? Notes.Name(on ? L.target : L.midi) : "—", bx, PT + 14, fresh ? (on ? Pal.Accent : Pal.Bright) : Pal.Faint, -1, -1, 26, true);
        int cents = (int)MathF.Round((L.midi - MathF.Round(L.midi)) * 100);
        Text(ds, "IN", bx, PT + 58, Pal.Faint, -1, -1);
        Text(ds, L.hz > 0 ? $"{Notes.Name(L.midi)} {(cents > 0 ? "+" : "")}{cents}c" : "—", bx, PT + 72, fresh ? Pal.Bright : Pal.Faint, -1, -1);
        Text(ds, "HZ", bx, PT + 96, Pal.Faint, -1, -1);
        Text(ds, L.hz > 0 ? $"{L.hz:0.0}" : "—", bx, PT + 110, fresh ? Pal.Bright : Pal.Faint, -1, -1);
    }
}

// ── делей ──
sealed class DelayGraph : GraphView
{
    const float PT = 8, PB = 18;
    readonly Func<float[]> getParams;

    public DelayGraph(Func<float[]> getParams) => this.getParams = getParams;

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        float pl = 30, pr = 8, pw = W - pl - pr, ph = H - PT - PB;
        if (pw < 4 || ph < 4) return;
        float T = MathF.Max(p[P.DLY_TIME], 10), fb = p[P.DLY_FB], mix = p[P.DLY_MIX];
        bool on = p[P.DLY_ON] >= 0.5f;
        const float span = 2400;
        float Xm(float ms) => pl + ms / span * pw;
        float Yd(float db) => PT + Math.Clamp(-db, 0, 48) / 48 * ph;
        ds.FillRoundedRectangle(pl, PT, pw, ph, 3, 3, Pal.Bg);
        for (float ms = 250; ms < span; ms += 250) VLine(ds, Xm(ms), PT, PT + ph, Pal.Grid);
        for (int d = -12; d > -48; d -= 12) HLine(ds, pl, pl + pw, Yd(d), Pal.Grid);
        for (float ms = 500; ms < span; ms += 500) Text(ds, $"{ms / 1000}s", Xm(ms), H - PB / 2 + 1, Pal.Faint, 0, 0);
        for (int d = 0; d >= -48; d -= 12) Text(ds, $"{d}", pl - 5, Math.Clamp(Yd(d), PT + 5, PT + ph - 5), Pal.Faint, 1, 0);

        ds.FillRectangle(Xm(0) + 1, Yd(0), 3, PT + ph - Yd(0), Pal.Text);
        float g = mix;
        var col = on ? Pal.Accent : Pal.Faint;
        for (int k = 1; k * T < span && g > 0.004f; k++)
        {
            float db = 20 * MathF.Log10(g);
            ds.FillRectangle(Xm(k * T) - 1.5f, Yd(db), 3, PT + ph - Yd(db), col);
            g *= fb * 0.85f;   // фильтры в петле съедают немного каждый круг
        }
        Text(ds, $"{MathF.Round(T)} ms  =  1/4 at {MathF.Round(60000 / T)} BPM", pl + 8, PT + 6, col, -1, -1);
    }
}

// ── реверб ──
sealed class ReverbGraph : GraphView
{
    const float PT = 8, PB = 18;
    readonly Func<float[]> getParams;

    public ReverbGraph(Func<float[]> getParams) => this.getParams = getParams;

    float Rt60()
    {
        float fb = 0.7f + Math.Clamp(getParams()[P.REV_SIZE], 0, 1) * 0.28f;
        return -3 * (1378f / 44100) / MathF.Log10(fb);   // средняя длина гребёнок Freeverb
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var p = getParams();
        float pl = 30, pr = 8, pw = W - pl - pr, ph = H - PT - PB;
        if (pw < 4 || ph < 4) return;
        bool on = p[P.REV_ON] >= 0.5f;
        float mix = MathF.Max(p[P.REV_MIX], 0.001f), pre = p[P.REV_PREDELAY] / 1000;
        float rt = Rt60(), damp = Math.Clamp(p[P.REV_DAMP], 0, 1);
        float span = MathF.Max(1.5f, MathF.Min(6, rt * 1.4f + pre));
        float Xs(float s) => pl + s / span * pw;
        float Yd(float db) => PT + Math.Clamp(-db, 0, 60) / 60 * ph;
        ds.FillRoundedRectangle(pl, PT, pw, ph, 3, 3, Pal.Bg);
        float step = span > 3 ? 1 : 0.5f;
        for (float s = step; s < span; s += step) VLine(ds, Xs(s), PT, PT + ph, Pal.Grid);
        for (int d = -12; d > -60; d -= 12) HLine(ds, pl, pl + pw, Yd(d), Pal.Grid);
        for (float s = step; s < span; s += step) Text(ds, $"{s}s", Xs(s), H - PB / 2 + 1, Pal.Faint, 0, 0);
        for (int d = 0; d >= -60; d -= 12) Text(ds, $"{d}", pl - 5, Math.Clamp(Yd(d), PT + 5, PT + ph - 5), Pal.Faint, 1, 0);

        float level0 = 20 * MathF.Log10(mix);
        CanvasGeometry Env(float rate)
        {
            var pts = new Vector2[123];
            pts[0] = new Vector2(Xs(pre), PT + ph);
            for (int i = 0; i <= 120; i++)
            {
                float s = pre + i / 120f * (span - pre);
                pts[i + 1] = new Vector2(Xs(s), Yd(level0 - 60 * (s - pre) / rate));
            }
            pts[122] = new Vector2(Xs(span), PT + ph);
            return Polygon(ds, pts);
        }
        using (ds.CreateLayer(1, new Windows.Foundation.Rect(pl, PT, pw, ph)))
        {
            using (var main = Env(rt))
            {
                ds.FillGeometry(main, on ? Pal.AccentSoft : Pal.Lane);
                ds.DrawGeometry(main, on ? Pal.Accent : Pal.Faint, 1.5f);
            }
            using (var hi = Env(rt * (1 - damp * 0.6f))) ds.DrawGeometry(hi, Pal.Text, 1, Dash4);
        }
        ds.FillRectangle(Xs(0) + 1, Yd(0), 3, PT + ph - Yd(0), Pal.Text);
        Text(ds, $"RT60 {rt:0.0} s   pre {MathF.Round(pre * 1000)} ms   dashed: highs", pl + 8, PT + 6, on ? Pal.Accent : Pal.Faint, -1, -1);
    }
}

// ── MATCH: средний спектр речи эталона и цели, что получится после подгонки, и поправка эквалайзера ──
sealed class MatchModel
{
    public string State = "idle";   // idle | rec | analyze | done | error
    public EqMatch.Result Result;
    public float Len;
    public int Max = 15;
    public string Error;
    public int Seconds = 15;
    public float Amount = 1;
    public bool Level = true, Eq = true, Dyn = true, Noise = true;
    public bool NotesShown;
}

sealed class MatchGraph : FreqGraph
{
    const float EqSpan = 12;   // шкала нижней полосы с поправкой, ±дБ
    readonly Func<MatchModel> getMatch;
    readonly Func<float> getLevel;

    public MatchGraph(Func<MatchModel> getMatch, Func<float> getLevel) : base(6, -54)
    {
        NoSpectrum = true;   // живой спектр тут только мешает сравнивать
        Live = false;
        this.getMatch = getMatch;
        this.getLevel = getLevel;
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var M = getMatch();
        var r = M.State == "rec" || M.State == "analyze" ? null : M.Result;
        // верх (70 %) — спектры, низ — поправка эквалайзера
        float split = MathF.Round(H * 0.7f);
        FrameH = split;
        DrawFrame(ds);
        float cx = PL + PW / 2, cy = PT + PlotH / 2;

        if (r == null)
        {
            if (M.State == "rec")
            {
                Text(ds, $"REC  {M.Len:0.0} / {M.Max} s", cx, cy - 16, Pal.Red, 0, 0, 22, true);
                Text(ds, L.S("Говори обычным голосом, оба микрофона на своих местах", "Speak normally, both microphones in their usual places"), cx, cy + 12, Pal.Text, 0, 0, 12, true);
                float lv = Math.Clamp((getLevel() + 60) / 60, 0, 1);
                ds.FillRectangle(cx - 120, cy + 32, 240, 4, Pal.Off);
                ds.FillRectangle(cx - 120, cy + 32, 240 * lv, 4, Pal.Accent);
            }
            else
            {
                string[] lines = M.State == "analyze"
                    ? new[] { L.S("Сравниваю спектры и подбираю полосы…", "Comparing spectra and fitting bands…") }
                    : M.Error != null
                        ? new[] { L.Msg(M.Error) }
                        : L.Ru ? new[] { "Выбери эталон внизу и нажми REC.", "Цель — микрофон в шапке: поменяются только её настройки.", "Поставь оба микрофона перед собой и говори 15 секунд." }
                            : new[] { "Pick a reference below and press REC.", "The target is the microphone in the title bar: only its settings change.", "Put both microphones in front of you and talk for 15 seconds." };
                for (int i = 0; i < lines.Length; i++)
                    Text(ds, lines[i], cx, cy - (lines.Length - 1) * 10 + i * 20, M.Error != null && M.State != "analyze" ? Pal.Red : Pal.Text, 0, 0, 12, true);
            }
        }
        else
        {
            void CurveArr(float[] arr, Color color, float width, CanvasStrokeStyle dash = null)
            {
                var pts = new Vector2[r.freqs.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(X(r.freqs[i]), Y(MathF.Max(DbBot - 6, arr[i])));
                using var layer = ds.CreateLayer(1, PlotRect);
                using var g = Polyline(ds, pts);
                if (dash != null) ds.DrawGeometry(g, color, width, dash);
                else ds.DrawGeometry(g, color, width);
            }
            CurveArr(r.tgtDb, Pal.Text, 1.25f);
            CurveArr(r.refDb, Pal.Accent, 2);
            CurveArr(r.afterDb, Pal.Bright, 1.25f, Dash5);

            float lx = PL + 8;
            foreach (var (t, col) in new[] { ("REF", Pal.Accent), ("TARGET", Pal.Text), ("AFTER", Pal.Bright) })
            {
                ds.FillRectangle(lx, PT + 9, 14, 2, col);
                Text(ds, t, lx + 18, PT + 4, col, -1, -1);
                lx += 30 + TextWidth(ds, t);
            }
            Text(ds, $"fit ±{r.fitDb:0.0} dB · {r.bands.Count} bands · {r.speechSec:0} s speech", lx + 10, PT + 4, Pal.Faint, -1, -1);
            if (r.speechSec < 3)
            {
                ds.FillRectangle(PL, PT + 24, PW, PlotH - 24, Pal.Veil);
                Text(ds, L.S($"Речи почти нет ({r.speechSec:0} с) — это подгонка под шум комнаты", $"Almost no speech ({r.speechSec:0} s) — this would match room noise"), PL + PW / 2, PT + PlotH / 2 - 8, Pal.Red, 0, 0, 14, true);
                Text(ds, L.S("Запиши ещё раз и говори все секунды записи", "Record again and keep talking the whole time"), PL + PW / 2, PT + PlotH / 2 + 14, Pal.Text, 0, 0, 12, true);
            }
        }

        FrameH = 0;
        DrawEqStrip(ds, r, split);
    }

    // нижняя полоса: поправка эквалайзера цели, ±12 дБ
    void DrawEqStrip(CanvasDrawingSession ds, EqMatch.Result r, float top)
    {
        float y0 = top + 2, h = H - y0 - 4;
        if (h < 8) return;
        float Ye(float db) => y0 + h / 2 - Math.Clamp(db, -EqSpan, EqSpan) / EqSpan * (h / 2 - 2);
        ds.FillRoundedRectangle(PL, y0, PW, h, 3, 3, Pal.Bg);
        HLine(ds, PL, PL + PW, Ye(0), Pal.Zero);
        Text(ds, "EQ", PL - 5, Ye(0), Pal.Faint, 1, 0);
        Text(ds, $"+{EqSpan}", PL - 5, y0 + 6, Pal.Faint, 1, 0);
        Text(ds, $"−{EqSpan}", PL - 5, y0 + h - 6, Pal.Faint, 1, 0);
        if (r == null) return;
        int n = r.freqs.Length;
        var poly = new Vector2[n + 2];
        var line = new Vector2[n];
        poly[0] = new Vector2(X(r.freqs[0]), Ye(0));
        for (int i = 0; i < n; i++) poly[i + 1] = line[i] = new Vector2(X(r.freqs[i]), Ye(r.eqDb[i]));
        poly[n + 1] = new Vector2(X(r.freqs[n - 1]), Ye(0));
        using var layer = ds.CreateLayer(1, new Windows.Foundation.Rect(PL, y0, PW, h));
        using (var g = Polygon(ds, poly)) ds.FillGeometry(g, Pal.GrSoft);
        using (var g = Polyline(ds, line)) ds.DrawGeometry(g, Pal.Gr, 1.5f);
    }
}

// ── MV7: схема сенсорной панели в текущем режиме подсветки ──
static class LedModes
{
    public sealed record Mode(string Id, string Label, string Title, bool Meter, bool Dim);
    public static readonly Mode[] All =
    {
        new("level", "LEVEL", L.S("Уровень: диоды показывают громкость голоса", "Level: LEDs show how loud you are"), true, false),
        new("night", "NIGHT", L.S("Ночь: уровень, но диоды приглушены", "Night: level, dimmed LEDs"), true, true),
        new("still", "STILL", L.S("Без уровня: диоды не бегают", "Still: no level meter on the LEDs"), false, false),
        new("quiet", "QUIET", L.S("Тихо: без уровня и приглушено — панель почти не видна", "Quiet: no meter and dimmed — the panel is barely visible"), false, true),
    };
    public static Mode Of(Mv7.State s) => All.FirstOrDefault(m => s != null && m.Meter == s.meter && m.Dim == s.dim) ?? All[0];
}

sealed class Mv7Graph : GraphView
{
    readonly Func<Mv7.State> getState;
    readonly Func<bool> getChecked;
    readonly Func<float> getLevel;

    public Mv7Graph(Func<Mv7.State> getState, Func<bool> getChecked, Func<float> getLevel)
    {
        this.getState = getState;
        this.getChecked = getChecked;
        this.getLevel = getLevel;
        Live = true;
    }

    protected override void Draw(CanvasDrawingSession ds)
    {
        var s = getState();
        ds.FillRoundedRectangle(4, 8, W - 8, H - 16, 3, 3, Pal.Bg);
        if (s == null || !s.connected)
        {
            Text(ds, getChecked() ? L.S("MV7 не отвечает: подключён ли он по USB? (MOTIV держит тот же канал)", "MV7 is not answering: is it on USB? (MOTIV holds the same channel)") : L.S("спрашиваю микрофон…", "asking the microphone…"), W / 2, H / 2, Pal.Faint, 0, 0, 13, true);
            return;
        }

        // корпус панели
        float pw = MathF.Min(420, W - 80), ph = 120;
        float x0 = (W - pw) / 2, y0 = (H - ph) / 2 - 10;
        ds.FillRoundedRectangle(x0, y0, pw, ph, 18, 18, Color.FromArgb(255, 22, 23, 26));
        ds.DrawRoundedRectangle(x0 + 0.5f, y0 + 0.5f, pw - 1, ph - 1, 18, 18, Pal.Frame);

        // диоды уровня: в режиме «уровень» горят по громкости, иначе тёмные
        const int n = 9;
        float seg = (pw - 120) / n, ly = y0 + ph / 2;
        float level = getLevel();   // дБFS пика входа
        int lit = s.meter ? Math.Clamp((int)MathF.Round((level + 54) / 54 * n), 0, n) : 0;
        float bright = s.dim ? 0.35f : 1;
        var dark = Color.FromArgb(255, 36, 39, 43);
        for (int i = 0; i < n; i++)
        {
            float x = x0 + 30 + i * seg;
            bool on = i < lit && !s.muted;
            var col = i >= n - 1 ? Pal.Red : i >= n - 3 ? Pal.Yellow : Pal.Green;
            if (on && !s.dim) ds.FillRoundedRectangle(x - 2, ly - 8, seg - 2, 16, 5, 5, Pal.A(col, 0.25f));
            ds.FillRoundedRectangle(x, ly - 5, seg - 6, 10, 3, 3, on ? Pal.A(col, bright) : dark);
        }

        // кнопка mute справа: красная, когда микрофон выключен касанием
        float mx = x0 + pw - 48;
        ds.FillCircle(mx, ly, 16, s.muted ? Pal.A(Pal.Red, bright) : dark);
        var mc = s.muted ? Pal.Red : Pal.Faint;
        using (var pb = new CanvasPathBuilder(ds))
        {
            pb.BeginFigure(mx - 4, ly - 8);
            pb.AddLine(mx - 4, ly + 2);
            pb.AddArc(new Vector2(mx + 4, ly + 2), 4, 4, 0, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            pb.AddLine(mx + 4, ly - 8);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, mc, 1.5f);
        }

        var mode = LedModes.Of(s);
        Text(ds, $"MODE {mode.Label}", x0, y0 + ph + 14, Pal.Accent, -1, -1);
        Text(ds, $"{(s.locked ? "TOUCH LOCKED" : "TOUCH ON")}   {(s.muted ? "MIC MUTED" : "MIC LIVE")}   FW {s.fw ?? "?"}", x0, y0 + ph + 30, Pal.Faint, -1, -1);
    }
}
