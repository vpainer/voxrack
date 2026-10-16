using System;

namespace Golos
{
    // Спектр для графика эквалайзера: БПФ 4096 с окном Ханна → 192 полосы в логарифмической
    // шкале 20 Гц – 20 кГц, с наклоном +3 дБ/окт (голос на верхах иначе выглядит пустым).
    sealed class Analyzer
    {
        public const int N = 4096;
        public const int Bins = 192;

        readonly float[] window = new float[N];
        readonly float[] re = new float[N], im = new float[N];
        readonly float[] cosT = new float[N / 2], sinT = new float[N / 2];
        readonly int[] rev = new int[N];
        float fsPrepared;
        readonly float[] lo = new float[Bins], hi = new float[Bins], center = new float[Bins], tilt = new float[Bins];

        public Analyzer()
        {
            for (int i = 0; i < N; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));
            for (int i = 0; i < N / 2; i++)
            {
                cosT[i] = MathF.Cos(2 * MathF.PI * i / N);
                sinT[i] = -MathF.Sin(2 * MathF.PI * i / N);
            }
            int bits = (int)Math.Log2(N);
            for (int i = 0; i < N; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                rev[i] = r;
            }
        }

        public static float BinFreq(int i) => 20f * MathF.Pow(1000f, i / (float)(Bins - 1));

        void Prepare(float fs)
        {
            fsPrepared = fs;
            float binHz = fs / N;
            for (int i = 0; i < Bins; i++)
            {
                float f = BinFreq(i);
                float fl = i == 0 ? f / 1.02f : MathF.Sqrt(f * BinFreq(i - 1));
                float fh = i == Bins - 1 ? f * 1.02f : MathF.Sqrt(f * BinFreq(i + 1));
                center[i] = f / binHz;
                lo[i] = fl / binHz;
                hi[i] = fh / binHz;
                tilt[i] = 3f * MathF.Log2(f / 1000f);
            }
        }

        public void Spectrum(float[] samples, float fs, float[] outDb)
        {
            if (fs != fsPrepared) Prepare(fs);
            for (int i = 0; i < N; i++)
            {
                re[rev[i]] = samples[i] * window[i];
                im[rev[i]] = 0;
            }
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1, step = N / size;
                for (int start = 0; start < N; start += size)
                    for (int k = 0; k < half; k++)
                    {
                        int a = start + k, b = a + half;
                        float wr = cosT[k * step], wi = sinT[k * step];
                        float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
            // |X|² → дБ относительно полной шкалы синуса: A = 4|X|/N для окна Ханна
            float norm = 16f / ((float)N * N);
            int maxBin = N / 2 - 1;
            for (int i = 0; i < Bins; i++)
            {
                int a = (int)MathF.Ceiling(lo[i]), b = (int)MathF.Floor(hi[i]);
                float pw;
                if (b >= a && a >= 1)
                {
                    pw = 0;
                    for (int k = a; k <= Math.Min(b, maxBin); k++)
                    {
                        float v = re[k] * re[k] + im[k] * im[k];
                        if (v > pw) pw = v;
                    }
                }
                else
                {
                    // полоса уже шага БПФ (низы): интерполируем между соседними отсчётами
                    float c = Math.Clamp(center[i], 1, maxBin - 1);
                    int k = (int)c;
                    float t = c - k;
                    float v0 = re[k] * re[k] + im[k] * im[k], v1 = re[k + 1] * re[k + 1] + im[k + 1] * im[k + 1];
                    pw = v0 + (v1 - v0) * t;
                }
                float db = 10f * MathF.Log10(pw * norm + 1e-14f) + tilt[i];
                outDb[i] = MathF.Round(Math.Max(db, -120f), 1);
            }
        }

        public static void Levels(float[] buf, int count, out float peakDb, out float rmsDb)
        {
            float pk = 0;
            double acc = 0;
            for (int i = 0; i < count; i++)
            {
                float v = buf[i];
                float a = MathF.Abs(v);
                if (a > pk) pk = a;
                acc += v * v;
            }
            peakDb = count > 0 ? 20f * MathF.Log10(pk + 1e-9f) : -120f;
            rmsDb = count > 0 ? 10f * MathF.Log10((float)(acc / count) + 1e-18f) : -120f;
        }
    }
}
