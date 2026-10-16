using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Golos;

public partial class App : Application
{
    static Mutex single;
    public static MainWindow Window { get; private set; }

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Golos");

    public App()
    {
        // числа на ручках и графиках — с точкой, как на железе
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // тема — как в Windows; VR_THEME=light|dark только для проверки вида (тот же путь, что у системной темы)
        var forced = Environment.GetEnvironmentVariable("VR_THEME");
        if (forced == "dark") RequestedTheme = ApplicationTheme.Dark;
        else if (forced == "light") RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Write("UI exception: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        single = new Mutex(true, "Golos.Window.Single", out bool first);
        if (!first)
        {
            // второе окно не нужно (оно открыло бы ещё один поток с микрофона): показываем первое
            var h = FindWindow(null, "VoxRack");
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9 /* SW_RESTORE */);
                SetForegroundWindow(h);
            }
            Exit();
            return;
        }
        Directory.CreateDirectory(DataDir);
        L.Init(Environment.GetEnvironmentVariable("VR_LANG") ?? Settings.Load().Lang);   // VR_LANG — для проверки вида
        // встроенные подписи WinUI (меню полей ввода и т. п.) — на том же языке
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = L.Ru ? "ru-RU" : "en-US"; }
        catch (Exception ex) { Log.Write("language override: " + ex.Message); }
        Window = new MainWindow();
        Window.Activate();
    }

    // перезапуск окна (смена языка): отпускаем «единственный экземпляр» до запуска нового
    public static void Restart()
    {
        try
        {
            single?.ReleaseMutex();
            single?.Dispose();
            single = null;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex) { Log.Write("restart: " + ex.Message); }
        Window?.Close();
        Current.Exit();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}

static class Log
{
    static readonly object gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (gate)
                File.AppendAllText(Path.Combine(App.DataDir, "golos.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\r\n");
        }
        catch { }
    }
}
