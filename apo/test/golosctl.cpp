// golosctl — служебная утилита для проверки APO на живом микрофоне.
//   golosctl init [--force]          создать %ProgramData%\Golos\state.bin (параметры по умолчанию)
//   golosctl set <idx> <val> ...     поменять параметры на лету (seqlock, как окно «Голоса»)
//   golosctl get                     показать параметры
//   golosctl meters                  что пишет APO: пульс, экземпляры, подавление по блокам
//   golosctl listen <sec> [--comms|--raw] [name]  открыть микрофон (по умолчанию «MV7») и печатать уровень
#define INITGUID
#include <windows.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "../params.h"
#include "../shared.h"

using namespace golos;

static void statePath(char* out, size_t cap) {
    char pd[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("ProgramData", pd, MAX_PATH);
    if (!n || n >= MAX_PATH) strcpy(pd, "C:\\ProgramData");
    snprintf(out, cap, "%s\\Golos\\state.bin", pd);
}

static uint8_t* mapState(bool create) {
    char path[MAX_PATH];
    statePath(path, sizeof path);
    HANDLE f = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           create ? OPEN_ALWAYS : OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        printf("не открыть %s (err %lu)\n", path, GetLastError());
        return nullptr;
    }
    HANDLE m = CreateFileMappingA(f, nullptr, PAGE_READWRITE, 0, kShmSize, nullptr);
    CloseHandle(f);
    if (!m) {
        printf("CreateFileMapping err %lu\n", GetLastError());
        return nullptr;
    }
    uint8_t* v = (uint8_t*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, kShmSize);
    CloseHandle(m);
    return v;
}

static void writeParams(uint8_t* v, const float* p) {
    ShmParams* sp = (ShmParams*)(v + OFF_PARAMS);
    int32_t s = sp->seq;
    if (s & 1) s++;
    sp->seq = s + 1;
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    sp->count = P_COUNT;
    memcpy((void*)sp->v, p, sizeof(float) * P_COUNT);
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    sp->seq = s + 2;
}

static int cmdInit(bool force) {
    uint8_t* v = mapState(true);
    if (!v) return 1;
    ShmHeader* h = (ShmHeader*)v;
    if (!force && h->magic == kShmMagic && h->version == kShmVersion && h->size == kShmSize) {
        printf("state.bin уже в порядке\n");
        return 0;
    }
    memset(v, 0, kShmSize);
    h->magic = kShmMagic;
    h->version = kShmVersion;
    h->size = kShmSize;
    float p[P_COUNT];
    defaultParams(p);
    writeParams(v, p);
    FlushViewOfFile(v, kShmSize);
    printf("state.bin создан\n");
    return 0;
}

static int cmdSet(int argc, char** argv) {
    uint8_t* v = mapState(false);
    if (!v) return 1;
    ShmParams* sp = (ShmParams*)(v + OFF_PARAMS);
    float p[P_COUNT];
    memcpy(p, (const void*)sp->v, sizeof p);
    for (int i = 0; i + 1 < argc; i += 2) {
        int idx = atoi(argv[i]);
        if (idx < 0 || idx >= P_COUNT) continue;
        p[idx] = (float)atof(argv[i + 1]);
        printf("p[%d] = %g\n", idx, p[idx]);
    }
    writeParams(v, p);
    return 0;
}

static int cmdGet() {
    uint8_t* v = mapState(false);
    if (!v) return 1;
    ShmParams* sp = (ShmParams*)(v + OFF_PARAMS);
    printf("seq %d count %d\n", sp->seq, sp->count);
    for (int i = 0; i < 82; i++) printf("%3d: %g%s", i, sp->v[i], (i % 6 == 5) ? "\n" : "\t");
    printf("\n");
    return 0;
}

static void printMeters(uint8_t* v) {
    ShmMeters* m = (ShmMeters*)(v + OFF_METERS);
    long long age = (long long)GetTickCount64() - m->heartbeatMs;
    printf("APO: пульс %s, экземпляров %d, writer %08X, pos %d, %.0f Hz x%d, gate %.1f comp %.1f ds %.1f lim %.1f, blocks %d, mode %08X\n",
           m->heartbeatMs == 0 ? "нет" : (age < 500 ? "живой" : "стоит"), m->instances, (unsigned)m->writer, m->ringPos,
           m->sampleRate, m->channels, m->gateDb, m->compGrDb, m->deessGrDb, m->limGrDb, m->processCount, m->modeData1);
}

static int cmdMeters() {
    uint8_t* v = mapState(false);
    if (!v) return 1;
    printMeters(v);
    return 0;
}

static int cmdListen(double seconds, const char* category, const wchar_t* match) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IMMDeviceEnumerator* en = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&en);
    if (FAILED(hr)) { printf("enumerator %08lX\n", hr); return 1; }
    IMMDeviceCollection* col = nullptr;
    en->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &col);
    UINT n = 0;
    col->GetCount(&n);
    IMMDevice* dev = nullptr;
    for (UINT i = 0; i < n && !dev; i++) {
        IMMDevice* d = nullptr;
        col->Item(i, &d);
        IPropertyStore* ps = nullptr;
        d->OpenPropertyStore(STGM_READ, &ps);
        PROPVARIANT pv;
        PropVariantInit(&pv);
        ps->GetValue(PKEY_Device_FriendlyName, &pv);
        if (pv.vt == VT_LPWSTR && wcsstr(pv.pwszVal, match)) {
            dev = d;
            wprintf(L"микрофон: %ls\n", pv.pwszVal);
        } else d->Release();
        PropVariantClear(&pv);
        ps->Release();
    }
    if (!dev) { wprintf(L"не нашёл микрофон с «%ls»\n", match); return 1; }

    IAudioClient2* ac = nullptr;
    hr = dev->Activate(__uuidof(IAudioClient2), CLSCTX_ALL, nullptr, (void**)&ac);
    if (FAILED(hr)) { printf("Activate %08lX\n", hr); return 1; }
    if (category) {
        AudioClientProperties props = {};
        props.cbSize = sizeof props;
        props.eCategory = strcmp(category, "comms") == 0 ? AudioCategory_Communications : AudioCategory_Other;
        props.Options = strcmp(category, "raw") == 0 ? AUDCLNT_STREAMOPTIONS_RAW : AUDCLNT_STREAMOPTIONS_NONE;
        hr = ac->SetClientProperties(&props);
        printf("категория %s: %08lX\n", category, hr);
    }
    WAVEFORMATEX* wf = nullptr;
    ac->GetMixFormat(&wf);
    printf("формат: %u ch, %lu Hz, %u bit\n", wf->nChannels, wf->nSamplesPerSec, wf->wBitsPerSample);
    hr = ac->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 2000000, 0, wf, nullptr);
    if (FAILED(hr)) { printf("Initialize %08lX\n", hr); return 1; }
    IAudioCaptureClient* cc = nullptr;
    ac->GetService(__uuidof(IAudioCaptureClient), (void**)&cc);
    ac->Start();

    uint8_t* shm = mapState(false);
    const int ch = wf->nChannels;
    DWORD t0 = GetTickCount();
    double acc = 0;
    float peak = 0;
    long cnt = 0;
    DWORD lastPrint = t0;
    while (GetTickCount() - t0 < seconds * 1000) {
        Sleep(10);
        UINT32 pkt = 0;
        while (SUCCEEDED(cc->GetNextPacketSize(&pkt)) && pkt) {
            BYTE* data;
            UINT32 frames;
            DWORD flags;
            if (FAILED(cc->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) break;
            const float* f = (const float*)data;
            for (UINT32 i = 0; i < frames; i++) {
                float s = (flags & AUDCLNT_BUFFERFLAGS_SILENT) ? 0 : f[i * ch];
                acc += (double)s * s;
                if (fabsf(s) > peak) peak = fabsf(s);
                cnt++;
            }
            cc->ReleaseBuffer(frames);
        }
        if (GetTickCount() - lastPrint >= 500) {
            lastPrint = GetTickCount();
            double rms = cnt ? sqrt(acc / cnt) : 0;
            printf("уровень: RMS %6.1f dBFS, пик %6.1f dBFS | ", 20 * log10(rms + 1e-9), 20 * log10(peak + 1e-9));
            if (shm) printMeters(shm); else printf("\n");
            acc = 0; peak = 0; cnt = 0;
        }
    }
    ac->Stop();
    return 0;
}

int main(int argc, char** argv) {
    if (argc < 2) {
        printf("golosctl init|set|get|meters|listen\n");
        return 1;
    }
    if (!strcmp(argv[1], "init")) return cmdInit(argc > 2 && !strcmp(argv[2], "--force"));
    if (!strcmp(argv[1], "set")) return cmdSet(argc - 2, argv + 2);
    if (!strcmp(argv[1], "get")) return cmdGet();
    if (!strcmp(argv[1], "meters")) return cmdMeters();
    if (!strcmp(argv[1], "listen")) {
        double sec = argc > 2 ? atof(argv[2]) : 5;
        const char* cat = nullptr;
        wchar_t match[128] = L"MV7";
        for (int i = 3; i < argc; i++) {
            if (!strcmp(argv[i], "--comms")) cat = "comms";
            else if (!strcmp(argv[i], "--raw")) cat = "raw";
            else MultiByteToWideChar(CP_UTF8, 0, argv[i], -1, match, 128);
        }
        return cmdListen(sec, cat, match);
    }
    printf("неизвестная команда\n");
    return 1;
}
