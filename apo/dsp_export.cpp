// GolosDsp.dll — та же цепочка, что в APO, но для окна «Голоса»: прогнать записанный дубль
// через текущие настройки и дать послушать «до/после», не трогая живой микрофон.
#include "dsp.h"
#include <new>
#include <xmmintrin.h>

using namespace golos;

extern "C" {

__declspec(dllexport) void* golos_create(float fs, int ch) {
    Engine* e = new (std::nothrow) Engine();
    if (e) e->prepare(fs, ch);
    return e;
}

__declspec(dllexport) void golos_destroy(void* h) { delete (Engine*)h; }

__declspec(dllexport) void golos_set(void* h, const float* p, int instant) { ((Engine*)h)->setParams(p, instant != 0); }

__declspec(dllexport) void golos_reset(void* h) { ((Engine*)h)->reset(); }

__declspec(dllexport) void golos_process(void* h, float* buf, int frames) {
    const unsigned csr = _mm_getcsr();
    _mm_setcsr(csr | 0x8040);
    ((Engine*)h)->process(buf, frames, nullptr);
    _mm_setcsr(csr);
}

// [0] гейт дБ, [1] компрессор, [2] де-эссер, [3] лимитер — худшее за последний блок
__declspec(dllexport) void golos_meters(void* h, float* out4) {
    Engine* e = (Engine*)h;
    out4[0] = e->gateDb;
    out4[1] = e->compGrDb;
    out4[2] = e->deessGrDb;
    out4[3] = e->limGrDb;
}

__declspec(dllexport) int golos_latency(void* h) { return ((Engine*)h)->latencyFrames(); }

}
