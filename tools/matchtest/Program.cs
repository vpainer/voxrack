// Проверка EQ Match на синтетике: «голос» → цель = голос через известный эквалайзер, тише и с шумом.
// Подгонка должна найти обратную поправку: громкость, полосы и гейт.
using System;
using System.Linq;
using Golos;

static class Program
{
    const float FS = 48000;

    static float[] Voice(int n, int seed)
    {
        var rnd = new Random(seed);
        var y = new float[n];
        double ph = 0, f0 = 120;
        var fr = new[] { 700.0, 1220, 2600, 3500 };
        var bw = new[] { 110.0, 120, 160, 250 };
        var gains = new[] { 1.0, 0.6, 0.3, 0.15 };
        var st = new double[4, 2];
        double lp = 0;
        for (int i = 0; i < n; i++)
        {
            // фразы: 1.2 с речи, 0.4 с паузы; высота плавает
            bool talk = (i / (int)(FS * 1.6)) % 1 == 0 && i % (int)(FS * 1.6) < FS * 1.2;
            f0 = 120 + 25 * Math.Sin(i / FS * 1.7);
            ph += f0 / FS;
            double e = 0;
            if (ph >= 1) { ph -= 1; e = talk ? 1 : 0; }
            e += talk ? (rnd.NextDouble() - 0.5) * 0.05 : 0;   // немного шума дыхания
            lp += (e - lp) * 0.3;
            double s = 0;
            for (int k = 0; k < 4; k++)
            {
                double r = Math.Exp(-Math.PI * bw[k] / FS);
                double a1 = 2 * r * Math.Cos(2 * Math.PI * fr[k] / FS), a2 = -r * r;
                double o = (1 - r) * gains[k] * lp + a1 * st[k, 0] + a2 * st[k, 1];
                st[k, 1] = st[k, 0];
                st[k, 0] = o;
                s += o;
            }
            double env = talk ? 0.6 + 0.4 * Math.Sin(i / FS * 9) : 0;   // громкость внутри фразы гуляет
            y[i] = (float)(s * env);
        }
        float peak = y.Max(v => Math.Abs(v));
        for (int i = 0; i < n; i++) y[i] *= 0.4f / peak;
        return y;
    }

    // RBJ-биквад для «порчи» цели
    static void Biquad(float[] x, int type, double f, double g, double q)
    {
        double w0 = 2 * Math.PI * f / FS, cw = Math.Cos(w0), sw = Math.Sin(w0), alpha = sw / (2 * q), A = Math.Pow(10, g / 40), sA = 2 * Math.Sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        if (type == 2) { b0 = A * ((A + 1) + (A - 1) * cw + sA); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - sA); a0 = (A + 1) - (A - 1) * cw + sA; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - sA; }
        else if (type == 3) { b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; }
        else { b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A; }
        double z1 = 0, z2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double y = b0 / a0 * x[i] + z1;
            z1 = b1 / a0 * x[i] - a1 / a0 * y + z2;
            z2 = b2 / a0 * x[i] - a2 / a0 * y;
            x[i] = (float)y;
        }
    }

    static int Main()
    {
        int n = (int)(FS * 15);
        var refSig = Voice(n, 1);
        var tgt = (float[])refSig.Clone();
        var rn = new Random(9);
        for (int i = 0; i < n; i++) refSig[i] += (float)((rn.NextDouble() - 0.5) * 0.0004);   // тихий фон и у эталона
        // «дешёвый микрофон»: бубнит на 300 Гц, нет верха, тише на 10 дБ и шумит
        Biquad(tgt, 0, 300, 6, 1.2);
        Biquad(tgt, 2, 5000, -8, 0.7);
        Biquad(tgt, 3, 150, 0, 0.7);
        var rnd = new Random(5);
        for (int i = 0; i < n; i++) tgt[i] = tgt[i] * 0.316f + (float)((rnd.NextDouble() - 0.5) * 0.0012);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = EqMatch.Analyze(refSig, FS, tgt, FS);
        Console.WriteLine($"analyze: {sw.ElapsedMilliseconds} ms, speech {r.speechSec:0.0} s, fit ±{r.fitDb:0.00} dB, gain {r.gainDb:+0.0;-0.0} dB");
        foreach (var b in r.bands) Console.WriteLine($"  band type {b.type} {b.freq,6:0} Hz {b.gain,6:+0.0;-0.0} dB  Q {b.q:0.00}");
        Console.WriteLine($"  comp: {(r.comp == null ? "—" : $"{r.comp.ratio}:1 @ {r.comp.thresh} dB, makeup {r.comp.makeup}")}");
        Console.WriteLine($"  gate: {(r.gate == null ? "—" : $"{r.gate.thresh} dB, range {r.gate.range} dB")}  noise ref {r.refNoise:0.0} tgt {r.tgtNoise:0.0}");
        double worst = 0;
        for (int i = 0; i < r.freqs.Length; i++)
            if (r.freqs[i] > 150 && r.freqs[i] < 12000) worst = Math.Max(worst, Math.Abs(r.afterDb[i] - r.refDb[i]));
        Console.WriteLine($"  max |after − ref| 150 Hz–12 kHz: {worst:0.00} dB");
        foreach (var note in r.notes) Console.WriteLine("  note: " + note);

        bool ok = r.fitDb < 1.2 && worst < 3 && Math.Abs(r.gainDb - 10) < 3;
        Console.WriteLine(ok ? "MATCH OK" : "MATCH FAILED");
        return ok ? 0 : 1;
    }
}
