using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Golos
{
    // Звук на стороне окна:
    //  • «держатель» — свой поток с микрофона, пока окно открыто: APO работает и рисует индикаторы,
    //    даже если больше никто микрофон не слушает; заодно это источник для «Слушать себя»;
    //  • прослушка дубля — записанный сухой кусок, прогнанный через ту же цепочку (GolosDsp.dll).
    sealed class AudioHost : IDisposable
    {
        readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
        MMDevice mic;
        WasapiCapture capture;
        WasapiOut output;
        BufferedWaveProvider monitorBuf;
        TakePlayer take;
        string deviceId;
        volatile bool monitorOn;
        DateTime nextRetry;

        public string DeviceId => deviceId;
        public bool CaptureRunning => capture != null;
        public bool MonitorOn => monitorOn;
        public bool TakePlaying => take != null && output != null;
        public string LastError { get; private set; }

        public void SelectDevice(string id)
        {
            if (id == deviceId && capture != null) return;
            StopCapture();
            deviceId = id;
            nextRetry = DateTime.MinValue;
            EnsureCapture();
        }

        // зовётся по таймеру: поднимает поток снова, если микрофон переподключали
        public void EnsureCapture()
        {
            if (capture != null || deviceId == null || DateTime.Now < nextRetry) return;
            try
            {
                mic = enumerator.GetDevice(deviceId);
                if (mic.State != DeviceState.Active) throw new InvalidOperationException("микрофон не подключён");
                var c = new WasapiCapture(mic, true, 20) { ShareMode = AudioClientShareMode.Shared };
                c.DataAvailable += OnData;
                c.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null) Log.Write("capture stopped: " + e.Exception.Message);
                    if (capture == c) { capture = null; nextRetry = DateTime.Now.AddSeconds(2); }
                };
                c.StartRecording();
                capture = c;
                LastError = null;
                if (monitorOn) StartMonitorOutput();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                capture = null;
                nextRetry = DateTime.Now.AddSeconds(2);
            }
        }

        void StopCapture()
        {
            var c = capture;
            capture = null;
            if (c != null)
            {
                try { c.StopRecording(); } catch { }
                c.Dispose();
            }
        }

        void OnData(object sender, WaveInEventArgs e)
        {
            var buf = monitorBuf;
            if (!monitorOn || buf == null) return;
            // держим задержку прослушки короткой: накопилось больше 80 мс — сбрасываем
            if (buf.BufferedDuration.TotalMilliseconds > 80) buf.ClearBuffer();
            buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }

        public void SetMonitor(bool on)
        {
            if (on == monitorOn) return;
            StopOutput();
            monitorOn = on;
            if (on) StartMonitorOutput();
        }

        void StartMonitorOutput()
        {
            if (capture == null) return;
            StopOutput();
            var wf = capture.WaveFormat;
            monitorBuf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, wf.Channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(400),
                ReadFully = true,
            };
            ISampleProvider src = monitorBuf.ToSampleProvider();
            if (wf.Channels == 1) src = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(src);
            StartOutput(src, 30);
        }

        public void PlayTake(float[] samples, float fs, float[] parameters, bool dry)
        {
            monitorOn = false;
            StopOutput();
            take = new TakePlayer(samples, fs, parameters) { Dry = dry };
            StartOutput(take, 60);
        }

        public void StopTake()
        {
            if (take == null) return;
            StopOutput();
        }

        public void SetTakeDry(bool dry)
        {
            if (take != null) take.Dry = dry;
        }

        public void SetParams(float[] p) => take?.SetParams(p);

        public float TakePosition => take?.Position ?? 0;

        void StartOutput(ISampleProvider src, int latencyMs)
        {
            try
            {
                var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
                o.Init(src);
                o.Play();
                output = o;
            }
            catch (Exception ex)
            {
                LastError = "вывод звука: " + ex.Message;
                Log.Write(LastError);
                output = null;
            }
        }

        void StopOutput()
        {
            var o = output;
            output = null;
            if (o != null)
            {
                try { o.Stop(); } catch { }
                o.Dispose();
            }
            monitorBuf = null;
            take?.Dispose();
            take = null;
        }

        // кто сейчас слушает микрофон (кроме нас): имена процессов из аудиосессий устройства
        public List<string> Listeners()
        {
            var names = new List<string>();
            try
            {
                if (mic == null) return names;
                var sessions = mic.AudioSessionManager.Sessions;
                int self = Environment.ProcessId;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    int pid = (int)s.GetProcessID;
                    if (pid == self || pid == 0) continue;
                    string name;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        name = p.MainModule?.FileVersionInfo.FileDescription;
                        if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                    }
                    catch
                    {
                        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
                        catch { continue; }
                    }
                    if (!names.Contains(name)) names.Add(name);
                }
            }
            catch { }
            return names;
        }

        public void Dispose()
        {
            StopOutput();
            StopCapture();
            enumerator.Dispose();
        }
    }

    // Дубль по кругу через нативную цепочку. Параметры подменяются атомарно ссылкой —
    // аудиопоток их подхватывает на следующем блоке.
    sealed class TakePlayer : ISampleProvider, IDisposable
    {
        readonly float[] samples;
        readonly IntPtr engine;
        float[] pending;
        float[] mono = new float[4096];
        int pos;
        bool disposed;
        readonly object gate = new object();
        public volatile bool Dry;

        public WaveFormat WaveFormat { get; }
        public float Position => samples.Length == 0 ? 0 : pos / (float)samples.Length;

        public TakePlayer(float[] samples, float fs, float[] parameters)
        {
            this.samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)fs, 2);
            engine = Native.golos_create(fs, 1);
            if (engine != IntPtr.Zero) Native.golos_set(engine, parameters, 1);
        }

        public void SetParams(float[] p) => Volatile.Write(ref pending, (float[])p.Clone());

        public int Read(float[] buffer, int offset, int count)
        {
            lock (gate) return ReadLocked(buffer, offset, count);
        }

        int ReadLocked(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            if (disposed) { Array.Clear(buffer, offset, count); return count; }
            if (mono.Length < frames) mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                mono[i] = samples.Length > 0 ? samples[pos] : 0;
                if (++pos >= samples.Length) pos = 0;
            }
            var p = Interlocked.Exchange(ref pending, null);
            if (engine != IntPtr.Zero)
            {
                if (p != null) Native.golos_set(engine, p, 0);
                if (!Dry) Native.golos_process(engine, mono, frames);
            }
            for (int i = 0; i < frames; i++)
            {
                buffer[offset + 2 * i] = mono[i];
                buffer[offset + 2 * i + 1] = mono[i];
            }
            return frames * 2;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (engine != IntPtr.Zero) Native.golos_destroy(engine);
            }
        }
    }

    static class Native
    {
        const string Dll = "GolosDsp.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr golos_create(float fs, int ch);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void golos_destroy(IntPtr h);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void golos_set(IntPtr h, float[] p, int instant);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void golos_process(IntPtr h, float[] buf, int frames);
    }
}
