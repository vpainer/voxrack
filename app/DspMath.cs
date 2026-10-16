namespace Golos;

// АЧХ цепочки для графиков: те же формулы RBJ, что в apo/dsp.h.
static class DspMath
{
    public readonly record struct Coefs(double B0, double B1, double B2, double A1, double A2);

    public static Coefs Design(int type, double f, double gainDb, double q, double fs)
    {
        f = Math.Clamp(f, 10, fs * 0.45);
        q = Math.Clamp(q, 0.05, 30);
        double w0 = 2 * Math.PI * f / fs, cw = Math.Cos(w0), sw = Math.Sin(w0);
        double alpha = sw / (2 * q), A = Math.Pow(10, gainDb / 40), sA = 2 * Math.Sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case T.LOW_SHELF:
                b0 = A * ((A + 1) - (A - 1) * cw + sA); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - sA);
                a0 = (A + 1) + (A - 1) * cw + sA; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - sA;
                break;
            case T.HIGH_SHELF:
                b0 = A * ((A + 1) + (A - 1) * cw + sA); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - sA);
                a0 = (A + 1) - (A - 1) * cw + sA; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - sA;
                break;
            case T.LOW_CUT:
                b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case T.HIGH_CUT:
                b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case T.NOTCH:
                b0 = 1; b1 = -2 * cw; b2 = 1; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            default:
                b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
                break;
        }
        return new Coefs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public static double MagDb(in Coefs c, double f, double fs)
    {
        double w = 2 * Math.PI * f / fs, c1 = Math.Cos(w), c2 = Math.Cos(2 * w);
        double num = c.B0 * c.B0 + c.B1 * c.B1 + c.B2 * c.B2 + 2 * (c.B0 * c.B1 + c.B1 * c.B2) * c1 + 2 * c.B0 * c.B2 * c2;
        double den = 1 + c.A1 * c.A1 + c.A2 * c.A2 + 2 * (c.A1 + c.A1 * c.A2) * c1 + 2 * c.A2 * c2;
        return 10 * Math.Log10(Math.Max(num, 1e-20) / Math.Max(den, 1e-20));
    }

    public static Coefs? BandCoefs(float[] p, int b, double fs)
    {
        var x = Params.Band(p, b);
        if (!x.On || p[P.EQ_ON] < 0.5f) return null;
        if (T.HasGain(x.Type) && MathF.Abs(x.Gain) < 0.01f) return null;
        return Design(x.Type, x.Freq, T.HasGain(x.Type) ? x.Gain : 0, x.Q, fs);
    }

    public static List<Coefs> HpfCoefs(float[] p, double fs, bool force = false)
    {
        var list = new List<Coefs>();
        if (!force && p[P.HPF_ON] < 0.5f) return list;
        double f = Math.Clamp(p[P.HPF_FREQ], 20, 400);
        if (p[P.HPF_SLOPE] >= 18)
        {
            list.Add(Design(T.LOW_CUT, f, 0, 0.5411961, fs));
            list.Add(Design(T.LOW_CUT, f, 0, 1.306563, fs));
        }
        else list.Add(Design(T.LOW_CUT, f, 0, 0.7071068, fs));
        return list;
    }

    public static void Chain(float[] p, float[] freqs, double fs, float[] outDb)
    {
        var coefs = HpfCoefs(p, fs);
        for (int b = 0; b < P.EQ_BANDS; b++)
            if (BandCoefs(p, b, fs) is Coefs c) coefs.Add(c);
        for (int i = 0; i < freqs.Length; i++)
        {
            double s = 0;
            foreach (var c in coefs) s += MagDb(c, freqs[i], fs);
            outDb[i] = (float)s;
        }
    }

    // статическая характеристика компрессора: вход дБ → выход дБ (без мейкапа и микса)
    public static double CompCurve(double inDb, double t, double r, double w)
    {
        double over = inDb - t, gr;
        if (w > 0 && 2 * Math.Abs(over) <= w) { double k = over + w / 2; gr = (1 - 1 / r) * k * k / (2 * w); }
        else gr = over > 0 ? (1 - 1 / r) * over : 0;
        return inDb - gr;
    }

    // доли полос многополосного компрессора на частоте f: |LR4| = 1/(1+(f/fc)^4), сумма = 1
    public static void MbWeights(double f, double x1, double x2, double x3, Span<double> w)
    {
        static double Lp(double f, double fc) => 1 / (1 + Math.Pow(f / fc, 4));
        double l1 = Lp(f, x1), l2 = Lp(f, x2), l3 = Lp(f, x3);
        w[0] = l1;
        w[1] = (1 - l1) * l2;
        w[2] = (1 - l1) * (1 - l2) * l3;
        w[3] = (1 - l1) * (1 - l2) * (1 - l3);
    }

    public static double Lin(double db) => Math.Pow(10, db / 20);
}
