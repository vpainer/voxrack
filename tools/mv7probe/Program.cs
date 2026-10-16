// mv7probe — разговор с текстовой «консолью» Shure MV7 (PID 1012) по служебному HID-каналу.
// Команды шлются строкой: report id 0 + "текст\n" в 65-байтном output-отчёте; ответы — строками в input-отчётах.
//   mv7probe                → help + чтение текущих настроек (ничего не меняет)
//   mv7probe "<команда>" …  → отправить свои строки по очереди
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var dev = Mv7.Open();
        if (dev == null) { Console.WriteLine("MV7 (14ED:1012) не найден или канал занят"); return 1; }
        using (dev)
        {
            Console.WriteLine($"канал: {dev.Path}  (out {dev.OutLen}, in {dev.InLen})");
            var cmds = args.Length > 0 ? args : new[] { "help", "fwVersion", "meterMode", "dimMode", "micMute", "lock" };
            foreach (var c in cmds)
            {
                Console.WriteLine($"> {c}");
                foreach (var line in dev.Ask(c, c.StartsWith("help") ? 1200 : 400)) Console.WriteLine($"  {line}");
            }
        }
        return 0;
    }
}

sealed class Mv7 : IDisposable
{
    public string Path { get; private set; }
    public int OutLen { get; private set; }
    public int InLen { get; private set; }
    FileStream stream;

    public static Mv7 Open()
    {
        foreach (var path in Native.FindPaths(0x14ED, 0x1012))
        {
            var h = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!Native.HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); continue; }
            Native.HidP_GetCaps(pp, out var caps);
            Native.HidD_FreePreparsedData(pp);
            // служебный канал — vendor-defined коллекция с 65-байтными отчётами в обе стороны
            if (caps.UsagePage >= 0xFF00 && caps.OutputReportByteLength >= 65)
                return new Mv7 { Path = path, OutLen = caps.OutputReportByteLength, InLen = caps.InputReportByteLength, stream = new FileStream(h, FileAccess.ReadWrite, 64, true) };
            h.Dispose();
        }
        return null;
    }

    public List<string> Ask(string line, int quietMs)
    {
        Drain();
        var pkt = new byte[OutLen];
        var text = Encoding.ASCII.GetBytes(line + "\n");
        Array.Copy(text, 0, pkt, 1, Math.Min(text.Length, OutLen - 1));
        stream.Write(pkt, 0, pkt.Length);
        return ReadLines(quietMs);
    }

    void Drain() => ReadLines(60);

    List<string> ReadLines(int quietMs)
    {
        var sb = new StringBuilder();
        var buf = new byte[InLen];
        while (true)
        {
            using var cts = new CancellationTokenSource(quietMs);
            int n;
            try { n = stream.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { break; }
            if (n <= 1) break;
            for (int i = 1; i < n; i++) if (buf[i] != 0) sb.Append((char)buf[i]);
        }
        var lines = new List<string>();
        foreach (var l in sb.ToString().Split('\n')) if (l.Trim().Length > 0) lines.Add(l.TrimEnd('\r'));
        return lines;
    }

    public void Dispose() => stream?.Dispose();
}

static class Native
{
    public static List<string> FindPaths(ushort vid, ushort pid)
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
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("setupapi.dll")] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint need, IntPtr dev);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
}
