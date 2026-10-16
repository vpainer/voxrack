// Общая память APO ⇄ «Голос». Это файл C:\ProgramData\Golos\state.bin, который обе стороны
// отображают в память: audiodg (LocalService, сессия 0) и окно (пользователь) видят одни
// и те же страницы без Global\-объектов и привилегий, а настройки сами переживают перезагрузку.
// Раскладка дублируется в app/Shm.cs — менять синхронно.
#pragma once
#include <stdint.h>
#include <stddef.h>

namespace golos {

constexpr uint32_t kShmMagic = 0x534F4C47;   // 'GLOS'
constexpr uint32_t kShmVersion = 1;
constexpr uint32_t kRingSize = 16384;        // отсчётов в каждом кольце (степень двойки)

constexpr uint32_t OFF_HEADER = 0;
constexpr uint32_t OFF_PARAMS = 64;
constexpr uint32_t OFF_METERS = 1024;
constexpr uint32_t OFF_RING_PRE = 2048;
constexpr uint32_t OFF_RING_POST = OFF_RING_PRE + kRingSize * 4;
constexpr uint32_t kShmSize = 139264;        // 34 страницы, с запасом после колец

static_assert(OFF_RING_POST + kRingSize * 4 <= kShmSize, "shm layout");

struct ShmHeader {          // @0
    uint32_t magic;
    uint32_t version;
    uint32_t size;
    uint32_t reserved;
};

struct ShmParams {          // @64, пишет окно (seqlock: нечётный seq — идёт запись)
    volatile int32_t seq;
    int32_t count;
    float v[128];
};

struct ShmMeters {          // @1024, пишет APO
    volatile int64_t heartbeatMs;   // GetTickCount64 последней записи
    volatile int32_t writer;        // какой экземпляр APO сейчас пишет индикаторы
    volatile int32_t ringPos;       // сколько отсчётов записано (по модулю kRingSize — позиция)
    float sampleRate;
    int32_t channels;
    float gateDb;                   // 0 — гейт открыт, минус — сколько глушит
    float compGrDb;                 // плюс — сколько снимает компрессор
    float deessGrDb;
    float limGrDb;
    volatile int32_t instances;     // экземпляров APO в работе (сколько программ слушают мик)
    int32_t bypass;
    volatile int32_t processCount;
    int32_t apoVersion;
    uint32_t modeData1;             // первые 4 байта GUID режима обработки (для диагностики)
    float mbInDb[4];                // @60: уровень в полосах многополосного компрессора
    float mbGrDb[4];                // @76: подавление в полосах
    float tuneHz;                   // @92: найденная высота голоса (0 — не голос)
    float tuneMidi;                 // @96: она же в полутонах MIDI
    float tuneTarget;               // @100: нота, к которой тянем
    float tuneShift;                // @104: итоговый сдвиг, полутоны
};

// Свои настройки у отдельных микрофонов: таблица слотов после колец. APO находит слот по хешу
// своего endpoint GUID; нет слота — берёт общие параметры (OFF_PARAMS).
constexpr uint32_t OFF_SLOTS = OFF_RING_POST + kRingSize * 4;   // 133120
constexpr int kSlots = 8;
constexpr uint32_t OFF_SLOT_PARAMS = OFF_SLOTS + 128;
constexpr uint32_t kSlotStride = 576;

struct ShmSlotEntry {
    volatile uint32_t ep;           // хеш endpoint GUID (0 — пусто)
    volatile int32_t active;
};

struct ShmSlots {                   // @OFF_SLOTS
    int32_t version;
    int32_t reserved;
    volatile uint32_t meterEp;      // чьи индикаторы писать (0 — любого); окно ставит выбранный микрофон
    int32_t reserved2;
    ShmSlotEntry e[kSlots];
};

static_assert(OFF_SLOT_PARAMS + kSlots * kSlotStride <= kShmSize, "slots");
static_assert(sizeof(ShmSlots) <= 128 && sizeof(ShmParams) <= kSlotStride, "slot layout");

// FNV-1a по ASCII-строке GUID в верхнем регистре: «{0F3A51B6-12BF-…}». Ноль зарезервирован.
inline uint32_t epHashOf(const char* s) {
    uint32_t h = 2166136261u;
    for (; *s; s++) {
        char c = *s;
        if (c >= 'a' && c <= 'z') c = (char)(c - 32);
        h = (h ^ (uint8_t)c) * 16777619u;
    }
    return h ? h : 1;
}

static_assert(sizeof(ShmParams) <= OFF_METERS - OFF_PARAMS, "params");
static_assert(sizeof(ShmMeters) <= OFF_RING_PRE - OFF_METERS, "meters");
static_assert(offsetof(ShmMeters, mbInDb) == 60 && offsetof(ShmMeters, mbGrDb) == 76 && offsetof(ShmMeters, tuneHz) == 92,
              "meters layout = app/Shm.cs");

} // namespace golos
