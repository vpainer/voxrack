using System;
using System.Collections.Generic;
using System.Linq;

namespace Golos
{
    // EQ Match: эталон и цель записывают одну и ту же речь. Сравниваем средний спектр речи (LTAS),
    // громкость, «плотность» (разброс громкости фраз) и шумовой фон, и подбираем цели:
    // усиление на входе, полосы эквалайзера, компрессор и гейт так, чтобы она звучала как эталон.
    static class EqMatch
    {
        public sealed class Band
        {
            public int type { get; set; }
            public float freq { get; set; }
            public float gain { get; set; }
            public float q { get; set; }
        }

        public sealed class Comp
        {
            public float thresh { get; set; }
            public float ratio { get; set; }
            public float makeup { get; set; }
        }

        public sealed class Gate
        {
            public float thresh { get; set; }
            public float range { get; set; }
        }

        public sealed class Result
        {
            public float[] freqs { get; set; }
            public float[] refDb { get; set; }
            public float[] tgtDb { get; set; }
            public float[] afterDb { get; set; }
            public float[] eqDb { get; set; }
            public List<Band> bands { get; set; }
            public float gainDb { get; set; }
            public float fitDb { get; set; }
            public Comp comp { get; set; }
            public Gate gate { get; set; }
            public float refLevel { get; set; }
            public float tgtLevel { get; set; }
            public float refSpread { get; set; }
            public float tgtSpread { get; set; }
            public float refNoise { get; set; }
            public float tgtNoise { get; set; }
            public float speechSec { get; set; }
            public float maxFreq { get; set; }
            public List<string> notes { get; set; } = new();
        }

        const int Grid = 120;
        const int MaxBands = 8;
        const float DesignFs = 48000;

        public static Result Analyze(float[] refSig, float refFs, float[] tgtSig, float tgtFs)
        {
            var res = new Result();
            var R = Levels(refSig, refFs);
            var T = Levels(tgtSig, tgtFs);
            res.speechSec = Math.Min(R.speechFrames, T.speechFrames) * 0.05f;
            if (res.speechSec < 3)
            {
                res.notes.Add("Мало речи в записи — запиши ещё раз и говори все 15 секунд.");
            }

            float fmax = Math.Min(16000f, 0.45f * Math.Min(refFs, tgtFs));
            res.maxFreq = fmax;
            if (fmax < 14000) res.notes.Add($"У одного из микрофонов звук только до {fmax / 1000:0.#} кГц — выше подгонять нечего.");
            var freqs = new float[Grid];
            for (int i = 0; i < Grid; i++) freqs[i] = 40f * MathF.Pow(fmax / 40f, i / (float)(Grid - 1));
            res.freqs = freqs;

            var refSpec = Smooth(Ltas(refSig, refFs, R.speechMask, freqs), freqs);
            var tgtSpec = Smooth(Ltas(tgtSig, tgtFs, T.speechMask, freqs), freqs);

            // разница формы: уровень вынесен в усиление, сравниваем в речевой середине 150 Гц – 5 кГц
            var diff = new double[Grid];
            double num = 0, den = 0;
            for (int i = 0; i < Grid; i++)
            {
                diff[i] = refSpec[i] - tgtSpec[i];
                if (freqs[i] >= 150 && freqs[i] <= 5000) { num += diff[i]; den++; }
            }
            double offset = den > 0 ? num / den : 0;
            var weight = new double[Grid];
            double tgtMax = tgtSpec.Max();
            for (int i = 0; i < Grid; i++)
            {
                diff[i] -= offset;
                weight[i] = freqs[i] < 80 || freqs[i] > 12000 ? 0.4 : 1.0;
                // там, где у цели почти ничего нет, подъём даст только шум: слабее вес и не больше +6
                if (tgtSpec[i] - tgtMax < -45) { weight[i] *= 0.2; diff[i] = Math.Min(diff[i], 6); }
                // самый низ не раздуваем: у дешёвых микрофонов он срезан, а подъём вытащит только гул
                if (freqs[i] < 100) diff[i] = Math.Min(diff[i], 8);
                diff[i] = Math.Clamp(diff[i], -15, 15);
            }

            var bands = Fit(diff, weight, freqs);
            var eq = Response(bands, freqs);
            double err = 0, wsum = 0;
            for (int i = 0; i < Grid; i++) { err += weight[i] * Math.Pow(diff[i] - eq[i], 2); wsum += weight[i]; }
            res.fitDb = (float)Math.Sqrt(err / Math.Max(wsum, 1e-9));
            res.bands = bands;

            // как эквалайзер меняет общую громкость речи цели (мощность, взвешенная шириной полосы)
            double p0 = 0, p1 = 0;
            for (int i = 0; i < Grid; i++)
            {
                double pw = Math.Pow(10, tgtSpec[i] / 10) * freqs[i];
                p0 += pw;
                p1 += pw * Math.Pow(10, eq[i] / 10);
            }
            double eqEffect = 10 * Math.Log10(p1 / Math.Max(p0, 1e-30));
            res.gainDb = (float)Math.Round(Math.Clamp(R.speech - T.speech - eqEffect, -24, 24), 1);
            if (Math.Abs(R.speech - T.speech - eqEffect) > 24) res.notes.Add("Микрофоны отличаются по громкости больше чем на 24 дБ — подтяни усиление цели вручную.");
            res.refLevel = (float)R.speech;
            res.tgtLevel = (float)T.speech;

            // для графика: обе кривые на одном уровне в речевой середине
            res.refDb = new float[Grid];
            res.tgtDb = new float[Grid];
            res.afterDb = new float[Grid];
            res.eqDb = new float[Grid];
            double top = Math.Max(refSpec.Max(), tgtSpec.Max() + offset);
            for (int i = 0; i < Grid; i++)
            {
                res.refDb[i] = (float)Math.Round(refSpec[i] - top, 2);
                res.tgtDb[i] = (float)Math.Round(tgtSpec[i] + offset - top, 2);
                res.afterDb[i] = (float)Math.Round(tgtSpec[i] + offset + eq[i] - top, 2);
                res.eqDb[i] = (float)Math.Round(eq[i], 2);
            }

            // плотность: разброс громкости фраз (p90 − p30); у цели больше — подтягиваем компрессором
            double gained = res.gainDb + eqEffect;
            res.refSpread = (float)(R.p90 - R.p30);
            res.tgtSpread = (float)(T.p90 - T.p30);
            if (res.tgtSpread - res.refSpread > 2 && res.tgtSpread > 4)
            {
                double thr = T.p50 + gained;
                double above = T.p90 + gained - thr;
                double reduce = res.tgtSpread - res.refSpread;
                double k = Math.Min(0.85, reduce / Math.Max(above, 1));
                double ratio = Math.Clamp(1 / (1 - k), 1.2, 8);
                double makeup = Math.Max(0, (T.p70 + gained - thr) * (1 - 1 / ratio)) * 0.8;
                res.comp = new Comp { thresh = (float)Math.Round(thr * 2) / 2, ratio = (float)Math.Round(ratio, 1), makeup = (float)Math.Round(makeup * 2) / 2 };
            }

            // шум между фразами: у цели заметно выше — гейт приглушит его до уровня эталона
            res.refNoise = (float)R.p10;
            res.tgtNoise = (float)(T.p10 + gained);
            if (res.tgtNoise - res.refNoise > 6 && (T.p20s - T.p10) > 12)
            {
                double speechLow = T.p20s + gained;
                res.gate = new Gate
                {
                    thresh = (float)Math.Round(res.tgtNoise + 0.35 * (speechLow - res.tgtNoise) + 3),
                    range = (float)Math.Round(Math.Clamp(res.tgtNoise - res.refNoise, 6, 40)),
                };
            }
            return res;
        }

        // ── громкость по кадрам 50 мс ──

        sealed class Lv
        {
            public bool[] speechMask;
            public int speechFrames;
            public double speech, p10, p30, p50, p70, p90, p20s;
        }

        static Lv Levels(float[] x, float fs)
        {
            int n = Math.Max(1, (int)(fs * 0.05f));
            int frames = x.Length / n;
            var db = new double[Math.Max(frames, 1)];
            for (int f = 0; f < frames; f++)
            {
                double acc = 0;
                for (int i = 0; i < n; i++) { double v = x[f * n + i]; acc += v * v; }
                db[f] = 10 * Math.Log10(acc / n + 1e-14);
            }
            var sorted = db.OrderBy(v => v).ToArray();
            double P(double[] a, double q) => a.Length == 0 ? -120 : a[Math.Clamp((int)(q * (a.Length - 1)), 0, a.Length - 1)];
            var lv = new Lv { p10 = P(sorted, 0.10) };
            double thr = Math.Max(lv.p10 + 10, P(sorted, 0.95) - 25);
            lv.speechMask = db.Select(v => v >= thr).ToArray();
            var sp = db.Where(v => v >= thr).OrderBy(v => v).ToArray();
            lv.speechFrames = sp.Length;
            lv.p30 = P(sp, 0.30);
            lv.p50 = P(sp, 0.50);
            lv.p70 = P(sp, 0.70);
            lv.p90 = P(sp, 0.90);
            lv.p20s = P(sp, 0.20);
            double pw = sp.Length > 0 ? sp.Average(v => Math.Pow(10, v / 10)) : 1e-12;
            lv.speech = 10 * Math.Log10(pw);
            return lv;
        }

        // ── средний спектр речи (Уэлч, окно Ханна, только кадры с речью) ──

        static double[] Ltas(float[] x, float fs, bool[] speech, float[] freqs)
        {
            int N = 1;
            while (N < fs * 0.08f) N <<= 1;
            var win = new double[N];
            for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            var acc = new double[N / 2];
            var re = new double[N];
            var im = new double[N];
            int frameLen = Math.Max(1, (int)(fs * 0.05f)), count = 0;
            for (int start = 0; start + N <= x.Length; start += N / 2)
            {
                int fr = (start + N / 2) / frameLen;
                if (fr >= speech.Length || !speech[fr]) continue;
                for (int i = 0; i < N; i++) { re[i] = x[start + i] * win[i]; im[i] = 0; }
                Fft(re, im);
                for (int k = 0; k < N / 2; k++) acc[k] += re[k] * re[k] + im[k] * im[k];
                count++;
            }
            var outDb = new double[freqs.Length];
            double binHz = fs / N;
            for (int g = 0; g < freqs.Length; g++)
            {
                double f0 = freqs[g] / Math.Pow(2, 1.0 / 12), f1 = freqs[g] * Math.Pow(2, 1.0 / 12);
                int a = Math.Max(1, (int)Math.Ceiling(f0 / binHz)), b = Math.Min(N / 2 - 1, (int)Math.Floor(f1 / binHz));
                double s = 0;
                int m = 0;
                for (int k = a; k <= b; k++) { s += acc[k]; m++; }
                if (m == 0) { int k = Math.Clamp((int)Math.Round(freqs[g] / binHz), 1, N / 2 - 1); s = acc[k]; m = 1; }
                outDb[g] = 10 * Math.Log10(s / m / Math.Max(count, 1) + 1e-20);
            }
            return outDb;
        }

        // сглаживание в 1/3 октавы — гармоники голоса не должны превращаться в полосы эквалайзера
        static double[] Smooth(double[] db, float[] freqs)
        {
            var o = new double[db.Length];
            double half = Math.Pow(2, 1.0 / 6);
            for (int i = 0; i < db.Length; i++)
            {
                double s = 0;
                int n = 0;
                for (int j = 0; j < db.Length; j++)
                    if (freqs[j] >= freqs[i] / half && freqs[j] <= freqs[i] * half) { s += db[j]; n++; }
                o[i] = s / Math.Max(n, 1);
            }
            return o;
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        // ── подбор полос: жадно ставим полосу в место самой большой ошибки, потом доводим все ──

        const int Bell = 0, LowShelf = 1, HighShelf = 2;

        static List<Band> Fit(double[] target, double[] w, float[] freqs)
        {
            var bands = new List<Band>();
            for (int k = 0; k < MaxBands; k++)
            {
                var resp = Response(bands, freqs);
                int best = -1;
                double bv = 0;
                for (int i = 0; i < freqs.Length; i++)
                {
                    double v = w[i] * Math.Abs(target[i] - resp[i]);
                    if (v > bv) { bv = v; best = i; }
                }
                double r = best >= 0 ? target[best] - resp[best] : 0;
                if (best < 0 || Math.Abs(r) < 0.75) break;
                float f = freqs[best];
                var band = new Band { freq = f, gain = (float)Math.Clamp(r, -12, 12), q = 1 };
                if (f < 90) { band.type = LowShelf; band.freq = Math.Max(60, f * 1.5f); band.q = 0.7f; }
                else if (f > 9000) { band.type = HighShelf; band.freq = Math.Min(14000, f / 1.5f); band.q = 0.7f; }
                else
                {
                    // ширина: где ошибка падает до половины — оттуда добротность
                    int L = best, Rr = best;
                    while (L > 0 && Math.Sign(target[L - 1] - resp[L - 1]) == Math.Sign(r) && Math.Abs(target[L - 1] - resp[L - 1]) > Math.Abs(r) / 2) L--;
                    while (Rr < freqs.Length - 1 && Math.Sign(target[Rr + 1] - resp[Rr + 1]) == Math.Sign(r) && Math.Abs(target[Rr + 1] - resp[Rr + 1]) > Math.Abs(r) / 2) Rr++;
                    double bw = Math.Max(0.15, Math.Log2(freqs[Rr] / freqs[L]));
                    double p2 = Math.Pow(2, bw);
                    band.q = (float)Math.Clamp(Math.Sqrt(p2) / (p2 - 1), 0.4, 6);
                }
                double before = Error(bands, target, w, freqs);
                bands.Add(band);
                Refine(bands, target, w, freqs, 25);
                // полоса почти ничего не дала — дальше только подгонка под шум
                if (Error(bands, target, w, freqs) > before * 0.97) { bands.RemoveAt(bands.Count - 1); break; }
            }
            Refine(bands, target, w, freqs, 60);
            Tidy(bands, target, w, freqs);
            return bands;
        }

        // склеиваем однотипные полосы рядом друг с другом и выкидываем почти пустые
        static void Tidy(List<Band> bands, double[] target, double[] w, float[] freqs)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < bands.Count && !changed; i++)
                    for (int j = i + 1; j < bands.Count && !changed; j++)
                    {
                        var a = bands[i];
                        var b = bands[j];
                        double oct = Math.Abs(Math.Log2(a.freq / b.freq));
                        if (a.type == b.type && oct < (a.type == Bell ? 0.33 : 0.6))
                        {
                            a.freq = (float)Math.Sqrt(a.freq * b.freq);
                            a.gain = (float)Math.Clamp(a.gain + b.gain, -12, 12);
                            bands.RemoveAt(j);
                            changed = true;
                        }
                    }
            }
            bands.RemoveAll(b => Math.Abs(b.gain) < 0.4f);
            Refine(bands, target, w, freqs, 40);
            bands.RemoveAll(b => Math.Abs(b.gain) < 0.4f);
            bands.Sort((x, y) => x.freq.CompareTo(y.freq));
        }

        static double Error(List<Band> bands, double[] target, double[] w, float[] freqs)
        {
            var resp = Response(bands, freqs);
            double e = 0;
            for (int i = 0; i < freqs.Length; i++) e += w[i] * Math.Pow(target[i] - resp[i], 2);
            // лёгкий штраф за резкие и громкие полосы — звучит естественнее
            // штраф за громкие и острые полосы: две огромные полосы, гасящие друг друга, хуже одной честной
            foreach (var b in bands) e += 0.12 * b.gain * b.gain + (b.q > 3 ? 2 * (b.q - 3) : 0);
            return e;
        }

        static void Refine(List<Band> bands, double[] target, double[] w, float[] freqs, int passes)
        {
            double sf = 0.06, sg = 0.4, sq = 0.15;
            double cur = Error(bands, target, w, freqs);
            for (int pass = 0; pass < passes; pass++)
            {
                bool improved = false;
                foreach (var b in bands)
                {
                    for (int param = 0; param < 3; param++)
                    {
                        if (param == 2 && b.type != Bell) continue;
                        foreach (int dir in new[] { 1, -1 })
                        {
                            float of = b.freq, og = b.gain, oq = b.q;
                            if (param == 0) b.freq = (float)Math.Clamp(b.freq * (1 + dir * sf), 50, 15000);
                            else if (param == 1) b.gain = (float)Math.Clamp(b.gain + dir * sg, -12, 12);
                            else b.q = (float)Math.Clamp(b.q * (1 + dir * sq), 0.3, 8);
                            double e = Error(bands, target, w, freqs);
                            if (e < cur - 1e-9) { cur = e; improved = true; break; }
                            b.freq = of; b.gain = og; b.q = oq;
                        }
                    }
                }
                if (!improved) { sf *= 0.5; sg *= 0.5; sq *= 0.5; if (sg < 0.02) break; }
            }
            foreach (var b in bands)
            {
                b.freq = MathF.Round(b.freq);
                b.gain = MathF.Round(b.gain * 10) / 10;
                b.q = MathF.Round(b.q * 100) / 100;
            }
        }

        static double[] Response(List<Band> bands, float[] freqs)
        {
            var r = new double[freqs.Length];
            foreach (var b in bands)
            {
                var c = Design(b.type, b.freq, b.gain, b.q, DesignFs);
                for (int i = 0; i < freqs.Length; i++) r[i] += MagDb(c, freqs[i], DesignFs);
            }
            return r;
        }

        // те же формулы RBJ, что в apo/dsp.h и ui/dsp.js (здесь — только колокол и полки)
        static (double b0, double b1, double b2, double a1, double a2) Design(int type, double f, double g, double q, double fs)
        {
            double w0 = 2 * Math.PI * f / fs, cw = Math.Cos(w0), sw = Math.Sin(w0);
            double alpha = sw / (2 * q), A = Math.Pow(10, g / 40), sA = 2 * Math.Sqrt(A) * alpha;
            double b0, b1, b2, a0, a1, a2;
            if (type == LowShelf)
            {
                b0 = A * ((A + 1) - (A - 1) * cw + sA); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - sA);
                a0 = (A + 1) + (A - 1) * cw + sA; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - sA;
            }
            else if (type == HighShelf)
            {
                b0 = A * ((A + 1) + (A - 1) * cw + sA); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - sA);
                a0 = (A + 1) - (A - 1) * cw + sA; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - sA;
            }
            else
            {
                b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
            }
            return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        static double MagDb((double b0, double b1, double b2, double a1, double a2) c, double f, double fs)
        {
            double w = 2 * Math.PI * f / fs, c1 = Math.Cos(w), c2 = Math.Cos(2 * w);
            double num = c.b0 * c.b0 + c.b1 * c.b1 + c.b2 * c.b2 + 2 * (c.b0 * c.b1 + c.b1 * c.b2) * c1 + 2 * c.b0 * c.b2 * c2;
            double den = 1 + c.a1 * c.a1 + c.a2 * c.a2 + 2 * (c.a1 + c.a1 * c.a2) * c1 + 2 * c.a2 * c2;
            return 10 * Math.Log10(Math.Max(num, 1e-20) / Math.Max(den, 1e-20));
        }
    }
}
