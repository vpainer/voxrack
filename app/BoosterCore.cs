using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Golos;

// Мозг окна (порт app/Core.cs без JSON): пишет параметры в state.bin, 30 раз в секунду отдаёт индикаторы
// и спектр, раз в секунду — состояние (микрофоны, установка, кто слушает). Всё на потоке окна.
sealed class BoosterCore : IDisposable
{
    const int TakeSeconds = 15;

    public sealed class Frame
    {
        public bool Alive;
        public float Fs = 48000;
        public float InPk = -120, InRms = -120, OutPk = -120, OutRms = -120;
        public float Gate, Comp, Ds, Lim;
        public readonly float[] MbIn = new float[4], MbGr = new float[4];
        public float TuneHz, TuneMidi, TuneTarget, TuneShift;
        public readonly float[] Spec = new float[Analyzer.Bins], Pre = new float[Analyzer.Bins];
        public bool MatchRec;
        public float MatchLen;
        public int MatchMax;
        public bool TakeRec, TakePlaying;
        public float TakeLen, TakePos;
        public int TakeMax = TakeSeconds;
    }

    public sealed class Status
    {
        public List<Installer.Mic> Mics = new();
        public string Selected;
        public bool Installed, FxOff, Dll, Outdated, Shm, Alive, Capture, Monitor, Own, Busy;
        public int Instances;
        public List<string> Listeners = new();
        public string CaptureError;
        public Installer.Mic SelectedMic => Mics.FirstOrDefault(m => m.id == Selected);
    }

    public event Action<Frame> FrameReady;
    public event Action<Status> StatusReady;
    public event Action<float[]> External;            // параметры поменяли снаружи (плагин Loupedeck) или сменился микрофон
    public event Action<Mv7.State> Mv7Ready;
    public event Action<string, string> MatchState;    // rec | analyze | error | applied, текст ошибки
    public event Action<EqMatch.Result> MatchResult;
    public event Action<string, string, string> Busy;  // install|uninstall, start|done|cancel|error, текст

    readonly DispatcherQueue ui;
    readonly DispatcherQueueTimer frameTimer, statusTimer;
    readonly Analyzer analyzer = new();
    readonly AudioHost audio = new();
    readonly float[] ringBuf = new float[Shm.RingSize];
    readonly float[] fftBuf = new float[Analyzer.N];
    readonly Frame frame = new();

    Shm shm;
    float[] parameters;
    int knownSeq = -1;   // seq последней известной окну записи параметров
    uint lastPos;
    bool busy;
    List<Installer.Mic> mics = new();
    Settings settings;
    uint curEp;          // выбранный микрофон: хеш его endpoint GUID (слот «своих настроек» и индикаторы)

    // дубль: сухой сигнал из кольца «до обработки»
    float[] take;
    int takeLen;
    bool recording;
    float takeFs = 48000;

    // EQ Match: эталон пишем отдельным потоком (как его слышат программы), цель — из кольца «до обработки»
    NAudio.CoreAudioApi.WasapiCapture matchCap;
    readonly List<float> matchRef = new();
    readonly object matchGate = new();
    float[] matchTgt;
    int matchTgtLen, matchSeconds;
    float matchRefFs, matchTgtFs;
    bool matching;
    EqMatch.Result matchResult;

    public float[] Parameters => parameters;
    public Status Last { get; private set; } = new();

    public BoosterCore(DispatcherQueue ui)
    {
        this.ui = ui;
        settings = Settings.Load();
        shm = Shm.TryOpen();
        parameters = shm?.ReadParams();
        if (shm != null) knownSeq = shm.ReadSeq();
        WriteAppInfo();

        frameTimer = ui.CreateTimer();
        frameTimer.Interval = TimeSpan.FromMilliseconds(33);
        frameTimer.Tick += (_, _) => Tick();
        statusTimer = ui.CreateTimer();
        statusTimer.Interval = TimeSpan.FromSeconds(1);
        statusTimer.Tick += (_, _) => PostStatus();

        RefreshMics();
        audio.SelectDevice(SelectedMic()?.id);
        UseDeviceSlot(false);
    }

    public void Start()
    {
        frameTimer.Start();
        statusTimer.Start();
        PostStatus();
    }

    Installer.Mic SelectedMic()
    {
        return mics.FirstOrDefault(m => m.id == settings.DeviceId)
            ?? mics.FirstOrDefault(m => m.device != null && m.device.Contains("MV7"))
            ?? mics.FirstOrDefault(m => m.isDefault)
            ?? mics.FirstOrDefault();
    }

    static string GuidOf(string deviceId) => deviceId?.Substring(deviceId.LastIndexOf('.') + 1).ToUpperInvariant();

    // окно правит настройки выбранного микрофона: свой слот, если он есть, иначе общие
    void UseDeviceSlot(bool notify)
    {
        if (shm == null) return;
        var mic = SelectedMic();
        curEp = mic == null ? 0 : Shm.EpHash(GuidOf(mic.id));
        shm.SetMeterEp(curEp);
        shm.Slot = curEp == 0 ? -1 : shm.FindSlot(curEp);
        parameters = shm.ReadParams();
        knownSeq = shm.ReadSeq();
        lastPos = 0;
        if (notify)
        {
            audio.SetParams(parameters);
            External?.Invoke(parameters);
        }
    }

    void RefreshMics()
    {
        try { mics = Installer.ListMics(); }
        catch (Exception ex) { Log.Write("mics: " + ex.Message); }
    }

    // ── команды окна ──

    public void SetParams(float[] v)
    {
        parameters = (float[])v.Clone();
        if (shm != null) knownSeq = shm.WriteParams(parameters);
        audio.SetParams(parameters);
    }

    public void SelectDevice(string id)
    {
        if (id == null || id == SelectedMic()?.id) return;
        settings = Settings.Load();
        settings.DeviceId = id;
        settings.Save();
        audio.SelectDevice(id);
        UseDeviceSlot(true);
        PostStatus();
    }

    // свои настройки у этого микрофона (слот) или общие
    public void SetOwn(bool on)
    {
        if (shm != null && curEp != 0)
        {
            if (on) shm.CreateSlot(curEp, parameters ?? shm.ReadParams());
            else shm.DeleteSlot(curEp);
            UseDeviceSlot(true);
        }
        PostStatus();
    }

    public void SetMonitor(bool on)
    {
        audio.SetMonitor(on);
        PostStatus();
    }

    // сам микрофон MV7: подсветка, блокировка касаний, «мигнуть»; HID — не на потоке окна
    public void Mv7Get() => Mv7Do(null);
    public void Mv7Led(bool meter, bool dim) => Mv7Do(() => Mv7.SetLeds(meter, dim));
    public void Mv7Lock(bool on) => Mv7Do(() => Mv7.SetLock(on));
    public void Mv7Identify() => Mv7Do(Mv7.Identify);

    void Mv7Do(Action act)
    {
        Task.Run(() =>
        {
            act?.Invoke();
            var st = Mv7.Read();
            ui.TryEnqueue(() => Mv7Ready?.Invoke(st));
        });
    }

    // встроенные пресеты — файлом, чтобы их видел плагин Loupedeck
    public static void WriteBuiltins()
    {
        try
        {
            var list = Params.Presets.Select(pr => new { name = pr.Name, hint = pr.Hint, v = pr.Build() }).ToList();
            File.WriteAllText(Path.Combine(App.DataDir, "builtin-presets.json"), JsonSerializer.Serialize(list));
        }
        catch (Exception ex) { Log.Write("builtins: " + ex.Message); }
    }

    public void Install(bool uninstall) => _ = InstallAsync(uninstall);

    async Task InstallAsync(bool uninstall)
    {
        var mic = SelectedMic();
        if (busy || mic == null) return;
        busy = true;
        string what = uninstall ? "uninstall" : "install";
        Busy?.Invoke(what, "start", null);
        audio.SetMonitor(false);
        audio.StopTake();
        int code = await Installer.RunAsync(mic.device ?? mic.name, uninstall);
        // аудиослужбы перезапустились: переоткрываем общую память, поток поднимется сам
        await Task.Delay(1500);
        shm?.Dispose();
        shm = Shm.TryOpen();
        if (shm != null)
        {
            UseDeviceSlot(false);
            if (parameters != null) knownSeq = shm.WriteParams(parameters);
        }
        busy = false;
        string state = code == 0 ? "done" : code == 1223 ? "cancel" : "error";
        Busy?.Invoke(what, state, code == 0 || code == 1223 ? null : Installer.InstallLogTail() ?? L.S($"код {code}", $"code {code}"));
        PostStatus();
    }

    public void TakeRec()
    {
        audio.StopTake();
        take = new float[TakeSeconds * 192000];
        takeLen = 0;
        recording = true;
    }

    public void TakeStop()
    {
        if (!recording) return;
        recording = false;
        if (takeLen > takeFs * 0.5f) PlayTake(false);
    }

    public bool TakeRecording => recording;
    public void TakeDry(bool on) => audio.SetTakeDry(on);
    public void TakeHalt() => audio.StopTake();

    public void PlayTake(bool dry)
    {
        if (take == null || takeLen < 100) return;
        var clip = new float[takeLen];
        Array.Copy(take, clip, takeLen);
        audio.PlayTake(clip, takeFs, parameters ?? new float[Shm.ParamCount], dry);
    }

    // ── кадр ──

    void Tick()
    {
        audio.EnsureCapture();
        var f = frame;
        if (shm == null)
        {
            f.Alive = false;
            FrameReady?.Invoke(f);
            return;
        }
        // параметры поменял кто-то ещё (плагин Loupedeck) — показываем в окне
        int seq = shm.ReadSeq();
        if (seq != knownSeq && (seq & 1) == 0)
        {
            knownSeq = seq;
            parameters = shm.ReadParams();
            audio.SetParams(parameters);
            External?.Invoke(parameters);
        }

        shm.ClearStaleHeartbeat();
        var mt = shm.ReadMeters();
        long age = Environment.TickCount64 - mt.HeartbeatMs;
        bool alive = mt.HeartbeatMs > 0 && age >= 0 && age < 400;
        float fs = mt.SampleRate > 1000 ? mt.SampleRate : 48000;

        uint pos = mt.RingPos;
        int fresh = (int)Math.Min(pos - lastPos, (uint)Shm.RingSize);
        if (lastPos == 0 || pos < lastPos && lastPos - pos < 1u << 30) fresh = 0;   // первый кадр или смена писателя назад
        lastPos = pos;

        float inPk = -120, inRms = -120, outPk = -120, outRms = -120;
        if (alive && fresh > 0)
        {
            shm.CopyRing(false, pos, fresh, ringBuf);
            Analyzer.Levels(ringBuf, fresh, out inPk, out inRms);
            if (matching && matchTgt != null)
            {
                matchTgtFs = fs;
                int k = Math.Min(fresh, matchTgt.Length - matchTgtLen);
                if (k > 0) { Array.Copy(ringBuf, 0, matchTgt, matchTgtLen, k); matchTgtLen += k; }
                if (matchTgtLen >= (int)(matchSeconds * fs)) FinishMatch();
            }
            if (recording && take != null)
            {
                takeFs = fs;
                int n = Math.Min(fresh, (int)(TakeSeconds * fs) - takeLen);
                if (n > 0) { Array.Copy(ringBuf, 0, take, takeLen, n); takeLen += n; }
                if (takeLen >= (int)(TakeSeconds * fs)) { recording = false; PlayTake(false); }
            }
            shm.CopyRing(true, pos, fresh, ringBuf);
            Analyzer.Levels(ringBuf, fresh, out outPk, out outRms);
        }
        if (alive)
        {
            shm.CopyRing(true, pos, Analyzer.N, fftBuf);
            analyzer.Spectrum(fftBuf, fs, f.Spec);
            shm.CopyRing(false, pos, Analyzer.N, fftBuf);
            analyzer.Spectrum(fftBuf, fs, f.Pre);
        }

        f.Alive = alive;
        f.Fs = fs;
        f.InPk = inPk; f.InRms = inRms; f.OutPk = outPk; f.OutRms = outRms;
        f.Gate = mt.GateDb; f.Comp = mt.CompGrDb; f.Ds = mt.DeessGrDb; f.Lim = mt.LimGrDb;
        for (int i = 0; i < 4; i++)
        {
            f.MbIn[i] = mt.MbIn != null && i < mt.MbIn.Length ? mt.MbIn[i] : -120;
            f.MbGr[i] = mt.MbGr != null && i < mt.MbGr.Length ? mt.MbGr[i] : 0;
        }
        f.TuneHz = mt.TuneHz; f.TuneMidi = mt.TuneMidi; f.TuneTarget = mt.TuneTarget; f.TuneShift = mt.TuneShift;
        f.MatchRec = matching;
        f.MatchLen = matching ? matchTgtLen / fs : 0;
        f.MatchMax = matchSeconds;
        f.TakeRec = recording;
        f.TakeLen = takeLen / fs;
        f.TakePlaying = audio.TakePlaying;
        f.TakePos = audio.TakePosition;
        FrameReady?.Invoke(f);
    }

    // ── EQ Match ──

    public void MatchRec(string refId, int seconds)
    {
        var mic = SelectedMic();
        string error = null;
        if (shm == null || mic == null || !mic.installed) error = L.S("К целевому микрофону не подключена обработка", "Processing is not connected to the target microphone");
        else if (refId == null || refId == mic.id) error = L.S("Эталон и цель — один и тот же микрофон", "Reference and target are the same microphone");
        if (error == null)
        {
            try
            {
                StopMatchCapture();
                var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var dev = en.GetDevice(refId);
                var cap = new NAudio.CoreAudioApi.WasapiCapture(dev, true, 50);
                lock (matchGate) matchRef.Clear();
                matchRefFs = cap.WaveFormat.SampleRate;
                int ch = cap.WaveFormat.Channels;
                bool isFloat = cap.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                    || cap.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && cap.WaveFormat.BitsPerSample == 32;
                int bps = cap.WaveFormat.BitsPerSample / 8;
                cap.DataAvailable += (s, e) =>
                {
                    int frames = e.BytesRecorded / (bps * ch);
                    lock (matchGate)
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            float sum = 0;
                            for (int c = 0; c < ch; c++)
                            {
                                int o = (i * ch + c) * bps;
                                sum += isFloat ? BitConverter.ToSingle(e.Buffer, o)
                                    : bps == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f
                                    : bps == 4 ? BitConverter.ToInt32(e.Buffer, o) / 2147483648f : 0;
                            }
                            matchRef.Add(sum / ch);
                        }
                    }
                };
                matchSeconds = Math.Clamp(seconds, 5, 30);
                matchTgt = new float[matchSeconds * 192000];
                matchTgtLen = 0;
                matchCap = cap;
                cap.StartRecording();
                matching = true;
                MatchState?.Invoke("rec", null);
            }
            catch (Exception ex)
            {
                error = L.S("Эталон не открылся: ", "Could not open the reference: ") + ex.Message;
                StopMatchCapture();
            }
        }
        if (error != null) MatchState?.Invoke("error", error);
    }

    public void MatchStop()
    {
        if (matching) FinishMatch();
    }

    void StopMatchCapture()
    {
        var c = matchCap;
        matchCap = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
    }

    void FinishMatch()
    {
        matching = false;
        StopMatchCapture();
        float[] refSig;
        lock (matchGate) refSig = matchRef.ToArray();
        var tgt = new float[matchTgtLen];
        Array.Copy(matchTgt, tgt, matchTgtLen);
        float rfs = matchRefFs, tfs = matchTgtFs;
        MatchState?.Invoke("analyze", null);
        Task.Run(() =>
        {
            EqMatch.Result r = null;
            string error = null;
            try
            {
                if (refSig.Length < rfs * 3 || tgt.Length < tfs * 3) error = L.S("Запись слишком короткая", "The recording is too short");
                else r = EqMatch.Analyze(refSig, rfs, tgt, tfs);
            }
            catch (Exception ex) { error = ex.Message; Log.Write("match: " + ex); }
            ui.TryEnqueue(() =>
            {
                matchResult = r;
                if (r != null) MatchResult?.Invoke(r);
                else MatchState?.Invoke("error", error);
            });
        });
    }

    // результат — в настройки цели; у цели заводим свой слот, чтобы не задеть остальные микрофоны
    public void MatchApply(bool level, bool eq, bool dyn, bool noise, float amount)
    {
        var r = matchResult;
        if (r == null || shm == null || curEp == 0) return;
        if (r.speechSec < 3)
        {
            MatchState?.Invoke("error", L.S("В записи почти нет речи — применять нельзя", "Almost no speech in the recording — can’t apply"));
            return;
        }
        amount = Math.Clamp(amount, 0, 1);
        if (shm.Slot < 0)
        {
            shm.CreateSlot(curEp, parameters ?? shm.ReadParams());
            shm.Slot = shm.FindSlot(curEp);
        }
        var p = shm.ReadParams();
        if (level) p[P.IN_GAIN] = r.gainDb;
        if (eq)
        {
            p[P.EQ_ON] = 1;
            for (int b = 0; b < P.EQ_BANDS; b++)
            {
                int o = P.EQ_BASE + b * P.EQ_STRIDE;
                if (b < r.bands.Count)
                {
                    var x = r.bands[b];
                    p[o] = 1; p[o + 1] = x.type; p[o + 2] = x.freq; p[o + 3] = MathF.Round(x.gain * amount * 10) / 10; p[o + 4] = x.q;
                }
                else p[o] = 0;
            }
        }
        if (dyn && r.comp != null)
        {
            p[P.COMP_ON] = 1; p[P.COMP_THRESH] = r.comp.thresh; p[P.COMP_RATIO] = r.comp.ratio; p[P.COMP_KNEE] = 6;
            p[P.COMP_ATTACK] = 10; p[P.COMP_RELEASE] = 150; p[P.COMP_MAKEUP] = r.comp.makeup; p[P.COMP_MIX] = 1; p[P.COMP_RMS] = 1;
        }
        if (noise && r.gate != null)
        {
            p[P.GATE_ON] = 1; p[P.GATE_THRESH] = r.gate.thresh; p[P.GATE_RANGE] = r.gate.range;
            p[P.GATE_ATTACK] = 2; p[P.GATE_HOLD] = 120; p[P.GATE_RELEASE] = 180;
        }
        knownSeq = shm.WriteParams(p);
        parameters = p;
        audio.SetParams(p);
        External?.Invoke(p);
        MatchState?.Invoke("applied", null);
        PostStatus();
    }

    // где лежит окно — чтобы плагин Loupedeck мог его открыть
    static void WriteAppInfo()
    {
        try
        {
            File.WriteAllText(Path.Combine(App.DataDir, "app.json"),
                JsonSerializer.Serialize(new { exe = Environment.ProcessPath, version = typeof(BoosterCore).Assembly.GetName().Version?.ToString(3) }));
        }
        catch { }
    }

    public void PostStatus()
    {
        RefreshMics();
        var mic = SelectedMic();
        int instances = 0;
        bool alive = false;
        if (shm == null) shm = Shm.TryOpen();
        if (shm != null)
        {
            var mt = shm.ReadMeters();
            instances = mt.Instances;
            long age = Environment.TickCount64 - mt.HeartbeatMs;
            alive = mt.HeartbeatMs > 0 && age >= 0 && age < 1500;
        }
        Last = new Status
        {
            Mics = mics,
            Selected = mic?.id,
            Installed = mic?.installed ?? false,
            FxOff = mic?.fxOff ?? false,
            Dll = Installer.DllPresent,
            Outdated = Installer.DllOutdated(),
            Shm = shm != null,
            Alive = alive,
            Instances = instances,
            Listeners = audio.Listeners(),
            Capture = audio.CaptureRunning,
            CaptureError = audio.LastError,
            Monitor = audio.MonitorOn,
            Own = shm != null && shm.Slot >= 0,
            Busy = busy,
        };
        StatusReady?.Invoke(Last);
    }

    public void Dispose()
    {
        frameTimer.Stop();
        statusTimer.Stop();
        StopMatchCapture();
        shm?.SetMeterEp(0);
        audio.Dispose();
        shm?.Dispose();
    }
}

sealed class Settings
{
    public string DeviceId { get; set; }
    // прежнее окно (WPF) — в DIP; новое хранит физические пиксели отдельно
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Topmost { get; set; }
    public int? WinX { get; set; }
    public int? WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public string Module { get; set; }
    public string Preset { get; set; }
    public string Lang { get; set; }      // system | ru | en
    public bool RailCompact { get; set; } // цепочка свёрнута до иконок
    public DateTime? LastUpdateCheck { get; set; }

    static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}

static class Presets
{
    public sealed class Item
    {
        public string name { get; set; }
        public float[] v { get; set; }
    }

    static string FilePath => Path.Combine(App.DataDir, "presets.json");

    public static List<Item> Load()
    {
        try { return JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(FilePath)) ?? new List<Item>(); }
        catch { return new List<Item>(); }
    }

    public static void Save(string name, float[] v)
    {
        var list = Load();
        list.RemoveAll(p => p.name == name);
        list.Add(new Item { name = name, v = v });
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }

    public static void Delete(string name)
    {
        var list = Load();
        list.RemoveAll(p => p.name == name);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}
