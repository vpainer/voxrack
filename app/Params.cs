namespace Golos;

// Индексы параметров — те же, что в apo/params.h (и в ui/params.js прежнего окна). Менять только синхронно.
static class P
{
    public const int BYPASS = 0, IN_GAIN = 1, OUT_GAIN = 2,
        HPF_ON = 3, HPF_FREQ = 4, HPF_SLOPE = 5,
        GATE_ON = 6, GATE_THRESH = 7, GATE_RANGE = 8, GATE_ATTACK = 9, GATE_HOLD = 10, GATE_RELEASE = 11,
        EQ_ON = 12,
        COMP_ON = 13, COMP_THRESH = 14, COMP_RATIO = 15, COMP_KNEE = 16, COMP_ATTACK = 17, COMP_RELEASE = 18,
        COMP_MAKEUP = 19, COMP_MIX = 20, COMP_RMS = 21,
        DS_ON = 22, DS_FREQ = 23, DS_THRESH = 24, DS_RANGE = 25,
        LIM_ON = 26, LIM_CEIL = 27, LIM_RELEASE = 28,
        MB_ON = 29, TUNE_ON = 30, DLY_ON = 31,
        EQ_BASE = 32,
        REV_ON = 82,
        MB_X1 = 84, MB_X2 = 85, MB_X3 = 86,
        MB_BASE = 88,
        TUNE_KEY = 112, TUNE_SCALE = 113, TUNE_SPEED = 114, TUNE_AMOUNT = 115, TUNE_SHIFT = 116,
        DLY_TIME = 117, DLY_FB = 118, DLY_MIX = 119, DLY_TONE = 120,
        REV_SIZE = 121, REV_DAMP = 122, REV_MIX = 123, REV_PREDELAY = 124, REV_LOWCUT = 125,
        ORDER_A = 126, ORDER_B = 127;

    public const int COUNT = 128, EQ_BANDS = 10, EQ_STRIDE = 5, MB_BANDS = 4, MB_STRIDE = 6;
}

// типы полос эквалайзера (как в apo/dsp.h)
static class T
{
    public const int BELL = 0, LOW_SHELF = 1, HIGH_SHELF = 2, LOW_CUT = 3, HIGH_CUT = 4, NOTCH = 5;
    public static readonly string[] Labels = { "Bell", "Low shelf", "High shelf", "Low cut", "High cut", "Notch" };
    public static bool HasGain(int type) => type == BELL || type == LOW_SHELF || type == HIGH_SHELF;
}

record struct EqBand(bool On, int Type, float Freq, float Gain, float Q, int O);
record struct MbBand(bool On, float T, float R, float Att, float Rel, float Gain, int O);

static class Params
{
    // модули, которые можно переставлять (номера — как Stage в apo/params.h); I/O и LIMIT стоят на месте
    public static readonly string[] Stages = { "hpf", "gate", "tune", "eq", "mb", "comp", "ds", "dly", "rev" };
    public static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    public static readonly int[] Scales = { 0xfff, 0xab5, 0x5ad };   // все ноты, мажор, натуральный минор
    public static readonly string[] MbNames = { "LOW", "MID", "PRES", "AIR" };
    public static readonly string[] MbTitles = L.Ru
        ? new[] { "Низ: гул, бубнёж, близость", "Середина: тело голоса", "Присутствие: разборчивость, резкость", "Воздух: верх, дыхание" }
        : new[] { "Low: rumble, boominess, proximity", "Mid: body of the voice", "Presence: clarity, harshness", "Air: top end, breath" };

    public static EqBand Band(float[] p, int b)
    {
        int o = P.EQ_BASE + b * P.EQ_STRIDE;
        return new EqBand(p[o] >= 0.5f, (int)MathF.Round(p[o + 1]), p[o + 2], p[o + 3], p[o + 4], o);
    }

    public static MbBand Mb(float[] p, int b)
    {
        int o = P.MB_BASE + b * P.MB_STRIDE;
        return new MbBand(p[o] >= 0.5f, p[o + 1], p[o + 2], p[o + 3], p[o + 4], p[o + 5], o);
    }

    public static bool On(float[] p, int idx) => p[idx] >= 0.5f;

    public static int[] DecodeOrder(float[] p)
    {
        long a = (long)p[P.ORDER_A], b = (long)p[P.ORDER_B];
        var o = new int[Stages.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (int)((i < 6 ? a >> (4 * i) : b >> (4 * (i - 6))) & 15) - 1;
        bool ok = a > 0;
        for (int i = 0; ok && i < o.Length; i++) ok = o[i] >= 0 && o[i] < Stages.Length && Array.IndexOf(o, o[i]) == i;
        if (ok) return o;
        for (int i = 0; i < o.Length; i++) o[i] = i;
        return o;
    }

    public static void EncodeOrder(float[] p, int[] order)
    {
        long a = 0, b = 0;
        for (int i = 0; i < order.Length; i++)
        {
            if (i < 6) a += (long)(order[i] + 1) << (4 * i);
            else b += (long)(order[i] + 1) << (4 * (i - 6));
        }
        p[P.ORDER_A] = a;
        p[P.ORDER_B] = b;
    }

    static void MbDefaults(float[] p)
    {
        p[P.MB_X1] = 200; p[P.MB_X2] = 1500; p[P.MB_X3] = 6000;
        for (int b = 0; b < P.MB_BANDS; b++)
        {
            int o = P.MB_BASE + b * P.MB_STRIDE;
            p[o] = 1; p[o + 1] = -30; p[o + 2] = 2; p[o + 3] = 10; p[o + 4] = 150; p[o + 5] = 0;
        }
    }

    static void FxDefaults(float[] p)
    {
        p[P.TUNE_SPEED] = 20; p[P.TUNE_AMOUNT] = 1;
        p[P.DLY_TIME] = 320; p[P.DLY_FB] = 0.3f; p[P.DLY_MIX] = 0.2f; p[P.DLY_TONE] = 5000;
        p[P.REV_SIZE] = 0.55f; p[P.REV_DAMP] = 0.45f; p[P.REV_MIX] = 0.18f; p[P.REV_PREDELAY] = 20; p[P.REV_LOWCUT] = 250;
    }

    // настройки, сохранённые до появления новых модулей: там нули
    public static float[] Migrate(float[] p)
    {
        if (!(p[P.MB_X1] >= 20)) MbDefaults(p);
        if (!(p[P.DLY_TIME] >= 10)) FxDefaults(p);
        return p;
    }

    public static float[] Defaults()
    {
        var p = new float[P.COUNT];
        p[P.HPF_FREQ] = 80; p[P.HPF_SLOPE] = 12;
        p[P.GATE_THRESH] = -50; p[P.GATE_RANGE] = 20; p[P.GATE_ATTACK] = 2; p[P.GATE_HOLD] = 80; p[P.GATE_RELEASE] = 150;
        p[P.COMP_THRESH] = -24; p[P.COMP_RATIO] = 3; p[P.COMP_KNEE] = 6; p[P.COMP_ATTACK] = 8; p[P.COMP_RELEASE] = 120;
        p[P.COMP_MIX] = 1;
        p[P.DS_FREQ] = 6500; p[P.DS_THRESH] = -30; p[P.DS_RANGE] = 8;
        p[P.LIM_CEIL] = -1; p[P.LIM_RELEASE] = 60;
        for (int b = 0; b < P.EQ_BANDS; b++)
        {
            int o = P.EQ_BASE + b * P.EQ_STRIDE;
            p[o + 1] = T.BELL; p[o + 2] = 1000; p[o + 4] = 1;
        }
        MbDefaults(p);
        FxDefaults(p);
        return p;
    }

    static void SetMb(float[] p, float[] x, float[][] bands)
    {
        p[P.MB_ON] = 1;
        p[P.MB_X1] = x[0]; p[P.MB_X2] = x[1]; p[P.MB_X3] = x[2];
        for (int b = 0; b < bands.Length; b++)
        {
            int o = P.MB_BASE + b * P.MB_STRIDE;
            for (int k = 0; k < 6; k++) p[o + k] = bands[b][k];
        }
    }

    static void SetBands(float[] p, params float[][] list)
    {
        p[P.EQ_ON] = 1;
        for (int b = 0; b < list.Length; b++)
        {
            int o = P.EQ_BASE + b * P.EQ_STRIDE;
            p[o] = 1; p[o + 1] = list[b][0]; p[o + 2] = list[b][1]; p[o + 3] = list[b][2]; p[o + 4] = list[b][3];
        }
    }

    // Name — постоянное имя (оно в settings.json и в builtin-presets.json для плагина), показываем — на языке окна
    public sealed record Builtin(string Name, string Hint, string EnName, string EnHint, Func<float[]> Build)
    {
        public string Title => L.Ru ? Name : EnName;
        public string Subtitle => L.Ru ? Hint : EnHint;
    }

    public static string Custom => L.S("Пользовательский", "Custom");

    public static string Display(string name) =>
        Presets.FirstOrDefault(b => b.Name == name)?.Title ?? (name == "Своя настройка" ? Custom : name);

    // Стартовые пресеты под динамический MV7 у рта. Это точки отсчёта, а не истина: дальше — по ушам, на своём дубле.
    public static readonly Builtin[] Presets =
    {
        new("Без обработки", "всё выключено", "No processing", "everything off", Defaults),
        new("Подкаст", "ровно, плотно, без шипящих", "Podcast", "even, dense, no harsh esses", () =>
        {
            var p = Defaults();
            p[P.HPF_ON] = 1; p[P.HPF_FREQ] = 80; p[P.HPF_SLOPE] = 12;
            SetBands(p, new float[] { T.BELL, 220, -2.5f, 1.1f }, new float[] { T.BELL, 3200, 2.5f, 0.9f }, new float[] { T.HIGH_SHELF, 9000, 3, 0.7f });
            p[P.COMP_ON] = 1; p[P.COMP_THRESH] = -24; p[P.COMP_RATIO] = 3; p[P.COMP_KNEE] = 8;
            p[P.COMP_ATTACK] = 6; p[P.COMP_RELEASE] = 120; p[P.COMP_MAKEUP] = 7; p[P.COMP_RMS] = 1;
            p[P.DS_ON] = 1; p[P.DS_FREQ] = 6500; p[P.DS_THRESH] = -28; p[P.DS_RANGE] = 6;
            p[P.LIM_ON] = 1; p[P.LIM_CEIL] = -1; p[P.LIM_RELEASE] = 60;
            return p;
        }),
        new("Радио", "густой низ, сжатие по полосам", "Radio", "thick lows, multiband compression", () =>
        {
            var p = Defaults();
            p[P.HPF_ON] = 1; p[P.HPF_FREQ] = 70; p[P.HPF_SLOPE] = 24;
            SetBands(p, new float[] { T.LOW_SHELF, 120, 3, 0.7f }, new float[] { T.BELL, 350, -3, 1.2f }, new float[] { T.BELL, 4500, 3.5f, 1.0f }, new float[] { T.HIGH_SHELF, 10000, 2.5f, 0.7f });
            SetMb(p, new float[] { 180, 1200, 5500 }, new[]
            {
                new float[] { 1, -34, 3, 15, 180, 0 },
                new float[] { 1, -30, 1.8f, 10, 150, 0 },
                new float[] { 1, -36, 2.5f, 4, 90, 0 },
                new float[] { 1, -40, 2, 4, 90, 1.5f },
            });
            p[P.COMP_ON] = 1; p[P.COMP_THRESH] = -30; p[P.COMP_RATIO] = 5; p[P.COMP_KNEE] = 6;
            p[P.COMP_ATTACK] = 3; p[P.COMP_RELEASE] = 80; p[P.COMP_MAKEUP] = 12; p[P.COMP_RMS] = 1;
            p[P.DS_ON] = 1; p[P.DS_FREQ] = 7000; p[P.DS_THRESH] = -30; p[P.DS_RANGE] = 8;
            p[P.LIM_ON] = 1; p[P.LIM_CEIL] = -0.8f; p[P.LIM_RELEASE] = 40;
            return p;
        }),
        new("Стрим", "гейт глушит клавиатуру между фразами", "Stream", "gate mutes the keyboard between phrases", () =>
        {
            var p = Defaults();
            p[P.HPF_ON] = 1; p[P.HPF_FREQ] = 90; p[P.HPF_SLOPE] = 12;
            p[P.GATE_ON] = 1; p[P.GATE_THRESH] = -52; p[P.GATE_RANGE] = 18; p[P.GATE_ATTACK] = 2;
            p[P.GATE_HOLD] = 120; p[P.GATE_RELEASE] = 200;
            SetBands(p, new float[] { T.BELL, 250, -2, 1 }, new float[] { T.BELL, 3000, 2, 0.9f }, new float[] { T.HIGH_SHELF, 10000, 2, 0.7f });
            p[P.COMP_ON] = 1; p[P.COMP_THRESH] = -26; p[P.COMP_RATIO] = 3.5f; p[P.COMP_KNEE] = 6;
            p[P.COMP_ATTACK] = 8; p[P.COMP_RELEASE] = 140; p[P.COMP_MAKEUP] = 8; p[P.COMP_RMS] = 1;
            p[P.DS_ON] = 1; p[P.DS_FREQ] = 6500; p[P.DS_THRESH] = -30; p[P.DS_RANGE] = 6;
            p[P.LIM_ON] = 1; p[P.LIM_CEIL] = -1; p[P.LIM_RELEASE] = 60;
            return p;
        }),
        new("Созвоны", "разборчивость, без гула", "Calls", "clarity, no rumble", () =>
        {
            var p = Defaults();
            p[P.HPF_ON] = 1; p[P.HPF_FREQ] = 120; p[P.HPF_SLOPE] = 24;
            p[P.GATE_ON] = 1; p[P.GATE_THRESH] = -50; p[P.GATE_RANGE] = 12;
            SetBands(p, new float[] { T.BELL, 300, -3, 1 }, new float[] { T.BELL, 2500, 3, 1 }, new float[] { T.HIGH_CUT, 11000, 0, 0.707f });
            p[P.COMP_ON] = 1; p[P.COMP_THRESH] = -28; p[P.COMP_RATIO] = 4; p[P.COMP_KNEE] = 6;
            p[P.COMP_ATTACK] = 5; p[P.COMP_RELEASE] = 100; p[P.COMP_MAKEUP] = 9; p[P.COMP_RMS] = 1;
            p[P.LIM_ON] = 1; p[P.LIM_CEIL] = -1; p[P.LIM_RELEASE] = 60;
            return p;
        }),
    };

    public static bool Same(float[] a, float[] b)
    {
        for (int i = 0; i < P.COUNT; i++)
            if (i != P.BYPASS && MathF.Abs((i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0)) > 1e-3f) return false;
        return true;
    }
}

// подписи значений на ручках
static class F
{
    static string Sign(float v) => v > 0 ? "+" : v < 0 ? "−" : "";
    public static string Db(float v) => $"{Sign(v)}{MathF.Abs(v):0.0} dB";
    public static string Dbfs(float v) => $"{(v < 0 ? "−" : "")}{MathF.Abs(v):0.0} dB";
    public static string Hz(float v) => v >= 1000 ? (v / 1000).ToString(v >= 10000 ? "0.0" : "0.00") + " kHz" : $"{MathF.Round(v):0} Hz";
    public static string Ms(float v) => v >= 1000 ? $"{v / 1000:0.00} s" : v < 10 ? $"{v:0.0} ms" : $"{MathF.Round(v):0} ms";
    public static string Ratio(float v) => $"{v:0.0}:1";
    public static string Pct(float v) => $"{MathF.Round(v * 100):0}%";
    public static string Q(float v) => v.ToString("0.00");
    public static string HzShort(float f) => f >= 1000 ? (f / 1000).ToString(f >= 10000 ? "0.0" : "0.00") + "k" : $"{MathF.Round(f):0}";
}
