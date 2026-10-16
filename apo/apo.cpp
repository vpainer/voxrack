// GolosAPO.dll — системный эффект (SFX) на микрофон. Windows сама грузит его в audiodg.exe
// и прогоняет через него поток каждого приложения, которое открывает микрофон: OBS, Discord,
// Telegram, браузер видят обычный «Microphone (Shure MV7)», но уже обработанный.
#include "apodefs.h"
#include <new>
#include <stdio.h>
#include <stdarg.h>
#include <xmmintrin.h>
#include "dsp.h"
#include "shared.h"

using namespace golos;

// {BCC05645-2F4E-4C14-9C6E-24D2FCDE41DE}
static const CLSID CLSID_GolosApo = {0xBCC05645, 0x2F4E, 0x4C14, {0x9C, 0x6E, 0x24, 0xD2, 0xFC, 0xDE, 0x41, 0xDE}};
static const wchar_t kClsidStr[] = L"{BCC05645-2F4E-4C14-9C6E-24D2FCDE41DE}";
static const wchar_t kFriendlyName[] = L"VoxRack";
static const int kApoVersion = 5;

static HMODULE g_module = nullptr;
static volatile LONG g_objects = 0;
static volatile LONG g_locks = 0;
static volatile LONG g_instanceSeq = 0;

// ---------------------------------------------------------------------------------------------
// Журнал (только из не-RT методов). C:\ProgramData\Golos\apo.log

static void dataPath(wchar_t* out, size_t cap, const wchar_t* file) {
    wchar_t pd[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) lstrcpyW(pd, L"C:\\ProgramData");
    _snwprintf(out, cap, L"%s\\Golos\\%s", pd, file);
    out[cap - 1] = 0;
}

static void apoLog(const char* fmt, ...) {
    wchar_t path[MAX_PATH];
    dataPath(path, MAX_PATH, L"apo.log");
    HANDLE f = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER sz;
    if (GetFileSizeEx(f, &sz) && sz.QuadPart > 512 * 1024) {   // не даём журналу расти бесконечно
        CloseHandle(f);
        f = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) return;
    }
    char line[768];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int k = snprintf(line, sizeof line, "%04d-%02d-%02d %02d:%02d:%02d.%03d [%lu] ", t.wYear, t.wMonth, t.wDay, t.wHour,
                     t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentThreadId());
    va_list ap;
    va_start(ap, fmt);
    k += vsnprintf(line + k, sizeof line - k - 2, fmt, ap);
    va_end(ap);
    if (k > (int)sizeof line - 2) k = sizeof line - 2;
    line[k++] = '\r';
    line[k++] = '\n';
    DWORD w;
    WriteFile(f, line, k, &w, nullptr);
    CloseHandle(f);
}

static void guidStr(const GUID& g, char* out, size_t cap) {
    snprintf(out, cap, "{%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}", g.Data1, g.Data2, g.Data3, g.Data4[0],
             g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
}

// ---------------------------------------------------------------------------------------------
// Общая память: state.bin, отображённый в память (см. shared.h)

static uint8_t* mapShared() {
    wchar_t path[MAX_PATH];
    dataPath(path, MAX_PATH, L"state.bin");
    HANDLE f = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        apoLog("state.bin: open failed, err %lu — работаю прозрачно", GetLastError());
        return nullptr;
    }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz) || sz.QuadPart < (LONGLONG)kShmSize) {
        apoLog("state.bin: размер %lld < %u", (long long)sz.QuadPart, kShmSize);
        CloseHandle(f);
        return nullptr;
    }
    HANDLE m = CreateFileMappingW(f, nullptr, PAGE_READWRITE, 0, kShmSize, nullptr);
    CloseHandle(f);
    if (!m) {
        apoLog("state.bin: CreateFileMapping err %lu", GetLastError());
        return nullptr;
    }
    uint8_t* v = (uint8_t*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, kShmSize);
    CloseHandle(m);
    if (!v) {
        apoLog("state.bin: MapViewOfFile err %lu", GetLastError());
        return nullptr;
    }
    const ShmHeader* h = (const ShmHeader*)(v + OFF_HEADER);
    if (h->magic != kShmMagic || h->version != kShmVersion || h->size != kShmSize) {
        apoLog("state.bin: чужой формат (magic %08X ver %u size %u)", h->magic, h->version, h->size);
        UnmapViewOfFile(v);
        return nullptr;
    }
    VirtualLock(v, kShmSize);   // чтобы поток реального времени не ловил подкачку; не вышло — не страшно
    return v;
}

// ---------------------------------------------------------------------------------------------
// IAudioMediaType — в SDK его создаёт статическая audioenginebaseapo.lib, тут свой.

class MediaType final : public IAudioMediaType {
public:
    explicit MediaType(const UNCOMPRESSEDAUDIOFORMAT& u) {
        InterlockedIncrement(&g_objects);
        uf = u;
        memset(&wfx, 0, sizeof wfx);
        wfx.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        wfx.Format.nChannels = (WORD)u.dwSamplesPerFrame;
        wfx.Format.nSamplesPerSec = (DWORD)u.fFramesPerSecond;
        wfx.Format.wBitsPerSample = (WORD)(u.dwBytesPerSampleContainer * 8);
        wfx.Format.nBlockAlign = (WORD)(u.dwSamplesPerFrame * u.dwBytesPerSampleContainer);
        wfx.Format.nAvgBytesPerSec = wfx.Format.nSamplesPerSec * wfx.Format.nBlockAlign;
        wfx.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
        wfx.Samples.wValidBitsPerSample = (WORD)u.dwValidBitsPerSample;
        wfx.dwChannelMask = u.dwChannelMask;
        wfx.SubFormat = u.guidFormatType;
    }
    ~MediaType() { InterlockedDecrement(&g_objects); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IAudioMediaType_) {
            *ppv = static_cast<IAudioMediaType*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref); }
    ULONG STDMETHODCALLTYPE Release() override {
        LONG r = InterlockedDecrement(&ref);
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* p) override {
        if (!p) return E_POINTER;
        *p = FALSE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* other, DWORD* flags) override {
        if (!other || !flags) return E_POINTER;
        UNCOMPRESSEDAUDIOFORMAT o;
        HRESULT hr = other->GetUncompressedAudioFormat(&o);
        if (FAILED(hr)) return hr;
        *flags = 0;
        if (o.guidFormatType == uf.guidFormatType) *flags |= AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES;
        if (o.guidFormatType == uf.guidFormatType && o.dwSamplesPerFrame == uf.dwSamplesPerFrame &&
            o.dwBytesPerSampleContainer == uf.dwBytesPerSampleContainer && o.dwValidBitsPerSample == uf.dwValidBitsPerSample &&
            o.fFramesPerSecond == uf.fFramesPerSecond && o.dwChannelMask == uf.dwChannelMask)
            *flags |= AUDIOMEDIATYPE_EQUAL_FORMAT_DATA | AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA;
        return (*flags & AUDIOMEDIATYPE_EQUAL_FORMAT_DATA) ? S_OK : S_FALSE;
    }
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override { return &wfx.Format; }
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* p) override {
        if (!p) return E_POINTER;
        *p = uf;
        return S_OK;
    }

private:
    volatile LONG ref = 1;
    UNCOMPRESSEDAUDIOFORMAT uf;
    WAVEFORMATEXTENSIBLE wfx;
};

static bool isOurFormat(const UNCOMPRESSEDAUDIOFORMAT& f) {
    return f.guidFormatType == SUBTYPE_IEEE_FLOAT_ && f.dwBytesPerSampleContainer == 4 && f.dwValidBitsPerSample == 32 &&
           f.dwSamplesPerFrame >= 1 && f.dwSamplesPerFrame <= (DWORD)kMaxCh && f.fFramesPerSecond >= 8000.f &&
           f.fFramesPerSecond <= 384000.f;
}

// ---------------------------------------------------------------------------------------------

class GolosApo final : public IAudioProcessingObject,
                 public IAudioProcessingObjectRT,
                 public IAudioProcessingObjectConfiguration,
                 public IAudioSystemEffects2 {
public:
    // Неделегирующий IUnknown — на случай, если движок агрегирует APO.
    struct Inner : public IUnknown {
        GolosApo* owner = nullptr;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
            if (!ppv) return E_POINTER;
            if (riid == IID_IUnknown) {
                *ppv = static_cast<IUnknown*>(this);
                AddRef();
                return S_OK;
            }
            return owner->queryOwn(riid, ppv);
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&owner->ref); }
        ULONG STDMETHODCALLTYPE Release() override {
            LONG r = InterlockedDecrement(&owner->ref);
            if (r == 0) delete owner;
            return r;
        }
    } inner;

    explicit GolosApo(IUnknown* outerUnk) {
        InterlockedIncrement(&g_objects);
        inner.owner = this;
        outer = outerUnk ? outerUnk : static_cast<IUnknown*>(&inner);
        instanceId = (int32_t)((GetCurrentProcessId() << 12) ^ InterlockedIncrement(&g_instanceSeq));
        defaultParams(lastParams);
    }
    ~GolosApo() {
        unmap();
        InterlockedDecrement(&g_objects);
    }

    HRESULT queryOwn(REFIID riid, void** ppv) {
        if (riid == IID_IAudioProcessingObject_) *ppv = static_cast<IAudioProcessingObject*>(this);
        else if (riid == IID_IAudioProcessingObjectRT_) *ppv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (riid == IID_IAudioProcessingObjectConfiguration_) *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (riid == IID_IAudioSystemEffects_ || riid == IID_IAudioSystemEffects2_) *ppv = static_cast<IAudioSystemEffects2*>(this);
        else {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        static_cast<IUnknown*>(static_cast<IAudioProcessingObject*>(this))->AddRef();
        return S_OK;
    }

    // IUnknown (делегирующий)
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override { return outer->QueryInterface(riid, ppv); }
    ULONG STDMETHODCALLTYPE AddRef() override { return outer->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return outer->Release(); }

    // IAudioProcessingObject
    HRESULT STDMETHODCALLTYPE Reset() override {
        engine.reset();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* pTime) override {
        if (!pTime) return E_POINTER;
        *pTime = locked ? (HNSTIME)((double)engine.latencyFrames() / sampleRate * 1e7) : 0;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** pp) override {
        if (!pp) return E_POINTER;
        APO_REG_PROPERTIES* r = (APO_REG_PROPERTIES*)CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES));
        if (!r) return E_OUTOFMEMORY;
        fillRegProps(r);
        *pp = r;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Initialize(UINT32 cbDataSize, BYTE* pbyData) override {
        if (!pbyData && cbDataSize) return E_INVALIDARG;
        if (pbyData && !cbDataSize) return E_POINTER;
        if (locked) return APOERR_APO_LOCKED;
        char mode[48] = "-";
        bool discovery = false;
        if (pbyData && cbDataSize >= sizeof(APOInitSystemEffects2)) {
            const APOInitSystemEffects2* s = (const APOInitSystemEffects2*)pbyData;
            guidStr(s->AudioProcessingMode, mode, sizeof mode);
            modeData1 = s->AudioProcessingMode.Data1;
            discovery = s->InitializeForDiscoveryOnly != FALSE;
        }
        char ep[160] = "?";
        if (pbyData && cbDataSize >= sizeof(APOInitSystemEffects)) {
            const APOInitSystemEffects* s = (const APOInitSystemEffects*)pbyData;
            if (s->pAPOEndpointProperties) {
                PROPVARIANT v;
                PropVariantInit(&v);
                // PKEY_AudioEndpoint_GUID {1da5d803-d492-4edd-8c23-e0c0ffee7f0e},4
                PROPERTYKEY k = {{0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}}, 4};
                if (SUCCEEDED(s->pAPOEndpointProperties->GetValue(k, &v)) && v.vt == VT_LPWSTR)
                    WideCharToMultiByte(CP_UTF8, 0, v.pwszVal, -1, ep, sizeof ep, nullptr, nullptr);
                PropVariantClear(&v);
            }
        }
        epHash = ep[0] == '{' ? epHashOf(ep) : 0;
        apoLog("Initialize cb=%u endpoint=%s (#%08X) mode=%s discovery=%d", cbDataSize, ep, epHash, mode, discovery ? 1 : 0);
        initialized = true;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                                     IAudioMediaType** ppSupported) override {
        return formatSupported(pOpposite, pRequested, ppSupported);
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                                      IAudioMediaType** ppSupported) override {
        return formatSupported(pOpposite, pRequested, ppSupported);
    }

    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* p) override {
        if (!p) return E_POINTER;
        if (!locked) return APOERR_NOT_INITIALIZED;
        *p = (UINT32)channels;
        return S_OK;
    }

    // IAudioProcessingObjectConfiguration
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** ppIn, UINT32 nOut,
                                             APO_CONNECTION_DESCRIPTOR** ppOut) override {
        if (locked) return APOERR_APO_LOCKED;
        if (nIn != 1 || nOut != 1 || !ppIn || !ppOut || !ppIn[0] || !ppOut[0]) return APOERR_NUM_CONNECTIONS_INVALID;
        UNCOMPRESSEDAUDIOFORMAT fi, fo;
        HRESULT hr = ppIn[0]->pFormat->GetUncompressedAudioFormat(&fi);
        if (FAILED(hr)) return hr;
        hr = ppOut[0]->pFormat->GetUncompressedAudioFormat(&fo);
        if (FAILED(hr)) return hr;
        if (!isOurFormat(fi) || fi.dwSamplesPerFrame != fo.dwSamplesPerFrame || fi.fFramesPerSecond != fo.fFramesPerSecond) {
            apoLog("LockForProcess: формат не подходит (%lu ch, %.0f Hz, %lu bit)", fi.dwSamplesPerFrame, fi.fFramesPerSecond,
                 fi.dwValidBitsPerSample);
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        channels = (int)fi.dwSamplesPerFrame;
        sampleRate = fi.fFramesPerSecond;
        maxFrames = ppIn[0]->u32MaxFrameCount;
        engine.prepare(sampleRate, channels);

        unmap();
        shm = mapShared();
        lastSeq = -1;
        lastSp = nullptr;
        if (shm) {
            pullParams(true);
            ShmMeters* m = meters();
            InterlockedIncrement((volatile LONG*)&m->instances);
            m->apoVersion = kApoVersion;
            m->sampleRate = sampleRate;
            m->channels = channels;
            m->modeData1 = modeData1;
        }
        locked = true;
        apoLog("LockForProcess: %d ch, %.0f Hz, max %u frames, shm=%s, inst=%08X", channels, sampleRate, maxFrames,
             shm ? "ok" : "нет", (unsigned)instanceId);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (!locked) return APOERR_ALREADY_UNLOCKED;
        locked = false;
        unmap();
        apoLog("UnlockForProcess inst=%08X", (unsigned)instanceId);
        return S_OK;
    }

    // IAudioProcessingObjectRT — поток реального времени
    void STDMETHODCALLTYPE APOProcess(UINT32 nIn, APO_CONNECTION_PROPERTY** ppIn, UINT32 nOut,
                                      APO_CONNECTION_PROPERTY** ppOut) override {
        (void)nIn;
        (void)nOut;
        APO_CONNECTION_PROPERTY* in = ppIn[0];
        APO_CONNECTION_PROPERTY* out = ppOut[0];
        const UINT32 frames = in->u32ValidFrameCount;
        float* src = (float*)in->pBuffer;
        float* dst = (float*)out->pBuffer;
        const bool silent = in->u32BufferFlags == BUFFER_SILENT;

        if (in->u32BufferFlags == BUFFER_VALID || silent) {
            const unsigned csr = _mm_getcsr();
            _mm_setcsr(csr | 0x8040);   // FTZ + DAZ: денормали в хвостах фильтров не тормозят поток

            if (silent) memset(dst, 0, sizeof(float) * frames * channels);
            else if (dst != src) memcpy(dst, src, sizeof(float) * frames * channels);

            if (shm) pullParams(false);

            Tap tap;
            ShmMeters* m = shm ? meters() : nullptr;
            bool writeMeters = false;
            int64_t now = 0;
            if (m) {
                now = (int64_t)GetTickCount64();
                const uint32_t want = slots()->meterEp;   // окно смотрит на конкретный микрофон — пишет только он
                // state.bin переживает перезагрузку, а счётчик времени — нет: пульс «из будущего» тоже считаем старым
                const int64_t age = now - m->heartbeatMs;
                writeMeters = (want == 0 || want == epHash) && (m->writer == instanceId || age > 300 || age < 0);
                if (writeMeters) {
                    tap.pre = (float*)(shm + OFF_RING_PRE);
                    tap.post = (float*)(shm + OFF_RING_POST);
                    tap.mask = kRingSize - 1;
                    tap.pos = (uint32_t)m->ringPos;
                }
            }

            engine.process(dst, (int)frames, writeMeters ? &tap : nullptr);

            if (writeMeters) {
                m->writer = instanceId;
                m->ringPos = (int32_t)tap.pos;
                m->gateDb = engine.gateDb;
                m->compGrDb = engine.compGrDb;
                m->deessGrDb = engine.deessGrDb;
                m->limGrDb = engine.limGrDb;
                for (int k = 0; k < 4; k++) {
                    m->mbInDb[k] = engine.mbInDb[k];
                    m->mbGrDb[k] = engine.mbGrDb[k];
                }
                m->tuneHz = engine.tune.meterHz;
                m->tuneMidi = engine.tune.meterMidi;
                m->tuneTarget = engine.tune.meterTarget;
                m->tuneShift = engine.tune.meterShift;
                m->bypass = engine.bypassed() ? 1 : 0;
                m->processCount = m->processCount + 1;
                m->heartbeatMs = now;
            }

            _mm_setcsr(csr);

            // тишину оставляем тишиной: некоторым драйверам важен флаг BUFFER_SILENT
            if (silent) memset(dst, 0, sizeof(float) * frames * channels);
        }
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = in->u32BufferFlags;
    }

    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 n) override { return n; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 n) override { return n; }

    // IAudioSystemEffects2
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE) override {
        if (!ppEffectsIds || !pcEffects) return E_POINTER;
        *ppEffectsIds = nullptr;
        *pcEffects = 0;
        return S_OK;
    }

    static void fillRegProps(APO_REG_PROPERTIES* r) {
        memset(r, 0, sizeof *r);
        r->clsid = CLSID_GolosApo;
        r->Flags = (APO_FLAG)(APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH | APO_FLAG_FRAMESPERSECOND_MUST_MATCH |
                              APO_FLAG_BITSPERSAMPLE_MUST_MATCH);
        lstrcpynW(r->szFriendlyName, kFriendlyName, 256);
        lstrcpynW(r->szCopyrightInfo, L"Victor Painer", 256);
        r->u32MajorVersion = 1;
        r->u32MinorVersion = 0;
        r->u32MinInputConnections = 1;
        r->u32MaxInputConnections = 1;
        r->u32MinOutputConnections = 1;
        r->u32MaxOutputConnections = 1;
        r->u32MaxInstances = 0xFFFFFFFF;
        r->u32NumAPOInterfaces = 1;
        r->iidAPOInterfaceList[0] = IID_IAudioProcessingObject_;
    }

private:
    volatile LONG ref = 1;
    IUnknown* outer = nullptr;
    Engine engine;
    bool initialized = false, locked = false;
    int channels = 1;
    float sampleRate = 48000;
    UINT32 maxFrames = 0;
    uint8_t* shm = nullptr;
    int32_t lastSeq = -1;
    int32_t instanceId = 0;
    uint32_t modeData1 = 0;
    uint32_t epHash = 0;
    ShmParams* lastSp = nullptr;
    float lastParams[P_COUNT];

    ShmMeters* meters() { return (ShmMeters*)(shm + OFF_METERS); }
    ShmSlots* slots() { return (ShmSlots*)(shm + OFF_SLOTS); }

    // свой слот, если окно завело его этому микрофону, иначе общие параметры (только чтение памяти — RT-безопасно)
    ShmParams* paramsBlock() {
        if (epHash) {
            ShmSlots* t = slots();
            for (int i = 0; i < kSlots; i++)
                if (t->e[i].active && t->e[i].ep == epHash) return (ShmParams*)(shm + OFF_SLOT_PARAMS + i * kSlotStride);
        }
        return (ShmParams*)(shm + OFF_PARAMS);
    }

    void unmap() {
        if (!shm) return;
        ShmMeters* m = meters();
        InterlockedDecrement((volatile LONG*)&m->instances);
        if (m->writer == instanceId) m->heartbeatMs = 0;   // отдаём индикаторы другому экземпляру сразу
        UnmapViewOfFile(shm);
        shm = nullptr;
    }

    // seqlock: окно делает seq нечётным, пишет, снова чётным. Берём снимок, только если seq не
    // менялся за время копирования — иначе попробуем в следующем блоке (через ~10 мс).
    void pullParams(bool instant) {
        ShmParams* sp = paramsBlock();
        if (sp != lastSp) { lastSp = sp; lastSeq = -1; }   // слот завели или убрали — перечитываем
        const int32_t s1 = sp->seq;
        if (s1 == lastSeq || (s1 & 1) || sp->count != P_COUNT) return;
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        float tmp[P_COUNT];
        memcpy(tmp, (const void*)sp->v, sizeof tmp);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (sp->seq != s1) return;
        memcpy(lastParams, tmp, sizeof tmp);
        engine.setParams(tmp, instant);
        lastSeq = s1;
    }

    HRESULT formatSupported(IAudioMediaType* pOpposite, IAudioMediaType* pRequested, IAudioMediaType** ppSupported) {
        if (!pRequested || !ppSupported) return E_POINTER;
        *ppSupported = nullptr;
        UNCOMPRESSEDAUDIOFORMAT req, opp;
        HRESULT hr = pRequested->GetUncompressedAudioFormat(&req);
        if (FAILED(hr)) return hr;
        bool haveOpp = false;
        if (pOpposite) {
            if (FAILED(pOpposite->GetUncompressedAudioFormat(&opp))) return E_INVALIDARG;
            haveOpp = true;
        }
        bool ok = isOurFormat(req);
        if (ok && haveOpp && (opp.dwSamplesPerFrame != req.dwSamplesPerFrame || opp.fFramesPerSecond != req.fFramesPerSecond))
            ok = false;
        if (ok) {
            pRequested->AddRef();
            *ppSupported = pRequested;
            return S_OK;
        }
        // предлагаем тот же поток, но во float32 и с параметрами противоположной стороны
        UNCOMPRESSEDAUDIOFORMAT s = haveOpp ? opp : req;
        s.guidFormatType = SUBTYPE_IEEE_FLOAT_;
        s.dwBytesPerSampleContainer = 4;
        s.dwValidBitsPerSample = 32;
        if (s.dwSamplesPerFrame < 1 || s.dwSamplesPerFrame > (DWORD)kMaxCh || s.fFramesPerSecond < 8000.f ||
            s.fFramesPerSecond > 384000.f)
            return APOERR_FORMAT_NOT_SUPPORTED;
        MediaType* mt = new (std::nothrow) MediaType(s);
        if (!mt) return E_OUTOFMEMORY;
        *ppSupported = mt;
        return S_FALSE;
    }
};

// ---------------------------------------------------------------------------------------------

class Factory : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (pOuter && riid != IID_IUnknown) return CLASS_E_NOAGGREGATION;
        GolosApo* apo = new (std::nothrow) GolosApo(pOuter);
        if (!apo) return E_OUTOFMEMORY;
        HRESULT hr = apo->inner.QueryInterface(riid, ppv);
        apo->inner.Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static Factory g_factory;

// ---------------------------------------------------------------------------------------------
// Регистрация (regsvr32): COM-класс + описание APO для аудиодвижка.

static LONG setStr(HKEY k, const wchar_t* name, const wchar_t* val) {
    return RegSetValueExW(k, name, 0, REG_SZ, (const BYTE*)val, (DWORD)((lstrlenW(val) + 1) * sizeof(wchar_t)));
}
static LONG setDword(HKEY k, const wchar_t* name, DWORD val) {
    return RegSetValueExW(k, name, 0, REG_DWORD, (const BYTE*)&val, sizeof val);
}

extern "C" HRESULT __stdcall DllRegisterServer() {
    wchar_t dll[MAX_PATH];
    if (!GetModuleFileNameW(g_module, dll, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    wchar_t key[256];
    HKEY k;
    _snwprintf(key, 256, L"Software\\Classes\\CLSID\\%s", kClsidStr);
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr)) return E_ACCESSDENIED;
    setStr(k, nullptr, kFriendlyName);
    RegCloseKey(k);
    _snwprintf(key, 256, L"Software\\Classes\\CLSID\\%s\\InprocServer32", kClsidStr);
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr)) return E_ACCESSDENIED;
    setStr(k, nullptr, dll);
    setStr(k, L"ThreadingModel", L"Both");
    RegCloseKey(k);

    APO_REG_PROPERTIES r;
    GolosApo::fillRegProps(&r);
    _snwprintf(key, 256, L"Software\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", kClsidStr);
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr)) return E_ACCESSDENIED;
    setStr(k, L"FriendlyName", r.szFriendlyName);
    setStr(k, L"Copyright", r.szCopyrightInfo);
    setDword(k, L"MajorVersion", r.u32MajorVersion);
    setDword(k, L"MinorVersion", r.u32MinorVersion);
    setDword(k, L"Flags", (DWORD)r.Flags);
    setDword(k, L"MinInputConnections", r.u32MinInputConnections);
    setDword(k, L"MaxInputConnections", r.u32MaxInputConnections);
    setDword(k, L"MinOutputConnections", r.u32MinOutputConnections);
    setDword(k, L"MaxOutputConnections", r.u32MaxOutputConnections);
    setDword(k, L"MaxInstances", r.u32MaxInstances);
    setDword(k, L"NumAPOInterfaces", r.u32NumAPOInterfaces);
    setStr(k, L"APOInterface0", L"{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}");
    RegCloseKey(k);
    return S_OK;
}

extern "C" HRESULT __stdcall DllUnregisterServer() {
    wchar_t key[256];
    _snwprintf(key, 256, L"Software\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", kClsidStr);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    _snwprintf(key, 256, L"Software\\Classes\\CLSID\\%s", kClsidStr);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    return S_OK;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_GolosApo) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

extern "C" BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = h;
        DisableThreadLibraryCalls(h);
    }
    return TRUE;
}
