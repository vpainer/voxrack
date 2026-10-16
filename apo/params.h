// Параметры цепочки «Голос». Индексы общие для APO (C++) и интерфейса (ui/params.js) —
// менять только синхронно в обоих местах.
#pragma once
#include <stdint.h>

namespace golos {

enum Param : int {
    P_BYPASS = 0,
    P_IN_GAIN = 1,        // дБ
    P_OUT_GAIN = 2,       // дБ
    P_HPF_ON = 3,
    P_HPF_FREQ = 4,       // Гц
    P_HPF_SLOPE = 5,      // 12 или 24 дБ/окт
    P_GATE_ON = 6,
    P_GATE_THRESH = 7,    // дБFS
    P_GATE_RANGE = 8,     // насколько глушить закрытый гейт, дБ
    P_GATE_ATTACK = 9,    // мс
    P_GATE_HOLD = 10,     // мс
    P_GATE_RELEASE = 11,  // мс
    P_EQ_ON = 12,
    P_COMP_ON = 13,
    P_COMP_THRESH = 14,   // дБFS
    P_COMP_RATIO = 15,
    P_COMP_KNEE = 16,     // дБ
    P_COMP_ATTACK = 17,   // мс
    P_COMP_RELEASE = 18,  // мс
    P_COMP_MAKEUP = 19,   // дБ
    P_COMP_MIX = 20,      // 0..1 (параллельная компрессия)
    P_COMP_RMS = 21,      // 0 — пиковый детектор, 1 — RMS
    P_DS_ON = 22,
    P_DS_FREQ = 23,       // Гц
    P_DS_THRESH = 24,     // дБFS
    P_DS_RANGE = 25,      // максимум подавления, дБ
    P_LIM_ON = 26,
    P_LIM_CEIL = 27,      // дБFS
    P_LIM_RELEASE = 28,   // мс
    P_MB_ON = 29,         // многополосный компрессор
    P_TUNE_ON = 30,       // автотюн
    P_DLY_ON = 31,        // делей
    P_EQ_BASE = 32,       // полоса i: P_EQ_BASE + i*5 → on, type, freq, gain, q (32..81)
    P_REV_ON = 82,        // реверб
    P_MB_X1 = 84,         // частоты раздела полос, Гц
    P_MB_X2 = 85,
    P_MB_X3 = 86,
    P_MB_BASE = 88,       // полоса i: P_MB_BASE + i*6 → on, thresh, ratio, attack, release, gain (88..111)
    P_TUNE_KEY = 112,     // тоника 0..11 (C = 0)
    P_TUNE_SCALE = 113,   // 0 — все ноты, 1 — мажор, 2 — минор
    P_TUNE_SPEED = 114,   // мс: 0 — «робот», 50+ — естественно
    P_TUNE_AMOUNT = 115,  // 0..1 — насколько подтягивать к ноте
    P_TUNE_SHIFT = 116,   // транспонирование, полутоны ±12
    P_DLY_TIME = 117,     // мс
    P_DLY_FB = 118,       // 0..0.95
    P_DLY_MIX = 119,      // 0..1
    P_DLY_TONE = 120,     // Гц, срез верха в обратной связи
    P_REV_SIZE = 121,     // 0..1
    P_REV_DAMP = 122,     // 0..1
    P_REV_MIX = 123,      // 0..1
    P_REV_PREDELAY = 124, // мс
    P_REV_LOWCUT = 125,   // Гц, срез низа на входе зала
    P_ORDER_A = 126,      // порядок модулей: по 4 бита на место (id + 1), места 0..5
    P_ORDER_B = 127,      //   места 6..8; ноль — порядок по умолчанию
    P_COUNT = 128
};

enum EqType : int {
    EQ_BELL = 0,
    EQ_LOW_SHELF = 1,
    EQ_HIGH_SHELF = 2,
    EQ_LOW_CUT = 3,
    EQ_HIGH_CUT = 4,
    EQ_NOTCH = 5,
    EQ_ALLPASS = 6        // только внутри: фазовая компенсация кроссовера, в интерфейсе её нет
};

constexpr int kEqBands = 10;
constexpr int kEqStride = 5;
constexpr int kMbBands = 4;
constexpr int kMbStride = 6;

// Модули, которые можно переставлять. Вход/выход и лимитер всегда на своих местах.
enum Stage : int { ST_HPF = 0, ST_GATE, ST_TUNE, ST_EQ, ST_MB, ST_COMP, ST_DS, ST_DLY, ST_REV, kStages };

// Порядок — в двух float-параметрах (целые до 2^24 хранятся точно). Мусор → порядок по умолчанию.
inline void decodeOrder(float a, float b, int* order) {
    const uint32_t ua = a > 0 && a < 16777216.f ? (uint32_t)a : 0;
    const uint32_t ub = b > 0 && b < 16777216.f ? (uint32_t)b : 0;
    int tmp[kStages];
    unsigned seen = 0;
    bool ok = ua != 0;
    for (int i = 0; i < kStages && ok; i++) {
        const int v = (int)((i < 6 ? ua >> (4 * i) : ub >> (4 * (i - 6))) & 15) - 1;
        if (v < 0 || v >= kStages || ((seen >> v) & 1)) ok = false;
        else { tmp[i] = v; seen |= 1u << v; }
    }
    for (int i = 0; i < kStages; i++) order[i] = ok ? tmp[i] : i;
}

inline void encodeOrder(const int* order, float* a, float* b) {
    uint32_t ua = 0, ub = 0;
    for (int i = 0; i < kStages; i++) {
        if (i < 6) ua |= (uint32_t)(order[i] + 1) << (4 * i);
        else ub |= (uint32_t)(order[i] + 1) << (4 * (i - 6));
    }
    *a = (float)ua;
    *b = (float)ub;
}

// Без файла настроек APO прозрачен: всё выключено.
inline void defaultParams(float* p) {
    for (int i = 0; i < P_COUNT; i++) p[i] = 0.f;
    p[P_HPF_FREQ] = 80.f;
    p[P_HPF_SLOPE] = 12.f;
    p[P_GATE_THRESH] = -50.f;
    p[P_GATE_RANGE] = 20.f;
    p[P_GATE_ATTACK] = 2.f;
    p[P_GATE_HOLD] = 80.f;
    p[P_GATE_RELEASE] = 150.f;
    p[P_COMP_THRESH] = -24.f;
    p[P_COMP_RATIO] = 3.f;
    p[P_COMP_KNEE] = 6.f;
    p[P_COMP_ATTACK] = 8.f;
    p[P_COMP_RELEASE] = 120.f;
    p[P_COMP_MIX] = 1.f;
    p[P_DS_FREQ] = 6500.f;
    p[P_DS_THRESH] = -30.f;
    p[P_DS_RANGE] = 8.f;
    p[P_LIM_CEIL] = -1.f;
    p[P_LIM_RELEASE] = 60.f;
    for (int b = 0; b < kEqBands; b++) {
        float* e = p + P_EQ_BASE + b * kEqStride;
        e[1] = EQ_BELL;
        e[2] = 1000.f;
        e[4] = 1.f;
    }
    p[P_MB_X1] = 200.f;
    p[P_MB_X2] = 1500.f;
    p[P_MB_X3] = 6000.f;
    for (int b = 0; b < kMbBands; b++) {
        float* m = p + P_MB_BASE + b * kMbStride;
        m[0] = 1.f;
        m[1] = -30.f;
        m[2] = 2.f;
        m[3] = 10.f;
        m[4] = 150.f;
        m[5] = 0.f;
    }
    p[P_TUNE_SPEED] = 20.f;
    p[P_TUNE_AMOUNT] = 1.f;
    p[P_DLY_TIME] = 320.f;
    p[P_DLY_FB] = 0.3f;
    p[P_DLY_MIX] = 0.2f;
    p[P_DLY_TONE] = 5000.f;
    p[P_REV_SIZE] = 0.55f;
    p[P_REV_DAMP] = 0.45f;
    p[P_REV_MIX] = 0.18f;
    p[P_REV_PREDELAY] = 20.f;
    p[P_REV_LOWCUT] = 250.f;
}

} // namespace golos
