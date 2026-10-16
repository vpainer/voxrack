using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Golos
{
    // Подключение APO к микрофону — через install-apo.ps1 с правами администратора (один UAC).
    static class Installer
    {
        public const string Clsid = "{BCC05645-2F4E-4C14-9C6E-24D2FCDE41DE}";
        const string CapRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";
        const string VSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
        const string VNoFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

        public sealed class Mic
        {
            public string id { get; set; }
            public string name { get; set; }
            public string device { get; set; }
            public bool installed { get; set; }
            public bool fxOff { get; set; }
            public bool isDefault { get; set; }
        }

        public static string InstalledDll => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Golos", "GolosAPO.dll");
        static string BundledDll => Path.Combine(AppContext.BaseDirectory, "GolosAPO.dll");
        static string Script => Path.Combine(AppContext.BaseDirectory, "install-apo.ps1");

        public static List<Mic> ListMics()
        {
            var list = new List<Mic>();
            using var en = new MMDeviceEnumerator();
            string defId = null;
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var m = new Mic { id = d.ID, isDefault = d.ID == defId };
                try { m.name = d.FriendlyName; m.device = d.DeviceFriendlyName; } catch { m.name = m.device = d.ID; }
                var guid = d.ID.Substring(d.ID.LastIndexOf('.') + 1);
                try
                {
                    using var fx = Registry.LocalMachine.OpenSubKey(CapRoot + guid + @"\FxProperties");
                    m.installed = string.Equals(fx?.GetValue(VSfx) as string, Clsid, StringComparison.OrdinalIgnoreCase);
                    m.fxOff = fx?.GetValue(VNoFx) is int v && v != 0;
                }
                catch { }
                list.Add(m);
                d.Dispose();
            }
            return list;
        }

        public static bool DllPresent => File.Exists(InstalledDll);

        // в окне лежит более свежая сборка APO, чем стоит в системе
        public static bool DllOutdated()
        {
            try
            {
                if (!File.Exists(InstalledDll) || !File.Exists(BundledDll)) return false;
                using var sha = SHA256.Create();
                byte[] a, b;
                using (var f = File.OpenRead(InstalledDll)) a = sha.ComputeHash(f);
                using (var f = File.OpenRead(BundledDll)) b = sha.ComputeHash(f);
                return !a.AsSpan().SequenceEqual(b);
            }
            catch { return false; }
        }

        // 0 — готово, 1223 — пользователь отказался в UAC, иначе код ошибки скрипта
        public static async Task<int> RunAsync(string deviceName, bool uninstall)
        {
            var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Script}\" -Name \"{deviceName.Replace("\"", "")}\"";
            if (uninstall) args += " -Uninstall";
            var psi = new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using var p = Process.Start(psi);
                await p.WaitForExitAsync();
                Log.Write($"install-apo {(uninstall ? "uninstall" : "install")} «{deviceName}» → {p.ExitCode}");
                return p.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return 1223;
            }
        }

        public static string InstallLogTail()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Golos", "install.log");
                var lines = File.ReadAllLines(path);
                foreach (var l in lines)
                    if (l.Contains("Exception") || l.Contains("TerminatingError")) return l.Trim();
            }
            catch { }
            return null;
        }
    }
}
