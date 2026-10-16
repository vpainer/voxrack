// Цепочка обработки «Голос»: вход → срез низа → гейт → эквалайзер → компрессор → де-эссер →
// выход → лимитер. Работает в реальном времени внутри audiodg: никаких аллокаций, блокировок
// и ввода-вывода, только арифметика над переплетёнными float-кадрами.
#pragma once
#include <math.h>
#include <string.h>
#include <stdint.h>
#include "params.h"
#include "fx.h"

namespace golos {

constexpr int kMaxCh = 8;
constexpr int kMaxLookahead = 512;
constexpr int kRamp = 256;          // за сколько отсчётов плавно переезжают коэффициенты фильтров

inline float dbToLin(float db) { return expf(db * 0.11512925464970228f); }
inline float linToDb(float x) { return x > 1e-10f ? 8.685889638065035f * logf(x) : -200.f; }
inline float clampf(float v, float lo, float hi) { return v != v ? lo : (v < lo ? lo : (v > hi ? hi : v)); }
inline float timeCoef(float ms, float fs) { return ms <= 0.f ? 0.f : expf(-1000.f / (ms * fs)); }
// одно-полюсное сглаживание с «защёлкой»: подойдя вплотную, встаёт ровно в цель (без денормалей)
inline float glide(float cur, float target, float coef) {
    const float v = target + (cur - target) * coef;
    return fabsf(v - target) < 1e-6f ? target : v;
}

struct Coefs {
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    bool identity() const { return b0 == 1 && b1 == 0 && b2 == 0 && a1 == 0 && a2 == 0; }
};

// Формулы RBJ Audio EQ Cookbook — те же, что рисует интерфейс (ui/dsp.js).
inline Coefs designBiquad(int type, double f, double gainDb, double q, double fs) {
    if (f < 10) f = 10;
    if (f > fs * 0.45) f = fs * 0.45;
    if (q < 0.05) q = 0.05;
    if (q > 30) q = 30;
    const double w0 = 2 * 3.14159265358979323846 * f / fs, cw = cos(w0), sw = sin(w0);
    const double alpha = sw / (2 * q), A = pow(10.0, gainDb / 40), sA = 2 * sqrt(A) * alpha;
    double b0, b1, b2, a0, a1, a2;
    switch (type) {
    case EQ_LOW_SHELF:
        b0 = A * ((A + 1) - (A - 1) * cw + sA);
        b1 = 2 * A * ((A - 1) - (A + 1) * cw);
        b2 = A * ((A + 1) - (A - 1) * cw - sA);
        a0 = (A + 1) + (A - 1) * cw + sA;
        a1 = -2 * ((A - 1) + (A + 1) * cw);
        a2 = (A + 1) + (A - 1) * cw - sA;
        break;
    case EQ_HIGH_SHELF:
        b0 = A * ((A + 1) + (A - 1) * cw + sA);
        b1 = -2 * A * ((A - 1) + (A + 1) * cw);
        b2 = A * ((A + 1) + (A - 1) * cw - sA);
        a0 = (A + 1) - (A - 1) * cw + sA;
        a1 = 2 * ((A - 1) - (A + 1) * cw);
        a2 = (A + 1) - (A - 1) * cw - sA;
        break;
    case EQ_LOW_CUT:
        b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2;
        a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
        break;
    case EQ_HIGH_CUT:
        b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2;
        a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
        break;
    case EQ_NOTCH:
        b0 = 1; b1 = -2 * cw; b2 = 1;
        a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
        break;
    case EQ_ALLPASS:
        b0 = 1 - alpha; b1 = -2 * cw; b2 = 1 + alpha;
        a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
        break;
    default: // EQ_BELL
        b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A;
        a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
        break;
    }
    Coefs c;
    c.b0 = b0 / a0; c.b1 = b1 / a0; c.b2 = b2 / a0; c.a1 = a1 / a0; c.a2 = a2 / a0;
    return c;
}

// Биквад (транспонированная форма II, double). Новые коэффициенты подъезжают линейно за
// kRamp отсчётов: треугольник устойчивости выпуклый, так что промежуточные фильтры
// устойчивы, а щелчков при перетаскивании полос нет.
struct Biquad {
    Coefs cur, target, step;
    int rampLeft = 0;
    double z1[kMaxCh] = {}, z2[kMaxCh] = {};

    void set(const Coefs& c, bool instant) {
        target = c;
        if (instant) { cur = c; rampLeft = 0; return; }
        step.b0 = (c.b0 - cur.b0) / kRamp; step.b1 = (c.b1 - cur.b1) / kRamp;
        step.b2 = (c.b2 - cur.b2) / kRamp; step.a1 = (c.a1 - cur.a1) / kRamp;
        step.a2 = (c.a2 - cur.a2) / kRamp;
        rampLeft = kRamp;
    }
    bool active() const { return rampLeft > 0 || !cur.identity(); }
    void clear() { memset(z1, 0, sizeof z1); memset(z2, 0, sizeof z2); }
    inline void tick() {
        if (!rampLeft) return;
        if (--rampLeft == 0) { cur = target; return; }
        cur.b0 += step.b0; cur.b1 += step.b1; cur.b2 += step.b2; cur.a1 += step.a1; cur.a2 += step.a2;
    }
    inline float run(float x, int ch) {
        const double y = cur.b0 * x + z1[ch];
        z1[ch] = cur.b1 * x - cur.a1 * y + z2[ch];
        z2[ch] = cur.b2 * x - cur.a2 * y;
        return (float)y;
    }
};

struct Tap {                 // куда складывать моно-сигнал для индикаторов (может быть пусто)
    float* pre = nullptr;
    float* post = nullptr;
    uint32_t mask = 0;
    uint32_t pos = 0;
};

class Engine {
public:
    float gateDb = 0, compGrDb = 0, deessGrDb = 0, limGrDb = 0;   // худшее за последний блок
    float mbInDb[kMbBands] = {-120, -120, -120, -120};            // уровень в полосе (RMS, макс. за блок)
    float mbGrDb[kMbBands] = {};                                  // подавление в полосе
    FxTune tune;
    FxDelay echo;
    FxReverb reverb;

    Engine() = default;
    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    void prepare(float sampleRate, int channels) {
        fs = sampleRate > 1000 ? sampleRate : 48000;
        ch = channels < 1 ? 1 : (channels > kMaxCh ? kMaxCh : channels);
        L = (int)lroundf(0.0015f * fs);
        if (L < 2) L = 2;
        if (L > kMaxLookahead - 1) L = kMaxLookahead - 1;
        smooth20 = timeCoef(20, fs);
        rmsCoef = timeCoef(10, fs);
        dsAtt = timeCoef(0.5f, fs);
        dsRel = timeCoef(60, fs);
        gateDetRel = timeCoef(20, fs);
        tune.prepare(fs);      // буферы эффектов — здесь, вне потока реального времени
        echo.prepare(fs);
        reverb.prepare(fs);
        reset();
        float p[P_COUNT];
        defaultParams(p);
        setParams(p, true);
    }

    int latencyFrames() const { return L - 1; }

    void reset() {
        for (auto& b : hpf) b.clear();
        for (auto& b : eq) b.clear();
        dsDet.clear(); dsLp.clear();
        inGain = inTarget; outGain = outTarget;
        gateEnv = 0; gateGain = 1; gateOpen = true; gateHold = 0;
        msState = 0; compGr = 0; compWet = compWetTarget; makeup = makeupTarget;
        dsEnv = 0;
        for (auto& b : mbLp) b.clear();
        for (auto& b : mbHp) b.clear();
        for (auto& b : mbAp) b.clear();
        for (int k = 0; k < kMbBands; k++) { mb[k].ms = 0; mb[k].gr = 0; mb[k].gain = mb[k].gainTarget; }
        mbWet = mbWetTarget;
        tune.reset();
        echo.reset();
        reverb.reset();
        for (int i = 0; i < kMaxLookahead; i++) { hist[i] = 1; box[i] = 1; }
        boxSum = L; hPos = bPos = 0; limG = 1;
        memset(delay, 0, sizeof delay); dPos = 0;
    }

    // Безопасно звать из потока реального времени: только тригонометрия, без аллокаций.
    void setParams(const float* np, bool instant) {
        float p[P_COUNT];
        for (int i = 0; i < P_COUNT; i++) p[i] = np[i];

        bypass = p[P_BYPASS] >= 0.5f;
        decodeOrder(p[P_ORDER_A], p[P_ORDER_B], order);
        inTarget = dbToLin(clampf(p[P_IN_GAIN], -36, 36));
        outTarget = dbToLin(clampf(p[P_OUT_GAIN], -36, 36));

        const bool hpfOn = p[P_HPF_ON] >= 0.5f;
        const double hf = clampf(p[P_HPF_FREQ], 20, 400);
        const bool steep = p[P_HPF_SLOPE] >= 18;
        Coefs id;
        hpf[0].set(hpfOn ? designBiquad(EQ_LOW_CUT, hf, 0, steep ? 0.5411961 : 0.7071068, fs) : id, instant);
        hpf[1].set(hpfOn && steep ? designBiquad(EQ_LOW_CUT, hf, 0, 1.3065630, fs) : id, instant);

        gateOn = p[P_GATE_ON] >= 0.5f;
        const float gThr = clampf(p[P_GATE_THRESH], -90, 0);
        gateOpenLin = dbToLin(gThr);
        gateCloseLin = dbToLin(gThr - 4);
        gateRangeLin = dbToLin(-clampf(p[P_GATE_RANGE], 0, 80));
        gateAtt = timeCoef(clampf(p[P_GATE_ATTACK], 0.1f, 100), fs);
        gateRel = timeCoef(clampf(p[P_GATE_RELEASE], 5, 2000), fs);
        gateHoldN = (int)(clampf(p[P_GATE_HOLD], 0, 2000) * 0.001f * fs);

        const bool eqOn = p[P_EQ_ON] >= 0.5f;
        for (int b = 0; b < kEqBands; b++) {
            const float* e = p + P_EQ_BASE + b * kEqStride;
            const bool on = eqOn && e[0] >= 0.5f;
            const int type = (int)clampf(e[1], 0, 5);
            const float gain = clampf(e[3], -24, 24);
            // полка/колокол с нулевым усилением — это прямой провод, не тратим на неё такты
            const bool flat = (type == EQ_BELL || type == EQ_LOW_SHELF || type == EQ_HIGH_SHELF) && fabsf(gain) < 0.01f;
            eq[b].set(on && !flat ? designBiquad(type, clampf(e[2], 10, 22000), gain, clampf(e[4], 0.1f, 30), fs) : id, instant);
        }

        // многополосный: три кроссовера Линквица–Райли 4-го порядка, сумма полос — всепропускающий
        // фильтр (АЧХ ровная), фазу младших полос выравниваем всепропускающими звеньями
        mbOn = p[P_MB_ON] >= 0.5f;
        mbWetTarget = mbOn ? 1.f : 0.f;
        {
            double x1 = clampf(p[P_MB_X1], 40, 12000);
            double x2 = clampf(p[P_MB_X2], (float)(x1 * 1.4), 15000);
            double x3 = clampf(p[P_MB_X3], (float)(x2 * 1.4), 18000);
            const double xs[3] = {x1, x2, x3};
            for (int k = 0; k < 3; k++) {
                const Coefs lp = designBiquad(EQ_HIGH_CUT, xs[k], 0, 0.7071068, fs);
                const Coefs hp = designBiquad(EQ_LOW_CUT, xs[k], 0, 0.7071068, fs);
                mbLp[k * 2].set(lp, instant); mbLp[k * 2 + 1].set(lp, instant);
                mbHp[k * 2].set(hp, instant); mbHp[k * 2 + 1].set(hp, instant);
            }
            mbAp[0].set(designBiquad(EQ_ALLPASS, x2, 0, 0.7071068, fs), instant);   // полоса 0, раздел 2
            mbAp[1].set(designBiquad(EQ_ALLPASS, x3, 0, 0.7071068, fs), instant);   // полоса 0, раздел 3
            mbAp[2].set(designBiquad(EQ_ALLPASS, x3, 0, 0.7071068, fs), instant);   // полоса 1, раздел 3
        }
        for (int k = 0; k < kMbBands; k++) {
            const float* m = p + P_MB_BASE + k * kMbStride;
            MbBand& b = mb[k];
            b.on = mbOn && m[0] >= 0.5f;
            b.t = clampf(m[1], -60, 0);
            b.r = clampf(m[2], 1, 20);
            b.att = timeCoef(clampf(m[3], 0.1f, 200), fs);
            b.rel = timeCoef(clampf(m[4], 5, 3000), fs);
            b.gainTarget = b.on ? dbToLin(clampf(m[5], -18, 18)) : 1.f;
        }

        tune.set(p[P_TUNE_ON] >= 0.5f, (int)lroundf(p[P_TUNE_KEY]), (int)lroundf(p[P_TUNE_SCALE]),
                 p[P_TUNE_SPEED], p[P_TUNE_AMOUNT], p[P_TUNE_SHIFT]);
        echo.set(p[P_DLY_ON] >= 0.5f, p[P_DLY_TIME], p[P_DLY_FB], p[P_DLY_MIX], p[P_DLY_TONE]);
        reverb.set(p[P_REV_ON] >= 0.5f, p[P_REV_SIZE], p[P_REV_DAMP], p[P_REV_MIX], p[P_REV_PREDELAY], p[P_REV_LOWCUT]);

        compOn = p[P_COMP_ON] >= 0.5f;
        compT = clampf(p[P_COMP_THRESH], -60, 0);
        compR = clampf(p[P_COMP_RATIO], 1, 30);
        compW = clampf(p[P_COMP_KNEE], 0, 24);
        compAtt = timeCoef(clampf(p[P_COMP_ATTACK], 0.1f, 200), fs);
        compRel = timeCoef(clampf(p[P_COMP_RELEASE], 5, 3000), fs);
        compRms = p[P_COMP_RMS] >= 0.5f;
        makeupTarget = compOn ? dbToLin(clampf(p[P_COMP_MAKEUP], -12, 36)) : 1.f;
        compWetTarget = compOn ? clampf(p[P_COMP_MIX], 0, 1) : 0.f;

        dsOn = p[P_DS_ON] >= 0.5f;
        const double df = clampf(p[P_DS_FREQ], 2000, 16000);
        dsDet.set(designBiquad(EQ_LOW_CUT, df, 0, 0.7071068, fs), instant);
        dsLp.set(designBiquad(EQ_HIGH_CUT, df, 0, 0.7071068, fs), instant);
        dsThresh = clampf(p[P_DS_THRESH], -60, 0);
        dsRange = clampf(p[P_DS_RANGE], 0, 24);

        limOn = p[P_LIM_ON] >= 0.5f;
        limCeil = dbToLin(clampf(p[P_LIM_CEIL], -24, 0));
        limRel = timeCoef(clampf(p[P_LIM_RELEASE], 5, 1000), fs);

        if (instant) {
            inGain = inTarget; outGain = outTarget; makeup = makeupTarget; compWet = compWetTarget; mbWet = mbWetTarget;
            for (auto& b : mb) b.gain = b.gainTarget;
        }
    }

    bool bypassed() const { return bypass; }

    // buf — переплетённые кадры, обрабатываются на месте.
    void process(float* buf, int frames, Tap* tap) {
        wGate = wComp = wDs = wLim = 0;
        for (int k = 0; k < kMbBands; k++) wMbIn[k] = wMbGr[k] = 0;
        invCh = 1.f / ch;
        for (int i = 0; i < frames; i++) {
            float* x = buf + i * ch;

            if (tap && tap->pre) {
                float s = 0;
                for (int c = 0; c < ch; c++) s += x[c];
                tap->pre[tap->pos & tap->mask] = s * invCh;
            }

            if (bypass) {
                if (tap && tap->post) {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += x[c];
                    tap->post[tap->pos & tap->mask] = s * invCh;
                }
                if (tap) tap->pos++;
                continue;
            }

            // вход
            inGain = glide(inGain, inTarget, smooth20);
            for (int c = 0; c < ch; c++) x[c] *= inGain;

            // модули — в порядке, который выбрал пользователь (перетаскиванием в окне)
            for (int k = 0; k < kStages; k++) {
                switch (order[k]) {
                case ST_HPF: stHpf(x); break;
                case ST_GATE: stGate(x); break;
                case ST_TUNE: stTune(x); break;
                case ST_EQ: stEq(x); break;
                case ST_MB: stMb(x); break;
                case ST_COMP: stComp(x); break;
                case ST_DS: stDs(x); break;
                case ST_DLY: stDelay(x); break;
                case ST_REV: stReverb(x); break;
                default: break;
                }
            }

            // выход
            outGain = glide(outGain, outTarget, smooth20);
            for (int c = 0; c < ch; c++) x[c] *= outGain;

            // лимитер с заглядыванием вперёд: минимум требуемого усиления за окно L,
            // сглаженный скользящим средним той же длины, к пику доходит ровно вовремя
            {
                float peak = 0;
                for (int c = 0; c < ch; c++) { float a = fabsf(x[c]); if (a > peak) peak = a; }
                hist[hPos] = (limOn && peak > limCeil) ? limCeil / peak : 1.f;
                if (++hPos == L) hPos = 0;
                float m = 1.f;
                for (int k = 0; k < L; k++) if (hist[k] < m) m = hist[k];
                boxSum += (double)m - box[bPos];
                box[bPos] = m;
                if (++bPos == L) bPos = 0;
                const float avg = (float)(boxSum / L);
                limG = avg < limG ? avg : avg + (limG - avg) * limRel;
                float* d = delay[dPos];
                for (int c = 0; c < ch; c++) {
                    const float y = d[c] * limG;
                    d[c] = x[c];
                    x[c] = limOn ? clampf(y, -limCeil, limCeil) : y;
                }
                if (++dPos == L - 1) dPos = 0;
                if (limG < 1.f) { float r = -linToDb(limG); if (r > wLim) wLim = r; }
            }

            if (tap && tap->post) {
                float s = 0;
                for (int c = 0; c < ch; c++) s += x[c];
                tap->post[tap->pos & tap->mask] = s * invCh;
            }
            if (tap) tap->pos++;
        }

        // страховка: если где-то родился NaN/inf, сбрасываем состояние и отдаём тишину
        float acc = 0;
        for (int i = 0; i < frames * ch; i += 7) acc += buf[i];
        if (!(acc - acc == 0)) { reset(); memset(buf, 0, sizeof(float) * frames * ch); }

        gateDb = -wGate; compGrDb = wComp; deessGrDb = wDs; limGrDb = wLim;
        for (int k = 0; k < kMbBands; k++) {
            mbInDb[k] = mbWet > 0.f ? (wMbIn[k] == 0 ? -120.f : wMbIn[k]) : -120.f;
            mbGrDb[k] = wMbGr[k];
        }
    }

private:
    // ── модули по отдельности: каждый обрабатывает один кадр (все каналы) ──────────────

    inline void stHpf(float* x) {
        for (int s = 0; s < 2; s++) {
            Biquad& b = hpf[s];
            if (!b.active()) continue;
            b.tick();
            for (int c = 0; c < ch; c++) x[c] = b.run(x[c], c);
            if (!b.active()) b.clear();
        }
    }

    // гейт (экспандер с гистерезисом и удержанием)
    inline void stGate(float* x) {
        float det = 0;
        for (int c = 0; c < ch; c++) { float a = fabsf(x[c]); if (a > det) det = a; }
        gateEnv = det > gateEnv ? det : det + (gateEnv - det) * gateDetRel;
        if (gateEnv >= gateOpenLin) { gateOpen = true; gateHold = gateHoldN; }
        else if (gateEnv < gateCloseLin) { if (gateHold > 0) gateHold--; else gateOpen = false; }
        const float target = (!gateOn || gateOpen) ? 1.f : gateRangeLin;
        gateGain = glide(gateGain, target, target > gateGain ? gateAtt : gateRel);
        if (gateGain < 0.99999f) for (int c = 0; c < ch; c++) x[c] *= gateGain;
        if (gateGain < 1.f) { float d = -linToDb(gateGain); if (d > wGate) wGate = d; }
    }

    // автотюн: моно-сумма → PSOLA; пока выключен, только пишет вход в кольцо
    inline void stTune(float* x) {
        float mono = 0;
        for (int c = 0; c < ch; c++) mono += x[c];
        mono *= invCh;
        const float y = tune.process(mono);
        if (tune.busy()) for (int c = 0; c < ch; c++) x[c] = y;
    }

    inline void stEq(float* x) {
        for (int b = 0; b < kEqBands; b++) {
            Biquad& q = eq[b];
            if (!q.active()) continue;
            q.tick();
            for (int c = 0; c < ch; c++) x[c] = q.run(x[c], c);
            if (!q.active()) q.clear();
        }
    }

    // многополосный компрессор: делим на 4 полосы, каждая сжимается сама по себе
    inline void stMb(float* x) {
        mbWet = glide(mbWet, mbWetTarget, smooth20);
        if (mbWet <= 0.f) return;
        for (auto& f : mbLp) f.tick();
        for (auto& f : mbHp) f.tick();
        for (auto& f : mbAp) f.tick();
        float band[kMbBands][kMaxCh];
        float det[kMbBands] = {0, 0, 0, 0};
        for (int c = 0; c < ch; c++) {
            const float v = x[c];
            const float l1 = mbLp[1].run(mbLp[0].run(v, c), c);
            const float h1 = mbHp[1].run(mbHp[0].run(v, c), c);
            const float m2 = mbLp[3].run(mbLp[2].run(h1, c), c);
            const float h2 = mbHp[3].run(mbHp[2].run(h1, c), c);
            band[0][c] = mbAp[1].run(mbAp[0].run(l1, c), c);
            band[1][c] = mbAp[2].run(m2, c);
            band[2][c] = mbLp[5].run(mbLp[4].run(h2, c), c);
            band[3][c] = mbHp[5].run(mbHp[4].run(h2, c), c);
            for (int k = 0; k < kMbBands; k++) {
                const float e = band[k][c] * band[k][c];
                if (e > det[k]) det[k] = e;
            }
        }
        float g[kMbBands];
        for (int k = 0; k < kMbBands; k++) {
            MbBand& b = mb[k];
            b.ms = det[k] + (b.ms - det[k]) * rmsCoef;
            const float lvl = linToDb(sqrtf(b.ms));
            float gr = 0;
            if (b.on) {
                const float over = lvl - b.t;
                if (2 * fabsf(over) <= kMbKnee) { const float t = over + kMbKnee * 0.5f; gr = (1 - 1 / b.r) * t * t / (2 * kMbKnee); }
                else if (over > 0) gr = (1 - 1 / b.r) * over;
            }
            b.gr = gr + (b.gr - gr) * (gr > b.gr ? b.att : b.rel);
            b.gain = glide(b.gain, b.gainTarget, smooth20);
            g[k] = dbToLin(-b.gr) * b.gain;
            if (lvl > -120 && (wMbIn[k] == 0 || lvl > wMbIn[k])) wMbIn[k] = lvl;
            if (b.gr > wMbGr[k]) wMbGr[k] = b.gr;
        }
        for (int c = 0; c < ch; c++) {
            const float y = band[0][c] * g[0] + band[1][c] * g[1] + band[2][c] * g[2] + band[3][c] * g[3];
            x[c] = mbWet >= 1.f ? y : x[c] + (y - x[c]) * mbWet;
        }
    }

    // компрессор (каналы связаны, мягкое колено, параллельный микс)
    inline void stComp(float* x) {
        float det;
        if (compRms) {
            float s = 0;
            for (int c = 0; c < ch; c++) { float v = x[c] * x[c]; if (v > s) s = v; }
            msState = s + (msState - s) * rmsCoef;
            det = sqrtf(msState);
        } else {
            det = 0;
            for (int c = 0; c < ch; c++) { float a = fabsf(x[c]); if (a > det) det = a; }
        }
        const float over = linToDb(det) - compT;
        float gr;
        if (compW > 0 && 2 * fabsf(over) <= compW) {
            const float t = over + compW * 0.5f;
            gr = (1 - 1 / compR) * t * t / (2 * compW);
        } else {
            gr = over > 0 ? (1 - 1 / compR) * over : 0;
        }
        compGr = gr + (compGr - gr) * (gr > compGr ? compAtt : compRel);
        compWet = glide(compWet, compWetTarget, smooth20);
        makeup = glide(makeup, makeupTarget, smooth20);
        if (compWet > 0.f || makeup != 1.f) {
            const float g = (1 - compWet) + compWet * dbToLin(-compGr) * makeup;
            for (int c = 0; c < ch; c++) x[c] *= g;
        }
        if (compOn && compGr > wComp) wComp = compGr;
    }

    // де-эссер: следим за верхом через ФВЧ, давим только то, что выше частоты раздела
    inline void stDs(float* x) {
        if (!dsOn) return;
        dsDet.tick(); dsLp.tick();
        float det = 0;
        for (int c = 0; c < ch; c++) { float a = fabsf(dsDet.run(x[c], c)); if (a > det) det = a; }
        dsEnv = det + (dsEnv - det) * (det > dsEnv ? dsAtt : dsRel);
        const float over = linToDb(dsEnv) - dsThresh;
        const float red = over > 0 ? fminf(dsRange, over * 0.75f) : 0.f;
        const float g = dbToLin(-red);
        for (int c = 0; c < ch; c++) {
            const float lo = dsLp.run(x[c], c);
            x[c] = lo + g * (x[c] - lo);
        }
        if (red > wDs) wDs = red;
    }

    // делей и реверб — добавкой к сухому сигналу: что стоит выше по цепи, то и попадает в эффект
    inline void stDelay(float* x) {
        if (!echo.active()) return;
        float send = 0;
        for (int c = 0; c < ch; c++) send += x[c];
        const float dl = echo.process(send * invCh);
        for (int c = 0; c < ch; c++) x[c] += dl;
    }

    inline void stReverb(float* x) {
        if (!reverb.active()) return;
        float send = 0;
        for (int c = 0; c < ch; c++) send += x[c];
        const float rv = reverb.process(send * invCh);
        for (int c = 0; c < ch; c++) x[c] += rv;
    }

    float wGate = 0, wComp = 0, wDs = 0, wLim = 0, invCh = 1;
    float wMbIn[kMbBands] = {}, wMbGr[kMbBands] = {};
    int order[kStages] = {0, 1, 2, 3, 4, 5, 6, 7, 8};

    float fs = 48000;
    int ch = 1;
    bool bypass = false;
    float smooth20 = 0, rmsCoef = 0;

    float inGain = 1, inTarget = 1, outGain = 1, outTarget = 1;
    Biquad hpf[2];

    bool gateOn = false, gateOpen = true;
    float gateEnv = 0, gateGain = 1, gateOpenLin = 0, gateCloseLin = 0, gateRangeLin = 1;
    float gateAtt = 0, gateRel = 0, gateDetRel = 0;
    int gateHold = 0, gateHoldN = 0;

    Biquad eq[kEqBands];

    bool compOn = false, compRms = false;
    float compT = -24, compR = 3, compW = 6, compAtt = 0, compRel = 0;
    float msState = 0, compGr = 0, compWet = 0, compWetTarget = 0, makeup = 1, makeupTarget = 1;

    bool dsOn = false;
    Biquad dsDet, dsLp;
    float dsEnv = 0, dsThresh = -30, dsRange = 8, dsAtt = 0, dsRel = 0;

    struct MbBand {
        bool on = false;
        float t = -30, r = 2, att = 0, rel = 0, gain = 1, gainTarget = 1, ms = 0, gr = 0;
    };
    static constexpr float kMbKnee = 6.f;
    bool mbOn = false;
    float mbWet = 0, mbWetTarget = 0;
    Biquad mbLp[6], mbHp[6], mbAp[3];
    MbBand mb[kMbBands];

    bool limOn = false;
    float limCeil = 1, limRel = 0, limG = 1;
    int L = 72, hPos = 0, bPos = 0, dPos = 0;
    float hist[kMaxLookahead], box[kMaxLookahead];
    double boxSum = 0;
    float delay[kMaxLookahead][kMaxCh];
};

} // namespace golos
