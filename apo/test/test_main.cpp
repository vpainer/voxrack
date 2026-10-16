// Проверки до того, как APO попадёт в audiodg: математика цепочки и COM-обвязка DLL.
// Сборка и запуск: build.sh test
#include "../apodefs.h"
#include "../dsp.h"
#include "../shared.h"
#include <stdio.h>
#include <stdlib.h>
#include <vector>
#include <algorithm>
#include <xmmintrin.h>

using namespace golos;

static int failures = 0;
#define CHECK(cond, ...)                                    \
    do {                                                    \
        if (!(cond)) {                                      \
            failures++;                                     \
            printf("  FAIL %s:%d: ", __FILE__, __LINE__);   \
            printf(__VA_ARGS__);                            \
            printf("\n");                                   \
        }                                                   \
    } while (0)

static const float FS = 48000;

static void neutral(float* p) {
    defaultParams(p);
}

// RMS установившейся синусоиды на выходе цепочки, дБFS
static float sineThrough(Engine& e, float freq, float ampDb, int ch = 1, float seconds = 1.0f) {
    const int n = (int)(FS * seconds);
    std::vector<float> buf(n * ch);
    const float a = dbToLin(ampDb);
    for (int i = 0; i < n; i++)
        for (int c = 0; c < ch; c++) buf[i * ch + c] = a * sinf(2 * 3.14159265f * freq * i / FS);
    for (int off = 0; off < n; off += 480) e.process(buf.data() + off * ch, (n - off) < 480 ? n - off : 480, nullptr);
    double acc = 0;
    const int from = n / 2;
    for (int i = from; i < n; i++) acc += (double)buf[i * ch] * buf[i * ch];
    return 10 * log10f((float)(acc / (n - from)) + 1e-20f) + 3.0103f;   // +3 дБ: RMS синуса → пик
}

static void testTransparent() {
    printf("transparent chain\n");
    Engine e;
    e.prepare(FS, 2);
    for (float f : {50.f, 1000.f, 10000.f}) {
        float out = sineThrough(e, f, -12, 2);
        CHECK(fabsf(out + 12) < 0.05f, "f=%.0f out=%.3f dB, ожидалось -12", f, out);
    }
}

static void testEq() {
    printf("eq bands\n");
    struct Case { int type; float f, g, q, probe, expect; };
    Case cases[] = {
        {EQ_BELL, 1000, 6, 1, 1000, 6},
        {EQ_BELL, 3000, -9, 2, 3000, -9},
        {EQ_LOW_SHELF, 200, 4, 0.707f, 40, 4},
        {EQ_HIGH_SHELF, 8000, -5, 0.707f, 18000, -5},
        {EQ_LOW_CUT, 120, 0, 0.707f, 120, -3.01f},
        {EQ_HIGH_CUT, 5000, 0, 0.707f, 5000, -3.01f},
        {EQ_NOTCH, 2000, 0, 4, 1000, -0.25f},
    };
    for (auto& c : cases) {
        Engine e;
        e.prepare(FS, 1);
        float p[P_COUNT];
        neutral(p);
        p[P_EQ_ON] = 1;
        float* b = p + P_EQ_BASE + 3 * kEqStride;
        b[0] = 1; b[1] = (float)c.type; b[2] = c.f; b[3] = c.g; b[4] = c.q;
        e.setParams(p, true);
        float out = sineThrough(e, c.probe, -20) + 20;
        CHECK(fabsf(out - c.expect) < 0.35f, "type %d f=%.0f: %.2f dB, ожидалось %.2f", c.type, c.f, out, c.expect);
    }
}

static void testHpf() {
    printf("high-pass\n");
    Engine e;
    e.prepare(FS, 1);
    float p[P_COUNT];
    neutral(p);
    p[P_HPF_ON] = 1; p[P_HPF_FREQ] = 100; p[P_HPF_SLOPE] = 24;
    e.setParams(p, true);
    float at = sineThrough(e, 100, -20) + 20, below = sineThrough(e, 50, -20) + 20, above = sineThrough(e, 1000, -20) + 20;
    CHECK(fabsf(at + 3.01f) < 0.3f, "на срезе %.2f дБ", at);
    CHECK(below < -20, "октавой ниже %.2f дБ (24 дБ/окт)", below);
    CHECK(fabsf(above) < 0.1f, "1 кГц %.2f дБ", above);
}

static void testCompressorCurve() {
    printf("compressor static curve\n");
    // порог -30, 4:1, колено 0, без мейкапа: вход -10 → -25, вход -40 → -40
    for (float inDb : {-40.f, -30.f, -20.f, -10.f}) {
        Engine e;
        e.prepare(FS, 1);
        float p[P_COUNT];
        neutral(p);
        p[P_COMP_ON] = 1; p[P_COMP_THRESH] = -30; p[P_COMP_RATIO] = 4; p[P_COMP_KNEE] = 0;
        p[P_COMP_ATTACK] = 1; p[P_COMP_RELEASE] = 2000; p[P_COMP_RMS] = 1;
        e.setParams(p, true);
        float out = sineThrough(e, 1000, inDb, 1, 2.f);
        // RMS-детектор видит синус на 3 дБ тише пика
        float det = inDb - 3.0103f;
        float expect = det > -30 ? inDb - (det + 30) * 0.75f : inDb;
        CHECK(fabsf(out - expect) < 0.6f, "вход %.0f: %.2f дБ, ожидалось %.2f", inDb, out, expect);
    }
    // параллельный микс 50 %: сухой и мокрый сигнал складываются
    Engine e;
    e.prepare(FS, 1);
    float p[P_COUNT];
    neutral(p);
    p[P_COMP_ON] = 1; p[P_COMP_THRESH] = -40; p[P_COMP_RATIO] = 30; p[P_COMP_KNEE] = 0; p[P_COMP_MIX] = 0.5f;
    p[P_COMP_RMS] = 1; p[P_COMP_RELEASE] = 2000;
    e.setParams(p, true);
    float out = sineThrough(e, 1000, -10, 1, 2.f);
    CHECK(out > -16.5f && out < -15.5f, "параллельный микс: %.2f дБ (≈ -6 от сухого)", out);
}

static void testLimiter() {
    printf("limiter ceiling\n");
    Engine e;
    e.prepare(FS, 2);
    float p[P_COUNT];
    neutral(p);
    p[P_LIM_ON] = 1; p[P_LIM_CEIL] = -3; p[P_OUT_GAIN] = 12;
    e.setParams(p, true);
    const float ceil = dbToLin(-3);
    srand(1);
    float worst = 0;
    std::vector<float> buf(480 * 2);
    for (int blk = 0; blk < 2000; blk++) {
        for (int i = 0; i < 480; i++) {
            float spike = (rand() % 997 == 0) ? 1.f : 0.f;
            float v = 0.3f * sinf(blk * 480 + i) + spike * ((rand() & 1) ? 1 : -1);
            buf[i * 2] = v;
            buf[i * 2 + 1] = -v * 0.8f;
        }
        e.process(buf.data(), 480, nullptr);
        for (float v : buf) if (fabsf(v) > worst) worst = fabsf(v);
    }
    CHECK(worst <= ceil * 1.0001f, "пик %.4f > потолка %.4f", worst, ceil);
    printf("  peak %.4f (ceil %.4f), latency %d frames\n", worst, ceil, e.latencyFrames());
}

static void testGate() {
    printf("gate\n");
    Engine e;
    e.prepare(FS, 1);
    float p[P_COUNT];
    neutral(p);
    p[P_GATE_ON] = 1; p[P_GATE_THRESH] = -40; p[P_GATE_RANGE] = 30; p[P_GATE_HOLD] = 20; p[P_GATE_RELEASE] = 50;
    e.setParams(p, true);
    float quiet = sineThrough(e, 500, -55, 1, 1.f);
    float loud = sineThrough(e, 500, -20, 1, 1.f);
    CHECK(fabsf(quiet - (-85)) < 1.0f, "тихий фон: %.2f дБ, ожидалось -85", quiet);
    CHECK(fabsf(loud + 20) < 0.1f, "голос: %.2f дБ", loud);
}

static void testDeesser() {
    printf("de-esser\n");
    float p[P_COUNT];
    neutral(p);
    p[P_DS_ON] = 1; p[P_DS_FREQ] = 6000; p[P_DS_THRESH] = -30; p[P_DS_RANGE] = 10;
    Engine e;
    e.prepare(FS, 1);
    e.setParams(p, true);
    float ess = sineThrough(e, 9000, -10) + 10;
    Engine e2;
    e2.prepare(FS, 1);
    e2.setParams(p, true);
    float voice = sineThrough(e2, 300, -10) + 10;
    CHECK(ess < -8.f && ess > -12.f, "шипящая 9 кГц: %.2f дБ (ожидалось -10 ± фаза кроссовера)", ess);
    CHECK(fabsf(voice) < 0.3f, "300 Гц: %.2f дБ (не трогаем)", voice);
}

static void mbSet(float* p, int b, float on, float t, float r, float gain) {
    float* m = p + P_MB_BASE + b * kMbStride;
    m[0] = on; m[1] = t; m[2] = r; m[3] = 1; m[4] = 2000; m[5] = gain;
}

static void testMultiband() {
    printf("multiband\n");
    float p[P_COUNT];
    // без сжатия и усиления сумма полос — всепропускающий фильтр: АЧХ ровная
    neutral(p);
    p[P_MB_ON] = 1;
    for (int b = 0; b < kMbBands; b++) mbSet(p, b, 1, 0, 1, 0);
    for (float f : {40.f, 200.f, 700.f, 1500.f, 3000.f, 6000.f, 12000.f, 18000.f}) {
        Engine e;
        e.prepare(FS, 2);
        e.setParams(p, true);
        float out = sineThrough(e, f, -12, 2) + 12;
        CHECK(fabsf(out) < 0.05f, "ровная сумма полос: %.0f Гц → %.3f дБ", f, out);
    }
    // сжимаем только низ: 100 Гц прижимается, 3 кГц не трогаем
    neutral(p);
    p[P_MB_ON] = 1;
    mbSet(p, 0, 1, -40, 4, 0);
    for (int b = 1; b < kMbBands; b++) mbSet(p, b, 0, 0, 1, 0);
    {
        Engine e; e.prepare(FS, 1); e.setParams(p, true);
        float low = sineThrough(e, 60, -10, 1, 2.f);   // 60 Гц: соседняя полоса почти не подмешивается
        Engine e2; e2.prepare(FS, 1); e2.setParams(p, true);
        float mid = sineThrough(e2, 3000, -10, 1, 2.f);
        // уровень в полосе (RMS) = -13, над порогом 27 дБ, 4:1 → снимаем ~20 дБ
        CHECK(low < -28 && low > -32, "низ под компрессией: %.2f дБ (≈ -30)", low);
        CHECK(fabsf(mid + 10) < 0.1f, "3 кГц без изменений: %.2f дБ", mid);
        printf("  low band: 60 Hz -10 → %.1f dB, 3 kHz → %.1f dB, GR %.1f\n", low, mid, e.mbGrDb[0]);
    }
    // усиление полосы «воздух»
    neutral(p);
    p[P_MB_ON] = 1;
    for (int b = 0; b < kMbBands; b++) mbSet(p, b, 1, 0, 1, b == 3 ? 6 : 0);
    {
        Engine e; e.prepare(FS, 1); e.setParams(p, true);
        float air = sineThrough(e, 14000, -20) + 20;
        Engine e2; e2.prepare(FS, 1); e2.setParams(p, true);
        float body = sineThrough(e2, 300, -20) + 20;
        CHECK(fabsf(air - 6) < 0.2f, "воздух +6: %.2f дБ", air);
        CHECK(fabsf(body) < 0.1f, "300 Гц: %.2f дБ", body);
    }
}

// основной тон по нормированной разностной функции (как YIN): работает и для пилы, и для голоса
static float acPitch(const float* x, int n) {
    const int W = 2048, tmin = 40, tmax = 900;
    if (n < W + tmax) return 0;
    static float d[1000];
    float cum = 0;
    for (int tau = 1; tau <= tmax; tau++) {
        double s = 0;
        for (int j = 0; j < W; j++) { const float v = x[j] - x[j + tau]; s += v * v; }
        cum += (float)s;
        d[tau] = cum > 0 ? (float)s * tau / cum : 1;
    }
    for (int tau = tmin; tau < tmax; tau++)
        if (d[tau] < 0.2f) {
            while (tau + 1 < tmax && d[tau + 1] < d[tau]) tau++;
            const float a = d[tau - 1], b = d[tau], c = d[tau + 1];
            const float den = a - 2 * b + c;
            return FS / (tau + (den > 1e-9f ? 0.5f * (a - c) / den : 0));
        }
    return 0;
}

// грубая модель голоса: импульсы связок через три форманты (≈ «а»)
static void voice(std::vector<float>& v, float f0, float amp) {
    struct R { float a1, a2, g; float y1 = 0, y2 = 0; };
    R rs[3];
    const float fr[3] = {700, 1220, 2600}, bw[3] = {110, 120, 160};
    for (int k = 0; k < 3; k++) {
        const float r = expf(-3.14159265f * bw[k] / FS);
        rs[k].a1 = 2 * r * cosf(2 * 3.14159265f * fr[k] / FS);
        rs[k].a2 = -r * r;
        rs[k].g = (1 - r) * (k == 0 ? 1.f : k == 1 ? 0.6f : 0.25f);
    }
    float ph = 0, lp = 0, peak = 0;
    for (auto& s : v) {
        ph += f0 / FS;
        float e = 0;
        if (ph >= 1) { ph -= 1; e = 1; }
        lp += (e - lp) * 0.3f;
        float y = 0;
        for (auto& q : rs) { const float o = q.g * lp + q.a1 * q.y1 + q.a2 * q.y2; q.y2 = q.y1; q.y1 = o; y += o; }
        s = y;
        peak = fmaxf(peak, fabsf(y));
    }
    for (auto& s : v) s *= amp / peak;
}

static void saw(std::vector<float>& v, float f, float amp) {
    float ph = 0;
    for (auto& s : v) { ph += f / FS; ph -= floorf(ph); s = amp * (2 * ph - 1); }
}

static std::vector<float> runMono(Engine& e, const std::vector<float>& in) {
    std::vector<float> out(in);
    for (size_t off = 0; off < out.size(); off += 480) e.process(out.data() + off, (int)std::min<size_t>(480, out.size() - off), nullptr);
    return out;
}

static float rmsDb(const float* x, int n) {
    double a = 0;
    for (int i = 0; i < n; i++) a += (double)x[i] * x[i];
    return 10 * log10f((float)(a / n) + 1e-20f);
}

static void testTune() {
    printf("autotune\n");
    const int n = (int)(FS * 2);
    std::vector<float> in(n);
    float p[P_COUNT];
    struct Case { float f, shift, amount; int scale; float expect; const char* what; };
    Case cases[] = {
        {450, 0, 1, 0, 440.0f, "450 Гц → ближайшая нота A4"},
        {300, 0, 1, 0, 293.66f, "300 Гц → D4"},
        {118, 0, 1, 0, 116.54f, "118 Гц (мужской голос) → A#2"},
        {220, 12, 0, 0, 440.0f, "220 Гц +12 полутонов"},
        {196, -12, 0, 0, 98.0f, "196 Гц −12 полутонов"},
        {150, 7, 0, 0, 224.7f, "150 Гц +7 (квинта)"},
        {415, 0, 1, 1, 392.0f, "G#4 в до мажоре → G4 (ближе из гаммы)"},
    };
    for (auto& c : cases) {
        saw(in, c.f, 0.3f);
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_TUNE_ON] = 1; p[P_TUNE_SPEED] = 0; p[P_TUNE_AMOUNT] = c.amount; p[P_TUNE_SHIFT] = c.shift; p[P_TUNE_SCALE] = (float)c.scale;
        e.setParams(p, true);
        auto out = runMono(e, in);
        const float f = acPitch(out.data() + n / 2, n / 2);
        const float level = rmsDb(out.data() + n / 2, n / 2) - rmsDb(in.data() + n / 2, n / 2);
        CHECK(fabsf(f - c.expect) < c.expect * 0.012f, "%s: %.1f Гц, ожидалось %.1f", c.what, f, c.expect);
        CHECK(fabsf(level) < (c.amount > 0 ? 1.5f : 8.f), "%s: уровень %.2f дБ", c.what, level);   // пила при сдвиге теряет основной тон
        printf("  %-40s → %6.1f Hz (detected %.1f), level %+.2f dB\n", c.what, f, e.tune.meterHz, level);
    }
    // на «голосе» сдвиг сохраняет громкость (форманты остаются на месте)
    for (float sh : {-12.f, -5.f, 7.f, 12.f}) {
        voice(in, 130, 0.5f);
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_TUNE_ON] = 1; p[P_TUNE_AMOUNT] = 0; p[P_TUNE_SHIFT] = sh;
        e.setParams(p, true);
        auto out = runMono(e, in);
        const float f = acPitch(out.data() + n / 2, n / 2), want = 130 * exp2f(sh / 12);
        const float level = rmsDb(out.data() + n / 2, n / 2) - rmsDb(in.data() + n / 2, n / 2);
        CHECK(fabsf(f - want) < want * 0.015f && fabsf(level) < 3.5f, "голос 130 Гц %+.0f: %.1f Гц (надо %.1f), уровень %.2f дБ", sh, f, want, level);
        printf("  voice 130 Hz %+3.0f st → %6.1f Hz, level %+.2f dB\n", sh, f, level);
    }

    // шум (не голос) без сдвига проходит как есть, только с задержкой
    {
        srand(3);
        for (int i = 0; i < n; i++) in[i] = ((rand() % 2001) - 1000) / 4000.f;
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_TUNE_ON] = 1;
        e.setParams(p, true);
        auto out = runMono(e, in);
        float level = rmsDb(out.data() + n / 2, n / 2) - rmsDb(in.data() + n / 2, n / 2);
        CHECK(fabsf(level) < 1.0f, "шум через автотюн: %.2f дБ", level);
    }
    // «голос»: пила с вибрато — без NaN и провалов громкости
    {
        float ph = 0;
        for (int i = 0; i < n; i++) {
            const float f = 140 * exp2f(0.3f / 12 * sinf(2 * 3.14159265f * 5 * i / FS));
            ph += f / FS;
            ph -= floorf(ph);
            in[i] = 0.25f * (2 * ph - 1);
        }
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_TUNE_ON] = 1; p[P_TUNE_SPEED] = 30; p[P_TUNE_SCALE] = 1;
        e.setParams(p, true);
        auto out = runMono(e, in);
        bool bad = false;
        for (float v : out) if (!(v - v == 0)) bad = true;
        float level = rmsDb(out.data() + n / 2, n / 2) - rmsDb(in.data() + n / 2, n / 2);
        CHECK(!bad && fabsf(level) < 2.f, "вибрато: NaN=%d, уровень %.2f дБ", bad ? 1 : 0, level);
    }
}

static void testFx() {
    printf("delay / reverb\n");
    float p[P_COUNT];
    const int n = (int)(FS * 3);
    const int t0 = (int)(FS * 0.6f);
    std::vector<float> in(n, 0.f);
    in[t0] = 1.f;
    {
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_DLY_ON] = 1; p[P_DLY_TIME] = 250; p[P_DLY_FB] = 0.5f; p[P_DLY_MIX] = 1;
        e.setParams(p, true);
        auto out = runMono(e, in);
        const int lim = e.latencyFrames();
        float pk1 = 0, pk2 = 0;
        for (int i = -40; i < 40; i++) {
            pk1 = fmaxf(pk1, fabsf(out[t0 + lim + 12000 + i]));
            pk2 = fmaxf(pk2, fabsf(out[t0 + lim + 24000 + i]));
        }
        CHECK(fabsf(pk1 - 1) < 0.05f, "первое эхо %.3f, ожидалось 1", pk1);
        CHECK(pk2 > 0.1f && pk2 < 0.5f, "второе эхо %.3f (обратная связь 0.5 через фильтры)", pk2);
        printf("  delay 250 ms fb 0.5: echo1 %.3f, echo2 %.3f\n", pk1, pk2);
    }
    {
        Engine e;
        e.prepare(FS, 1);
        neutral(p);
        p[P_REV_ON] = 1; p[P_REV_MIX] = 1; p[P_REV_SIZE] = 0.6f; p[P_REV_PREDELAY] = 10;
        e.setParams(p, true);
        auto out = runMono(e, in);
        const int a = t0 + (int)(FS * 0.05f), b = t0 + (int)(FS * 0.4f), c = t0 + (int)(FS * 1.4f);
        const float early = rmsDb(out.data() + a, b - a), late = rmsDb(out.data() + b, c - b);
        bool bad = false;
        for (float v : out) if (!(v - v == 0)) bad = true;
        CHECK(!bad && early > -60 && late < early - 6, "хвост реверба: ранний %.1f дБ, поздний %.1f дБ", early, late);
        printf("  reverb tail: 50-400 ms %.1f dB, 0.4-1.4 s %.1f dB\n", early, late);
    }
}

static void testOrder() {
    printf("chain order\n");
    int order[kStages] = {ST_COMP, ST_EQ, ST_HPF, ST_REV, ST_GATE, ST_DLY, ST_TUNE, ST_MB, ST_DS}, back[kStages];
    float a, b;
    encodeOrder(order, &a, &b);
    decodeOrder(a, b, back);
    bool same = true;
    for (int i = 0; i < kStages; i++) same &= back[i] == order[i];
    CHECK(same && a < 16777216.f, "порядок не пережил запись/чтение");
    int dup[kStages] = {0, 0, 2, 3, 4, 5, 6, 7, 8};
    encodeOrder(dup, &a, &b);
    decodeOrder(a, b, back);
    CHECK(back[0] == 0 && back[1] == 1 && back[8] == 8, "битый порядок должен стать порядком по умолчанию");
    decodeOrder(0, 0, back);
    CHECK(back[3] == ST_EQ, "ноль — порядок по умолчанию");

    // EQ перед компрессором и после него — разный результат
    float out[2];
    for (int k = 0; k < 2; k++) {
        float p[P_COUNT];
        neutral(p);
        p[P_EQ_ON] = 1;
        float* e = p + P_EQ_BASE;
        e[0] = 1; e[1] = EQ_BELL; e[2] = 1000; e[3] = 12; e[4] = 1;
        p[P_COMP_ON] = 1; p[P_COMP_THRESH] = -20; p[P_COMP_RATIO] = 10; p[P_COMP_KNEE] = 0; p[P_COMP_RMS] = 1;
        p[P_COMP_RELEASE] = 2000;
        int o[kStages] = {0, 1, 2, 3, 4, 5, 6, 7, 8};
        if (k == 1) { o[3] = ST_COMP; o[5] = ST_EQ; }
        encodeOrder(o, &p[P_ORDER_A], &p[P_ORDER_B]);
        Engine en;
        en.prepare(FS, 1);
        en.setParams(p, true);
        out[k] = sineThrough(en, 1000, -20, 1, 2.f);
    }
    CHECK(out[0] < -14 && out[1] > -9, "EQ→COMP %.1f дБ (сжато), COMP→EQ %.1f дБ (только +12)", out[0], out[1]);
    printf("  EQ→COMP %.1f dB, COMP→EQ %.1f dB\n", out[0], out[1]);
}

static void testParamStorm() {
    printf("param storm (ramps, NaN guard)\n");
    Engine e;
    e.prepare(FS, 2);
    float p[P_COUNT];
    neutral(p);
    srand(7);
    std::vector<float> buf(480 * 2);
    bool bad = false;
    for (int blk = 0; blk < 3000; blk++) {
        for (int i = 0; i < P_COUNT; i++) p[i] = (rand() % 1000) / 10.f - 30.f;   // мусор в том числе
        p[P_BYPASS] = 0;
        p[P_EQ_ON] = 1;
        e.setParams(p, false);
        for (int i = 0; i < 960; i++) buf[i] = (rand() % 2000 - 1000) / 1000.f;
        e.process(buf.data(), 480, nullptr);
        for (float v : buf) if (!(v - v == 0)) bad = true;
    }
    CHECK(!bad, "NaN/inf на выходе");
}

static void benchmark() {
    printf("benchmark\n");
    Engine e;
    e.prepare(FS, 2);
    float p[P_COUNT];
    neutral(p);
    p[P_HPF_ON] = 1; p[P_HPF_SLOPE] = 24; p[P_GATE_ON] = 1; p[P_EQ_ON] = 1; p[P_COMP_ON] = 1; p[P_COMP_RMS] = 1;
    p[P_DS_ON] = 1; p[P_LIM_ON] = 1; p[P_MB_ON] = 1; p[P_TUNE_ON] = 1; p[P_DLY_ON] = 1; p[P_REV_ON] = 1;
    for (int b = 0; b < kEqBands; b++) {
        float* q = p + P_EQ_BASE + b * kEqStride;
        q[0] = 1; q[1] = EQ_BELL; q[2] = 100.f * (b + 1); q[3] = 3; q[4] = 1;
    }
    e.setParams(p, true);
    std::vector<float> buf(480 * 2);
    for (size_t i = 0; i < buf.size(); i++) buf[i] = sinf(i * 0.01f) * 0.3f;
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    const int blocks = 6000;   // 60 с звука
    for (int i = 0; i < blocks; i++) e.process(buf.data(), 480, nullptr);
    QueryPerformanceCounter(&t1);
    double sec = (double)(t1.QuadPart - t0.QuadPart) / f.QuadPart;
    printf("  60 s stereo, всё включено: %.1f ms → %.0fx быстрее реального времени, %.1f µs на блок 10 мс\n", sec * 1000,
           60.0 / sec, sec * 1e6 / blocks);
}

// ---------------------------------------------------------------------------------------------
// COM-обвязка: грузим собранную DLL так же, как это делает audiodg.

// endpoint-свойства, как их отдаёт аудиодвижок: нужен только GUID устройства
static const wchar_t kTestEp[] = L"{11111111-2222-3333-4444-555555555555}";
class TestProps : public IPropertyStore {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** ppv) override { *ppv = this; return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* c) override { *c = 1; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAt(DWORD, PROPERTYKEY*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetValue(REFPROPERTYKEY key, PROPVARIANT* v) override {
        PropVariantInit(v);
        if (key.pid != 4) return S_OK;
        const size_t n = (wcslen(kTestEp) + 1) * sizeof(wchar_t);
        v->vt = VT_LPWSTR;
        v->pwszVal = (LPWSTR)CoTaskMemAlloc(n);
        memcpy(v->pwszVal, kTestEp, n);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetValue(REFPROPERTYKEY, REFPROPVARIANT) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Commit() override { return S_OK; }
};

class TestType : public IAudioMediaType {
public:
    UNCOMPRESSEDAUDIOFORMAT f;
    WAVEFORMATEX w{};
    TestType(GUID sub, DWORD ch, DWORD bytes, float rate) {
        f.guidFormatType = sub; f.dwSamplesPerFrame = ch; f.dwBytesPerSampleContainer = bytes;
        f.dwValidBitsPerSample = bytes * 8; f.fFramesPerSecond = rate; f.dwChannelMask = ch == 1 ? 4 : 3;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** ppv) override { *ppv = this; return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* p) override { *p = FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType*, DWORD*) override { return E_NOTIMPL; }
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override { return &w; }
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* p) override { *p = f; return S_OK; }
};

static void testCom(const char* dllPath, const char* tmpDir) {
    printf("COM / APO contract (%s)\n", dllPath);
    // state.bin во временной папке: DLL ищет его в %ProgramData%\Golos
    char dir[MAX_PATH], file[MAX_PATH];
    snprintf(dir, sizeof dir, "%s\\Golos", tmpDir);
    CreateDirectoryA(tmpDir, nullptr);
    CreateDirectoryA(dir, nullptr);
    snprintf(file, sizeof file, "%s\\state.bin", dir);
    std::vector<uint8_t> img(kShmSize, 0);
    ShmHeader* h = (ShmHeader*)img.data();
    h->magic = kShmMagic; h->version = kShmVersion; h->size = kShmSize;
    ShmParams* sp = (ShmParams*)(img.data() + OFF_PARAMS);
    sp->count = P_COUNT;
    defaultParams(sp->v);
    sp->v[P_OUT_GAIN] = -6;   // заметная разница, чтобы увидеть, что параметры дошли
    sp->seq = 2;
    FILE* fp = fopen(file, "wb");
    fwrite(img.data(), 1, img.size(), fp);
    fclose(fp);
    SetEnvironmentVariableA("ProgramData", tmpDir);

    HMODULE dll = LoadLibraryA(dllPath);
    CHECK(dll, "LoadLibrary %lu", GetLastError());
    if (!dll) return;
    typedef HRESULT(__stdcall * GetCO)(REFCLSID, REFIID, void**);
    GetCO get = (GetCO)(void*)GetProcAddress(dll, "DllGetClassObject");
    CHECK(get, "нет DllGetClassObject");
    const CLSID clsid = {0xBCC05645, 0x2F4E, 0x4C14, {0x9C, 0x6E, 0x24, 0xD2, 0xFC, 0xDE, 0x41, 0xDE}};
    IClassFactory* cf = nullptr;
    HRESULT hr = get(clsid, IID_IClassFactory, (void**)&cf);
    CHECK(SUCCEEDED(hr) && cf, "DllGetClassObject %08lX", hr);
    if (!cf) return;

    IAudioProcessingObject* apo = nullptr;
    hr = cf->CreateInstance(nullptr, IID_IAudioProcessingObject_, (void**)&apo);
    CHECK(SUCCEEDED(hr) && apo, "CreateInstance %08lX", hr);
    if (!apo) return;
    IAudioProcessingObjectRT* rt = nullptr;
    IAudioProcessingObjectConfiguration* cfg = nullptr;
    IAudioSystemEffects2* fx2 = nullptr;
    apo->QueryInterface(IID_IAudioProcessingObjectRT_, (void**)&rt);
    apo->QueryInterface(IID_IAudioProcessingObjectConfiguration_, (void**)&cfg);
    apo->QueryInterface(IID_IAudioSystemEffects2_, (void**)&fx2);
    CHECK(rt && cfg && fx2, "QI rt=%p cfg=%p fx2=%p", (void*)rt, (void*)cfg, (void*)fx2);

    APO_REG_PROPERTIES* rp = nullptr;
    hr = apo->GetRegistrationProperties(&rp);
    CHECK(SUCCEEDED(hr) && rp && rp->u32MaxInstances == 0xFFFFFFFF && rp->Flags == 0xF, "GetRegistrationProperties");
    if (rp) CoTaskMemFree(rp);

    APOInitSystemEffects2 init{};
    init.APOInit.cbSize = sizeof init;
    init.APOInit.clsid = clsid;
    TestProps props;
    init.pAPOEndpointProperties = &props;
    hr = apo->Initialize(sizeof init, (BYTE*)&init);
    CHECK(hr == S_OK, "Initialize %08lX", hr);

    TestType fl(SUBTYPE_IEEE_FLOAT_, 1, 4, 48000), i16({0x00000001, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}}, 1, 2, 48000);
    IAudioMediaType* sup = nullptr;
    hr = apo->IsInputFormatSupported(&fl, &fl, &sup);
    CHECK(hr == S_OK && sup == &fl, "float32 должен подойти: %08lX", hr);
    sup = nullptr;
    hr = apo->IsInputFormatSupported(&fl, &i16, &sup);
    UNCOMPRESSEDAUDIOFORMAT s{};
    if (sup) sup->GetUncompressedAudioFormat(&s);
    CHECK(hr == S_FALSE && sup && s.guidFormatType == SUBTYPE_IEEE_FLOAT_ && s.dwBytesPerSampleContainer == 4,
          "int16 → предложить float32: %08lX", hr);
    if (sup) sup->Release();

    std::vector<float> buffer(480, 0.5f);
    APO_CONNECTION_DESCRIPTOR din{APO_CONNECTION_BUFFER_TYPE_EXTERNAL, (UINT_PTR)buffer.data(), 480, &fl, 0};
    APO_CONNECTION_DESCRIPTOR dout = din;
    APO_CONNECTION_DESCRIPTOR* pin = &din;
    APO_CONNECTION_DESCRIPTOR* pout = &dout;
    hr = cfg->LockForProcess(1, &pin, 1, &pout);
    CHECK(hr == S_OK, "LockForProcess %08lX", hr);
    HNSTIME lat = 0;
    apo->GetLatency(&lat);
    printf("  latency %.2f ms\n", lat / 10000.0);

    APO_CONNECTION_PROPERTY cin{(UINT_PTR)buffer.data(), 480, BUFFER_VALID, 0};
    APO_CONNECTION_PROPERTY cout_{(UINT_PTR)buffer.data(), 0, BUFFER_INVALID, 0};
    APO_CONNECTION_PROPERTY* ppin = &cin;
    APO_CONNECTION_PROPERTY* ppout = &cout_;
    for (int blk = 0; blk < 50; blk++) {
        for (auto& v : buffer) v = 0.5f;
        rt->APOProcess(1, &ppin, 1, &ppout);
    }
    float got = buffer[479];
    CHECK(fabsf(got - 0.5f * dbToLin(-6)) < 0.002f, "выход %.4f, ожидалось %.4f (выход -6 дБ из state.bin)", got,
          0.5f * dbToLin(-6));
    CHECK(cout_.u32ValidFrameCount == 480 && cout_.u32BufferFlags == BUFFER_VALID, "флаги выхода");

    // живая смена параметров через seqlock
    HANDLE fh = CreateFileA(file, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    HANDLE mh = CreateFileMappingA(fh, nullptr, PAGE_READWRITE, 0, kShmSize, nullptr);
    uint8_t* view = (uint8_t*)MapViewOfFile(mh, FILE_MAP_ALL_ACCESS, 0, 0, kShmSize);
    ShmParams* live = (ShmParams*)(view + OFF_PARAMS);
    live->seq = 3;
    live->v[P_OUT_GAIN] = -12;
    live->seq = 4;
    for (int blk = 0; blk < 50; blk++) {
        for (auto& v : buffer) v = 0.5f;
        rt->APOProcess(1, &ppin, 1, &ppout);
    }
    got = buffer[479];
    CHECK(fabsf(got - 0.5f * dbToLin(-12)) < 0.002f, "после правки на лету %.4f, ожидалось %.4f", got, 0.5f * dbToLin(-12));
    ShmMeters* m = (ShmMeters*)(view + OFF_METERS);
    CHECK(m->instances == 1 && m->ringPos == 100 * 480 && m->heartbeatMs > 0, "индикаторы: inst=%d pos=%d", m->instances,
          m->ringPos);
    float* post = (float*)(view + OFF_RING_POST);
    CHECK(fabsf(post[(100 * 480 - 1) & (kRingSize - 1)] - got) < 1e-6f, "кольцо post не совпадает с выходом");

    // свой слот у этого микрофона: APO переключается на него, без слота — снова общие параметры
    {
        ShmSlots* sl = (ShmSlots*)(view + OFF_SLOTS);
        ShmParams* own = (ShmParams*)(view + OFF_SLOT_PARAMS + 3 * kSlotStride);
        own->count = P_COUNT;
        defaultParams(own->v);
        own->v[P_OUT_GAIN] = -20;
        own->seq = 2;
        char ep[64];
        WideCharToMultiByte(CP_UTF8, 0, kTestEp, -1, ep, sizeof ep, nullptr, nullptr);
        sl->e[3].ep = epHashOf(ep);
        sl->e[3].active = 1;
        for (int blk = 0; blk < 50; blk++) { for (auto& v : buffer) v = 0.5f; rt->APOProcess(1, &ppin, 1, &ppout); }
        float own20 = buffer[479];
        sl->e[3].active = 0;
        for (int blk = 0; blk < 50; blk++) { for (auto& v : buffer) v = 0.5f; rt->APOProcess(1, &ppin, 1, &ppout); }
        float shared12 = buffer[479];
        CHECK(fabsf(own20 - 0.5f * dbToLin(-20)) < 0.002f, "свой слот: %.4f, ожидалось %.4f", own20, 0.5f * dbToLin(-20));
        CHECK(fabsf(shared12 - 0.5f * dbToLin(-12)) < 0.002f, "без слота: %.4f, ожидалось %.4f", shared12, 0.5f * dbToLin(-12));
        // окно смотрит на другой микрофон — индикаторы этот APO не пишет
        sl->meterEp = 12345;
        const int32_t before = m->ringPos;
        for (int blk = 0; blk < 10; blk++) rt->APOProcess(1, &ppin, 1, &ppout);
        CHECK(m->ringPos == before, "индикаторы пишутся, хотя окно смотрит на другой микрофон");
        sl->meterEp = 0;
    }

    hr = cfg->UnlockForProcess();
    CHECK(hr == S_OK, "UnlockForProcess %08lX", hr);
    CHECK(m->instances == 0, "instances после Unlock = %d", m->instances);
    UnmapViewOfFile(view);
    CloseHandle(mh);
    CloseHandle(fh);

    fx2->Release();
    cfg->Release();
    rt->Release();
    ULONG left = apo->Release();
    CHECK(left == 0, "утечка ссылок: %lu", left);
    cf->Release();
    typedef HRESULT(__stdcall * CanUnload)();
    CanUnload cu = (CanUnload)(void*)GetProcAddress(dll, "DllCanUnloadNow");
    CHECK(cu && cu() == S_OK, "DllCanUnloadNow != S_OK");
}

int main(int argc, char** argv) {
    _mm_setcsr(_mm_getcsr() | 0x8040);
    testTransparent();
    testEq();
    testHpf();
    testCompressorCurve();
    testLimiter();
    testGate();
    testDeesser();
    testMultiband();
    testTune();
    testFx();
    testOrder();
    testParamStorm();
    benchmark();
    if (argc >= 3) testCom(argv[1], argv[2]);
    printf(failures ? "\n%d FAILED\n" : "\nALL OK\n", failures);
    return failures ? 1 : 0;
}
