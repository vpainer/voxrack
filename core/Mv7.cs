using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Golos
{
    // Сам микрофон Shure MV7 (PID 1012): у него текстовая «консоль» на служебном HID-канале (MI_03).
    // Строка «meterMode on\n» в 65-байтном output-отчёте (report id 0), ответ — «meterMode=on».
    // Протокол разобран в github.com/Humblemonk/shurectl; список команд — `help` у самого микрофона.
    // Канал один на всех (окно, плагин Loupedeck, MOTIV), поэтому открываем его на время команды
    // и держим общий мьютекс.
    static class Mv7
    {
        public sealed class State
        {
            public bool connected { get; set; }
            public bool meter { get; set; }
            public bool dim { get; set; }
            public bool locked { get; set; }
            public bool muted { get; set; }
            public string fw { get; set; }
        }

        // имя общее с плагином Loupedeck: оба делят один HID-канал микрофона, поэтому не меняется
        static readonly Mutex channel = new Mutex(false, @"Local\ShureBooster.Mv7Hid");

        public static State Read()
        {
            var s = new State();
            Session(dev =>
            {
                s.connected = true;
                s.meter = Value(dev.Ask("meterMode"), "meterMode") == "on";
                s.dim = Value(dev.Ask("dimMode"), "dimMode") == "on";
                s.locked = Value(dev.Ask("lock"), "lock") == "on";
                s.muted = Value(dev.Ask("micMute"), "micMute") == "on";
                s.fw = Value(dev.Ask("fwVersion"), "fwVersion");
            });
            return s;
        }

        public static void SetLeds(bool meter, bool dim) => Session(dev =>
        {
            dev.Ask("meterMode " + (meter ? "on" : "off"));
            dev.Ask("dimMode " + (dim ? "on" : "off"));
        });

        public static void SetLock(bool on) => Session(dev => dev.Ask("lock " + (on ? "on" : "off")));

        public static void Identify() => Session(dev => dev.Ask("identify"));

        static string Value(List<string> lines, string name)
        {
            foreach (var l in lines)
                if (l.StartsWith(name + "=", StringComparison.Ordinal)) return l.Substring(name.Length + 1).Trim();
            return null;
        }

        static void Session(Action<Channel> work)
        {
            bool taken = false;
            try
            {
                try { taken = channel.WaitOne(2000); }
                catch (AbandonedMutexException) { taken = true; }
                if (!taken) return;
                using var dev = Channel.Open();
                if (dev != null) work(dev);
            }
            catch (Exception ex) { Log.Write("mv7: " + ex.Message); }
            finally { if (taken) channel.ReleaseMutex(); }
        }

        sealed class Channel : IDisposable
        {
            FileStream stream;
            int outLen, inLen;

            public static Channel Open()
            {
                foreach (var path in FindPaths(0x14ED, 0x1012))
                {
                    var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    if (!HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); continue; }
                    HidP_GetCaps(pp, out var caps);
                    HidD_FreePreparsedData(pp);
                    if (caps.UsagePage >= 0xFF00 && caps.OutputReportByteLength >= 65)
                        return new Channel { stream = new FileStream(h, FileAccess.ReadWrite, 64, true), outLen = caps.OutputReportByteLength, inLen = caps.InputReportByteLength };
                    h.Dispose();
                }
                return null;
            }

            public List<string> Ask(string line)
            {
                Read(40);   // хвосты чужих ответов, чтобы не принять их за свой
                var pkt = new byte[outLen];
                var text = Encoding.ASCII.GetBytes(line + "\n");
                Array.Copy(text, 0, pkt, 1, Math.Min(text.Length, outLen - 1));
                stream.Write(pkt, 0, pkt.Length);
                return Read(250);
            }

            List<string> Read(int quietMs)
            {
                var sb = new StringBuilder();
                var buf = new byte[inLen];
                while (true)
                {
                    using var cts = new CancellationTokenSource(quietMs);
                    int n;
                    try { n = stream.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { break; }
                    if (n <= 1) break;
                    for (int i = 1; i < n; i++) if (buf[i] != 0) sb.Append((char)buf[i]);
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\n') quietMs = 30;   // строка пришла — ждём только хвост
                }
                var lines = new List<string>();
                foreach (var l in sb.ToString().Split('\n')) if (l.Trim().Length > 0) lines.Add(l.Trim());
                return lines;
            }

            public void Dispose() => stream?.Dispose();
        }

        static List<string> FindPaths(ushort vid, ushort pid)
        {
            var list = new List<string>();
            HidD_GetHidGuid(out var guid);
            var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            var key = $"vid_{vid:x4}&pid_{pid:x4}";
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                var buffer = Marshal.AllocHGlobal((int)need);
                Marshal.WriteInt32(buffer, 8);
                if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, need, out _, IntPtr.Zero))
                {
                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (path != null && path.Contains(key, StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
                Marshal.FreeHGlobal(buffer);
            }
            SetupDiDestroyDeviceInfoList(set);
            return list;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint need, IntPtr dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    }
}
