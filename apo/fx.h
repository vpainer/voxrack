// Эффекты для стримов: автотюн (определение высоты YIN + PSOLA), делей и реверб (Freeverb).
// Работают на моно-сумме каналов (микрофон и так моно). Буферы выделяются в prepare() — вне
// потока реального времени; в process() только арифметика.
#pragma once
#include <math.h>
#include <string.h>
#include <stdint.h>
#include <new>

namespace golos {

inline int pow2At(int n) {
    int s = 1;
    while (s < n) s <<= 1;
    return s;
}

// ── Делей ───────────────────────────────────────────────────────────────────────────
// Эхо с фильтрами в обратной связи (верх срезается «тоном», низ — на 150 Гц, чтобы хвосты
// не гудели). Выключенный делей перестаёт принимать звук, но даёт договорить хвостам.
class FxDelay {
public:
    FxDelay() = default;
    FxDelay(const FxDelay&) = delete;
    FxDelay& operator=(const FxDelay&) = delete;
    ~FxDelay() { delete[] buf; }

    bool prepare(float sampleRate) {
        fs = sampleRate;
        const int need = pow2At((int)(fs * 2.1f));
        if (need != size) {
            delete[] buf;
            buf = new (std::nothrow) float[need];
            size = buf ? need : 0;
        }
        mask = size - 1;
        hpCoef = 1.f - expf(-2.f * 3.14159265f * 150.f / fs);
        smooth = 1.f - expf(-1.f / (0.08f * fs));
        reset();
        return buf != nullptr;
    }

    void reset() {
        if (buf) memset(buf, 0, sizeof(float) * size);
        pos = 0;
        lp = hpState = 0;
        idle = on ? 0 : idleLimit();
        dCur = dTarget;
        mixCur = on ? mix : 0;
        inCur = on ? 1.f : 0.f;
    }

    void set(bool enabled, float timeMs, float feedback, float mixAmount, float toneHz) {
        const bool wasActive = active();
        on = enabled;
        dTarget = (double)fminf(fmaxf(timeMs, 10.f), 2000.f) * 0.001 * fs;
        fb = fminf(fmaxf(feedback, 0.f), 0.95f);
        mix = fminf(fmaxf(mixAmount, 0.f), 1.5f);
        lpCoef = 1.f - expf(-2.f * 3.14159265f * fminf(fmaxf(toneHz, 500.f), 18000.f) / fs);
        if (!wasActive) dCur = dTarget;   // в тишине ехать некуда — сразу нужное время
        if (on) idle = 0;
    }

    bool active() const { return buf && (on || idle < idleLimit()); }

    // возвращает добавку к сухому сигналу
    inline float process(float x) {
        inCur += ((on ? 1.f : 0.f) - inCur) * smooth;
        mixCur += (mix - mixCur) * smooth;
        // время в double: во float шаг сглаживания у 12000 отсчётов меньше точности и время «залипает»
        dCur += (dTarget - dCur) * smooth;
        if (fabs(dTarget - dCur) < 0.01) dCur = dTarget;
        if (!on) idle++;
        const int di = (int)dCur;
        const float fr = (float)(dCur - di);
        const uint32_t i1 = pos - (uint32_t)di;
        const float wet = buf[i1 & mask] * (1.f - fr) + buf[(i1 - 1) & mask] * fr;
        lp += (wet - lp) * lpCoef;
        hpState += (lp - hpState) * hpCoef;
        buf[pos & mask] = x * inCur + (lp - hpState) * fb;
        pos++;
        return wet * mixCur;
    }

private:
    int idleLimit() const { return (int)(fs * 8); }
    float* buf = nullptr;
    int size = 0;
    uint32_t mask = 0, pos = 0;
    double dTarget = 14400, dCur = 14400;
    float fs = 48000, fb = 0.3f, mix = 0, mixCur = 0, inCur = 0;
    float lpCoef = 0.5f, hpCoef = 0.02f, smooth = 0.001f, lp = 0, hpState = 0;
    bool on = false;
    int idle = 1 << 30;
};

// ── Реверб ──────────────────────────────────────────────────────────────────────────
// Freeverb (Jezar, public domain): 8 параллельных гребёнок с затуханием верха + 4 всепропускающих,
// плюс предзадержка и срез низа на входе зала, чтобы бас голоса не превращал его в гул.
class FxReverb {
public:
    FxReverb() = default;
    FxReverb(const FxReverb&) = delete;
    FxReverb& operator=(const FxReverb&) = delete;
    ~FxReverb() { delete[] mem; }

    bool prepare(float sampleRate) {
        fs = sampleRate;
        const float k = fs / 44100.f;
        static const int combL[8] = {1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617};
        static const int apL[4] = {556, 441, 341, 225};
        int total = 0;
        for (int i = 0; i < 8; i++) total += cl[i] = (int)(combL[i] * k);
        for (int i = 0; i < 4; i++) total += al[i] = (int)(apL[i] * k);
        pdSize = pow2At((int)(fs * 0.25f));
        total += pdSize;
        if (total != memSize) {
            delete[] mem;
            mem = new (std::nothrow) float[total];
            memSize = mem ? total : 0;
        }
        if (!mem) return false;
        float* p = mem;
        for (int i = 0; i < 8; i++) { cb[i] = p; p += cl[i]; }
        for (int i = 0; i < 4; i++) { ab[i] = p; p += al[i]; }
        pd = p;
        smooth = 1.f - expf(-1.f / (0.05f * fs));
        reset();
        return true;
    }

    void reset() {
        if (mem) memset(mem, 0, sizeof(float) * memSize);
        for (int i = 0; i < 8; i++) { ci[i] = 0; cs[i] = 0; }
        for (int i = 0; i < 4; i++) ai[i] = 0;
        pdPos = 0;
        hpState = 0;
        idle = on ? 0 : idleLimit();
        mixCur = on ? mix : 0;
        inCur = on ? 1.f : 0.f;
    }

    void set(bool enabled, float size, float damp, float mixAmount, float predelayMs, float lowCutHz) {
        on = enabled;
        roomFb = 0.7f + fminf(fmaxf(size, 0.f), 1.f) * 0.28f;
        damp1 = fminf(fmaxf(damp, 0.f), 1.f) * 0.4f;
        mix = fminf(fmaxf(mixAmount, 0.f), 1.5f);
        pdLen = (int)(fminf(fmaxf(predelayMs, 0.f), 200.f) * 0.001f * fs);
        if (pdLen > pdSize - 1) pdLen = pdSize - 1;
        hpCoef = 1.f - expf(-2.f * 3.14159265f * fminf(fmaxf(lowCutHz, 20.f), 2000.f) / fs);
        if (on) idle = 0;
    }

    bool active() const { return mem && (on || idle < idleLimit()); }

    inline float process(float x) {
        inCur += ((on ? 1.f : 0.f) - inCur) * smooth;
        mixCur += (mix - mixCur) * smooth;
        if (!on) idle++;
        hpState += (x - hpState) * hpCoef;
        const uint32_t pm = (uint32_t)pdSize - 1;
        pd[pdPos & pm] = (x - hpState) * inCur;
        const float in = pd[(pdPos - (uint32_t)pdLen) & pm] * 0.015f;
        pdPos++;
        float out = 0;
        for (int i = 0; i < 8; i++) {
            const float y = cb[i][ci[i]];
            cs[i] = y * (1.f - damp1) + cs[i] * damp1;
            cb[i][ci[i]] = in + cs[i] * roomFb;
            if (++ci[i] >= cl[i]) ci[i] = 0;
            out += y;
        }
        for (int i = 0; i < 4; i++) {
            const float b = ab[i][ai[i]];
            ab[i][ai[i]] = out + b * 0.5f;
            if (++ai[i] >= al[i]) ai[i] = 0;
            out = b - out;
        }
        return out * 3.f * mixCur;
    }

private:
    int idleLimit() const { return (int)(fs * 10); }
    float* mem = nullptr;
    int memSize = 0;
    float* cb[8] = {};
    float* ab[4] = {};
    float* pd = nullptr;
    int cl[8] = {}, al[4] = {}, ci[8] = {}, ai[4] = {};
    float cs[8] = {};
    int pdSize = 1, pdLen = 0;
    uint32_t pdPos = 0;
    float fs = 48000, roomFb = 0.84f, damp1 = 0.2f, mix = 0, mixCur = 0, inCur = 0, smooth = 0.001f;
    float hpCoef = 0.02f, hpState = 0;
    bool on = false;
    int idle = 1 << 30;
};

// ── Автотюн ─────────────────────────────────────────────────────────────────────────
// Высота: YIN на сигнале, прореженном до ~12 кГц (дёшево), с уточнением на полной частоте.
// Сдвиг: PSOLA — зёрна по два периода из входа, разложенные с периодом нужной ноты; окна
// нормируются суммой, так что без сдвига получается просто задержанный вход.
// Задержка 36 мс: зерну нужен запас в два с половиной периода самого низкого голоса (70 Гц).
class FxTune {
public:
    FxTune() = default;
    FxTune(const FxTune&) = delete;
    FxTune& operator=(const FxTune&) = delete;
    float meterHz = 0, meterMidi = 0, meterTarget = 0, meterShift = 0;

    ~FxTune() { delete[] mem; }

    bool prepare(float sampleRate) {
        fs = sampleRate;
        D = (int)lroundf(fs / 12000.f);
        if (D < 1) D = 1;
        fsd = fs / D;
        lat = (int)lroundf(0.036f * fs);
        pMax = fs / 70.f;
        hMax = (int)((lat - pMax * 0.5f) * 0.5f) - 2;
        tauMin = (int)(fsd / 1000.f);
        tauMax = (int)(fsd / 70.f);
        if (tauMax > kTauCap) tauMax = kTauCap;
        win = (int)(0.026f * fsd);
        ring = pow2At(lat * 4);
        dring = pow2At(win + tauMax + 64);
        const int total = ring * 3 + dring;
        if (total != memSize) {
            delete[] mem;
            mem = new (std::nothrow) float[total];
            memSize = mem ? total : 0;
        }
        if (!mem) return false;
        in = mem;
        ola = in + ring;
        olaW = ola + ring;
        dec = olaW + ring;
        smooth = 1.f - expf(-1.f / (0.03f * fs));
        reset();
        return true;
    }

    void reset() {
        if (mem) memset(mem, 0, sizeof(float) * memSize);
        t = 0;
        decPos = 0;
        decAcc = 0;
        decN = 0;
        sinceHop = 0;
        hopN = 0;
        corr = 0;
        target = -1;
        wet = on ? 1.f : 0.f;
        restart();
    }

    void set(bool enabled, int keyNote, int scaleType, float speedMs, float amountK, float shiftSemi) {
        if (enabled && !on) restart();
        on = enabled;
        key = ((keyNote % 12) + 12) % 12;
        scale = scaleType < 0 ? 0 : scaleType > 2 ? 2 : scaleType;
        speed = fminf(fmaxf(speedMs, 0.f), 1000.f);
        amount = fminf(fmaxf(amountK, 0.f), 1.f);
        shift = fminf(fmaxf(shiftSemi, -12.f), 12.f);
    }

    int latency() const { return lat; }
    bool busy() const { return on || wet > 0.f; }

    // вход всегда пишется в кольцо (дёшево), остальное — только когда автотюн нужен
    inline void feed(float x) { in[(uint32_t)t & (ring - 1)] = x; }

    inline float process(float x) {
        feed(x);
        float y = x;
        if (busy()) {
            analyze(x);
            while (nextS - nextH <= t) placeGrain();
            const uint32_t i = (uint32_t)t & (ring - 1);
            const float w = olaW[i];
            // при сдвиге вверх зёрна накладываются (сумма окон > 1) — делим на корень, чтобы
            // сохранить энергию; паузы при сдвиге вниз — это и есть более низкий тон, их не раздуваем
            const float tuned = w > 1.f ? ola[i] / sqrtf(w) : ola[i];
            ola[i] = 0;
            olaW[i] = 0;
            wet += ((on ? 1.f : 0.f) - wet) * smooth;
            if (!on && wet < 1e-4f) wet = 0;
            y = x + (tuned - x) * wet;
        }
        t++;
        return y;
    }

private:
    static constexpr int kTauCap = 200;
    static constexpr int kHops = 64;
    struct Hop { int64_t at; float period; float ratio; };

    float* mem = nullptr;
    int memSize = 0;
    float *in = nullptr, *ola = nullptr, *olaW = nullptr, *dec = nullptr;
    int ring = 1, dring = 1;
    float fs = 48000, fsd = 12000, pMax = 686;
    int D = 4, lat = 1536, hMax = 590, tauMin = 12, tauMax = 171, win = 312;
    int64_t t = 0;
    uint32_t decPos = 0;
    float decAcc = 0;
    int decN = 0, sinceHop = 0;
    Hop hops[kHops] = {};
    int hopN = 0;
    bool on = false;
    int key = 0, scale = 0;
    float speed = 20, amount = 1, shift = 0, corr = 0, wet = 0, smooth = 0.001f;
    int target = -1;
    double anaMark = 0;
    int64_t nextS = 0;
    int nextH = 256;
    float dn[kTauCap + 2];

    void restart() {
        anaMark = (double)(t - lat);
        nextH = (int)(0.01f * fs);
        nextS = t + nextH;
    }

    // период и коэффициент сдвига, действовавшие в момент at
    Hop lookup(int64_t at) const {
        Hop best = {at, 0.01f * fs, exp2f(shift / 12.f)};
        int64_t bd = INT64_MAX;
        const int n = hopN < kHops ? hopN : kHops;
        for (int k = 0; k < n; k++) {
            const Hop& h = hops[k];
            const int64_t d = h.at > at ? h.at - at : at - h.at;
            if (d < bd) { bd = d; best = h; }
        }
        return best;
    }

    void placeGrain() {
        const int64_t s = nextS;
        const double c = (double)(s - lat);
        Hop h = lookup((int64_t)c);
        while (anaMark + h.period * 0.5 < c) {
            anaMark += h.period;
            h = lookup((int64_t)anaMark);
        }
        if (anaMark - c > h.period * 0.5) anaMark = c;   // после паузы метки могли убежать вперёд
        const int64_t m = (int64_t)llround(anaMark);
        const int H = nextH;
        const uint32_t rm = (uint32_t)ring - 1;
        const float inv = 3.14159265f / H;
        // вниз импульсов связок становится реже — каждый чуть громче, чтобы голос не проседал
        const float g = h.ratio < 1.f ? 1.f / sqrtf(h.ratio) : 1.f;
        for (int i = -H + 1; i < H; i++) {
            const float w = 0.5f + 0.5f * cosf(i * inv);
            const uint32_t oi = (uint32_t)(s + i) & rm;
            ola[oi] += in[(uint32_t)(m + i) & rm] * w * g;
            olaW[oi] += w;
        }
        // следующее зерно: шаг — период нужной ноты, длина — два периода исходного голоса
        const float pOut = h.period / h.ratio;
        int nh = (int)h.period;
        if (nh > hMax) nh = hMax;
        if (nh < 16) nh = 16;
        nextS = s + (int64_t)lroundf(pOut);
        nextH = nh;
    }

    static int nearestNote(float midi, int key, int scale, int current) {
        static const uint16_t masks[3] = {0xFFF, 0xAB5, 0x5AD};   // хроматика, мажор, натуральный минор
        const uint16_t mask = masks[scale];
        int best = -1;
        float bd = 1e9f;
        const int base = (int)lroundf(midi);
        for (int n = base - 3; n <= base + 3; n++) {
            const int deg = ((n - key) % 12 + 12) % 12;
            if (!(mask >> deg & 1)) continue;
            const float d = fabsf(n - midi);
            if (d < bd) { bd = d; best = n; }
        }
        // гистерезис: не прыгаем между соседними нотами на границе
        if (current >= 0 && best != current && fabsf(current - midi) < bd + 0.15f) {
            const int deg = ((current - key) % 12 + 12) % 12;
            if (mask >> deg & 1) return current;
        }
        return best;
    }

    inline void analyze(float x) {
        decAcc += x;
        if (++decN < D) return;
        dec[decPos & (dring - 1)] = decAcc / D;
        decPos++;
        decAcc = 0;
        decN = 0;
        if (++sinceHop < 48) return;
        sinceHop = 0;
        detect();
    }

    void detect() {
        const uint32_t dm = (uint32_t)dring - 1;
        const int span = win + tauMax;
        if ((int)decPos < span) return;
        const uint32_t base = decPos - (uint32_t)span;
        auto X = [&](int j) { return dec[(base + (uint32_t)j) & dm]; };

        float energy = 0;
        for (int j = 0; j < win; j++) energy += X(j) * X(j);
        const float rms = sqrtf(energy / win);

        float period = 0;
        if (rms > 0.0015f) {   // ~ -56 дБFS: тише — не голос
            float cum = 0;
            dn[0] = 1;
            for (int tau = 1; tau <= tauMax; tau++) {
                float s = 0;
                for (int j = 0; j < win; j++) { const float d = X(j) - X(j + tau); s += d * d; }
                cum += s;
                dn[tau] = cum > 0 ? s * tau / cum : 1;
            }
            int tau = -1;
            for (int k = tauMin; k < tauMax; k++) {
                if (dn[k] < 0.15f) {
                    while (k + 1 < tauMax && dn[k + 1] < dn[k]) k++;
                    tau = k;
                    break;
                }
            }
            if (tau > 0) {
                // уточняем на полной частоте вокруг tau*D
                const uint32_t rm = (uint32_t)ring - 1;
                const int wFull = win * D;
                const int64_t start = t - (int64_t)(span * D);
                int bestL = tau * D;
                float bestV = 1e30f, vm = 0, v0 = 0, vp = 0;
                float vals[64];
                const int lo = tau * D - D, hi = tau * D + D;
                for (int L = lo; L <= hi && L - lo < 64; L++) {
                    float s = 0;
                    for (int j = 0; j < wFull; j += 2) {
                        const float d = in[(uint32_t)(start + j) & rm] - in[(uint32_t)(start + j + L) & rm];
                        s += d * d;
                    }
                    vals[L - lo] = s;
                    if (s < bestV) { bestV = s; bestL = L; }
                }
                float p = (float)bestL;
                const int bi = bestL - lo;
                if (bi > 0 && bestL < hi) {
                    vm = vals[bi - 1]; v0 = vals[bi]; vp = vals[bi + 1];
                    const float den = vm - 2 * v0 + vp;
                    if (den > 1e-12f) p += 0.5f * (vm - vp) / den;
                }
                if (p >= fs / 1000.f && p <= pMax) period = p;
            }
        }

        const float hopSec = 48.f / fsd;
        float ratio;
        if (period > 0) {
            const float f0 = fs / period;
            const float midi = 69.f + 12.f * log2f(f0 / 440.f);
            target = nearestNote(midi, key, scale, target);
            const float want = (float)target - midi;
            const float a = speed <= 0.5f ? 1.f : 1.f - expf(-hopSec * 1000.f / speed);
            // смена ноты при быстрой настройке — сразу, иначе «скорость» тянет как задумано
            corr += (want - corr) * a;
            const float semis = corr * amount + shift;
            ratio = exp2f(fminf(fmaxf(semis, -12.f), 12.f) / 12.f);
            meterHz = f0;
            meterMidi = midi;
            meterTarget = (float)target;
            meterShift = semis;
        } else {
            corr *= 0.9f;
            ratio = exp2f(shift / 12.f);
            period = 0.01f * fs;
            target = -1;
            meterHz = 0;
            meterShift = shift;
        }
        // центр окна анализа — середина отрезка, по которому считали
        Hop& h = hops[hopN % kHops];
        h.at = t - (int64_t)(span * D / 2);
        h.period = period;
        h.ratio = ratio;
        hopN++;
    }
};

} // namespace golos
