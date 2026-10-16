using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Golos.Graphs;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Golos;

// Окно: шапка, цепочка модулей, выбранный модуль, индикаторы. Модули — в MainWindow.Modules.cs.
public sealed partial class MainWindow : Window
{
    readonly BoosterCore core;
    readonly DispatcherQueue ui;
    readonly DispatcherQueueTimer toastTimer, mv7Timer;
    float[] p = Params.Defaults();
    readonly LiveState live = new();
    readonly MatchModel match = new();
    Mv7.State mv7;
    bool mv7Checked;
    string preset = "Без обработки";
    bool modified, sendQueued, syncing, takeDry;
    List<Presets.Item> userPresets = new();
    readonly List<Action> syncers = new();
    readonly Dictionary<string, ModulePanel> panels = new();
    readonly Dictionary<string, ChainItem> items = new();
    readonly ObservableCollection<ChainItem> topItems = new(), stageItems = new(), bottomItems = new();
    readonly MetersView meters = new();
    string current;
    float fs = 48000;
    Action bannerAct;

    public MainWindow()
    {
        InitializeComponent();
        ui = DispatcherQueue;
        Title = "VoxRack";

        // тема — как в Windows (App может подменить её для проверки вида через VR_THEME)
        var forced = Environment.GetEnvironmentVariable("VR_THEME");
        if (forced is not ("light" or "dark")) forced = null;
        bool light = Application.Current.RequestedTheme == ApplicationTheme.Light;
        Th.Set(light);
        Pal.Refresh(light);
        // кнопки «свернуть / развернуть / закрыть» — в тон теме окна, а не системной
        AppWindow.TitleBar.PreferredTheme = forced == null ? TitleBarTheme.UseDefaultAppMode : light ? TitleBarTheme.Light : TitleBarTheme.Dark;
        Root.ActualThemeChanged += (_, _) => ApplyTheme(Root.ActualTheme == ElementTheme.Light);
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); } catch { }
        RestorePlacement();
        AppWindow.Closing += (_, _) => SavePlacement();

        toastTimer = ui.CreateTimer();
        toastTimer.IsRepeating = false;
        toastTimer.Interval = TimeSpan.FromSeconds(3);
        toastTimer.Tick += (_, _) => ToastBar.IsOpen = false;

        core = new BoosterCore(ui);
        if (core.Parameters is { Length: P.COUNT } init) p = Params.Migrate((float[])init.Clone());
        userPresets = Presets.Load();

        MetersHost.Children.Add(meters.Host);
        BuildModules();
        BuildChain();

        core.FrameReady += OnFrame;
        core.StatusReady += RenderStatus;
        core.External += v =>
        {
            // параметры поменяли снаружи (плагин Loupedeck) или сменился микрофон — показываем, обратно не отправляем
            if (v is not { Length: P.COUNT }) return;
            p = Params.Migrate((float[])v.Clone());
            preset = GuessPreset();
            SyncAll();
        };
        core.Mv7Ready += st => { mv7 = st; mv7Checked = true; SyncMv7(); };
        core.MatchState += OnMatchState;
        core.MatchResult += r =>
        {
            match.Result = r;
            match.State = "done";
            match.Error = null;
            match.NotesShown = false;
            SyncMatch();
        };
        core.Busy += OnBusy;

        // пока открыт модуль MV7 — изредка спрашиваем микрофон (его могли переключить с Loupedeck или касанием)
        mv7Timer = ui.CreateTimer();
        mv7Timer.Interval = TimeSpan.FromSeconds(4);
        mv7Timer.Tick += (_, _) => { if (current == "mv7" && Visible) core.Mv7Get(); };
        mv7Timer.Start();

        // поменяли цвет акцента в Windows — графики перекрашиваются сразу (XAML-контролы делают это сами)
        Pal.Changed += () => ui.TryEnqueue(() =>
        {
            Pal.Refresh(Th.Light);
            foreach (var pn in panels.Values) pn.View?.Invalidate();
            meters.Invalidate();
        });

        Root.PreviewKeyDown += Root_PreviewKeyDown;
        Closed += (_, _) => core.Dispose();

        (L.Mode switch { "ru" => LangRu, "en" => LangEn, _ => LangSystem }).IsChecked = true;
        preset = GuessPreset();
        SyncAll();
        RenderTake(null);
        var st = Settings.Load();
        SetRailCompact(Environment.GetEnvironmentVariable("VR_RAIL") is { } rail ? rail == "compact" : st.RailCompact);
        Show(Environment.GetEnvironmentVariable("VR_MODULE") ?? st.Module ?? "comp");
        BoosterCore.WriteBuiltins();
        core.Start();

        // новая версия на GitHub — через несколько секунд после запуска, не чаще раза в 12 часов
        var upd = ui.CreateTimer();
        upd.Interval = TimeSpan.FromSeconds(6);
        upd.IsRepeating = false;
        upd.Tick += (_, _) => CheckUpdates(false);
        upd.Start();
    }

    // ── параметры ──

    void Changed()
    {
        if (syncing) return;
        // совпало с пресетом — показываем его имя, иначе «Пользовательский»
        preset = GuessPreset();
        UpdatePresetBtn();
        if (current != null && panels.TryGetValue(current, out var pn)) pn.View?.Invalidate();
        RenderRail();
        if (sendQueued) return;
        sendQueued = true;
        ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            sendQueued = false;
            core.SetParams(p);
        });
    }

    void SyncAll()
    {
        syncing = true;
        try
        {
            foreach (var f in syncers) f();
        }
        finally { syncing = false; }
        foreach (var pn in panels.Values) pn.View?.Invalidate();
        LayoutChain();
        RenderRail();
        UpdatePresetBtn();
        SyncPower();
    }

    // ── цепочка ──

    // иконки Segoe Fluent: в свёрнутой цепочке видны только они
    static readonly (string id, string name, int power, string gr, string glyph)[] Chain =
    {
        ("mv7", "MV7", -1, null, "\uE720"),             // микрофон
        ("match", "MATCH", -1, null, "\uE895"),         // синхронизация
        ("io", "I/O", -1, null, "\uE767"),              // громкость
        ("hpf", "HPF", P.HPF_ON, null, "\uE71C"),       // фильтр
        ("gate", "GATE", P.GATE_ON, "gate", "\uE72E"),  // замок: закрывает тишину
        ("tune", "TUNE", P.TUNE_ON, "tune", "\uE8D6"),  // ноты
        ("eq", "EQ", P.EQ_ON, null, "\uE9E9"),          // эквалайзер
        ("mb", "M-BAND", P.MB_ON, "mb", "\uE81E"),      // слои-полосы
        ("comp", "COMP", P.COMP_ON, "comp", "\uE73F"),  // стрелки внутрь
        ("ds", "DE-ESS", P.DS_ON, "ds", "\uE706"),      // яркость верха
        ("dly", "DELAY", P.DLY_ON, null, "\uE8EE"),     // повтор
        ("rev", "REVERB", P.REV_ON, null, "\uE80F"),    // комната
        ("lim", "LIMIT", P.LIM_ON, "lim", "\uE74A"),    // потолок
    };


    void BuildChain()
    {
        foreach (var (id, name, power, gr, glyph) in Chain)
        {
            var it = new ChainItem { Id = id, Name = name, Power = power, Gr = gr, Glyph = glyph, Tip = $"{name} · {panels[id].Title}" };
            items[id] = it;
        }
        topItems.Add(items["mv7"]);
        topItems.Add(items["match"]);
        topItems.Add(items["io"]);
        bottomItems.Add(items["lim"]);
        TopList.ItemsSource = topItems;
        StageList.ItemsSource = stageItems;
        BottomList.ItemsSource = bottomItems;
        LayoutChain();
    }

    // порядок девяти модулей между I/O и LIMIT — из параметров; коллекцию не пересоздаём, чтобы не терять выделение
    void LayoutChain()
    {
        var order = Params.DecodeOrder(p);
        for (int i = 0; i < order.Length; i++)
        {
            var it = items[Params.Stages[order[i]]];
            int at = stageItems.IndexOf(it);
            if (at < 0) stageItems.Insert(i, it);
            else if (at != i) stageItems.Move(at, i);
        }
    }

    void ApplyOrder(int[] order)
    {
        Params.EncodeOrder(p, order);
        Changed();
        LayoutChain();
    }

    void MoveStage(string id, int dir)
    {
        var order = Params.DecodeOrder(p);
        int i = Array.IndexOf(order, Array.IndexOf(Params.Stages, id)), j = i + dir;
        if (i < 0 || j < 0 || j >= order.Length) return;
        (order[i], order[j]) = (order[j], order[i]);
        ApplyOrder(order);
        StageList.SelectedItem = items[id];
    }

    void StageList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var order = stageItems.Select(it => Array.IndexOf(Params.Stages, it.Id)).ToArray();
        var before = Params.DecodeOrder(p);
        if (!order.SequenceEqual(before)) ApplyOrder(order);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveStage((sender as FrameworkElement)?.Tag as string, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveStage((sender as FrameworkElement)?.Tag as string, 1);

    void Led_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || !items.TryGetValue(id, out var it) || it.Power < 0) return;
        p[it.Power] = p[it.Power] >= 0.5f ? 0 : 1;
        Changed();
        SyncAll();
    }

    void RenderRail()
    {
        foreach (var it in items.Values)
            if (it.Power >= 0) it.IsOn = p[it.Power] >= 0.5f;
    }

    void Chain_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is not ChainItem it) return;
        foreach (var lv in new[] { TopList, StageList, BottomList })
            if (!ReferenceEquals(lv, sender) && lv.SelectedItem != null) lv.SelectedItem = null;
        Show(it.Id);
    }

    void Show(string id)
    {
        if (!panels.ContainsKey(id)) id = "comp";
        current = id;
        foreach (var (k, pn) in panels) pn.Root.Visibility = k == id ? Visibility.Visible : Visibility.Collapsed;
        var it = items[id];
        var list = topItems.Contains(it) ? TopList : bottomItems.Contains(it) ? BottomList : StageList;
        if (list.SelectedItem != it) list.SelectedItem = it;
        panels[id].View?.Invalidate();
        var st = Settings.Load();
        if (st.Module != id) { st.Module = id; st.Save(); }
        if (id == "mv7") core?.Mv7Get();
    }

    // гамбургер в заголовке: цепочка сворачивается до иконок, как NavigationView
    void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        SetRailCompact(RailSplit.IsPaneOpen);
        var st = Settings.Load();
        st.RailCompact = !RailSplit.IsPaneOpen;
        st.Save();
    }

    void SetRailCompact(bool compact)
    {
        RailSplit.IsPaneOpen = !compact;
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        TakeHead.Visibility = TakeAb.Visibility = TakeBar.Visibility = v;
        foreach (var it in items.Values) it.Compact = compact;
        RailContent.Width = compact ? 52 : 208;
        TakeRow.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        TakePanel.Margin = compact ? new Thickness(10, 8, 0, 0) : new Thickness(10, 8, 8, 0);
    }

    void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt && e.Key is VirtualKey.Up or VirtualKey.Down && Params.Stages.Contains(current))
        {
            MoveStage(current, e.Key == VirtualKey.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Delete or VirtualKey.Back && current == "eq" && eqGraph.Sel >= 0)
        {
            eqGraph.Remove(eqGraph.Sel);
            e.Handled = true;
        }
    }

    // ── шапка ──

    bool micListSync;

    void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (micListSync || MicBox.SelectedItem is not ComboBoxItem ci || ci.Tag is not string id) return;
        core.SelectDevice(id);
    }

    void OwnBtn_Click(object sender, RoutedEventArgs e)
    {
        bool own = !core.Last.Own;
        OwnBtn.IsChecked = core.Last.Own;
        core.SetOwn(own);
        Toast(own ? L.S("У этого микрофона теперь свои настройки", "This microphone now has its own settings") : L.S("Микрофон снова на общих настройках", "The microphone is back on shared settings"));
    }

    void PowerBtn_Click(object sender, RoutedEventArgs e)
    {
        p[P.BYPASS] = p[P.BYPASS] >= 0.5f ? 0 : 1;
        Changed();
        SyncPower();
    }

    void SyncPower()
    {
        PowerBtn.IsChecked = p[P.BYPASS] < 0.5f;
        if (core?.Last != null) RenderStatus(core.Last);
    }

    void MonBtn_Click(object sender, RoutedEventArgs e)
    {
        bool on = MonBtn.IsChecked == true;
        core.SetMonitor(on);
        if (on) Toast(L.S("Эхо в наушниках MV7? Ручку мониторинга MV7 — в сторону компьютера", "Echo in the MV7 headphones? Turn the MV7 monitor knob towards the computer"));
    }

    void PinBtn_Click(object sender, RoutedEventArgs e) => SetPin(PinBtn.IsChecked == true);

    void SetPin(bool on)
    {
        if (AppWindow.Presenter is OverlappedPresenter op) op.IsAlwaysOnTop = on;
        PinBtn.IsChecked = on;
        var st = Settings.Load();
        st.Topmost = on;
        st.Save();
    }

    void Reinstall_Click(object sender, RoutedEventArgs e) => core.Install(false);
    void Uninstall_Click(object sender, RoutedEventArgs e) => core.Install(true);
    void SetupBtn_Click(object sender, RoutedEventArgs e) => core.Install(false);
    void BannerBtn_Click(object sender, RoutedEventArgs e) => bannerAct?.Invoke();

    // ── пресеты ──

    float[][] builtinValues;

    string GuessPreset()
    {
        builtinValues ??= Params.Presets.Select(pr => pr.Build()).ToArray();
        for (int i = 0; i < builtinValues.Length; i++) if (Params.Same(p, builtinValues[i])) { modified = false; return Params.Presets[i].Name; }
        foreach (var pr in userPresets) if (pr.v != null && Params.Same(p, Params.Migrate(Pad(pr.v)))) { modified = false; return pr.name; }
        modified = true;   // ни с одним не совпало — «Пользовательский»
        return "Своя настройка";
    }

    static float[] Pad(float[] v)
    {
        var a = new float[P.COUNT];
        Array.Copy(v, a, Math.Min(v.Length, P.COUNT));
        return a;
    }

    void UpdatePresetBtn()
    {
        // настройки ни с одним пресетом не совпадают — это уже свой вариант
        var name = Params.Display(preset);
        PresetText.Text = modified ? Params.Custom : name;
        ToolTipService.SetToolTip(PresetBtn, modified
            ? L.S("Свои настройки — сохрани их как пресет, чтобы вернуться к ним", "Your own settings — save them as a preset to come back to them")
            : L.S($"Пресет «{name}»", $"Preset “{name}”"));
    }

    void ApplyPreset(string name, float[] values)
    {
        float bypass = p[P.BYPASS];
        p = Params.Migrate(Pad(values));
        p[P.BYPASS] = bypass;
        preset = name;
        SavePresetName(name);
        eqGraph.Sel = -1;
        modified = false;
        SyncAll();
        core.SetParams(p);
    }

    static void SavePresetName(string name)
    {
        var st = Settings.Load();
        st.Preset = name;
        st.Save();
    }

    void PresetFlyout_Opening(object sender, object e)
    {
        BuiltinList.Items.Clear();
        foreach (var pr in Params.Presets)
        {
            var sp = new StackPanel { Spacing = 1, Padding = new Thickness(0, 5, 0, 5) };
            sp.Children.Add(new TextBlock { Text = pr.Title, FontWeight = !modified && pr.Name == preset ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
            sp.Children.Add(new TextBlock { Text = pr.Subtitle, FontSize = 11, Foreground = Th.B("TextFillColorSecondaryBrush") });
            BuiltinList.Items.Add(new ListViewItem { Content = sp, Tag = pr, IsSelected = false });
        }
        MineList.Items.Clear();
        MineHead.Visibility = MineList.Visibility = userPresets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pr in userPresets)
        {
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = pr.name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = !modified && pr.name == preset ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
            var del = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(del, L.S("Удалить", "Delete"));
            var name = pr.name;
            del.Click += (_, _) =>
            {
                Presets.Delete(name);
                userPresets = Presets.Load();
                PresetFlyout_Opening(null, null);
            };
            Grid.SetColumn(del, 1);
            g.Children.Add(del);
            MineList.Items.Add(new ListViewItem { Content = g, Tag = pr, MinHeight = 36 });
        }
        SaveName.Text = modified || Params.Presets.Any(x => x.Name == preset) ? "" : preset;
    }

    // у ListView с готовыми ListViewItem в ClickedItem приходит содержимое строки — ищем строку по нему
    static object ClickedTag(ListView lv, object clicked) =>
        (clicked as ListViewItem)?.Tag ?? lv.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Content == clicked)?.Tag;

    void BuiltinList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ClickedTag(BuiltinList, e.ClickedItem) is not Params.Builtin pr) return;
        ApplyPreset(pr.Name, pr.Build());
        PresetFlyout.Hide();
    }

    void MineList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ClickedTag(MineList, e.ClickedItem) is not Presets.Item pr || pr.v == null) return;
        ApplyPreset(pr.name, pr.v);
        PresetFlyout.Hide();
    }

    void SaveName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { SavePreset_Click(null, null); e.Handled = true; }
    }

    void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = SaveName.Text.Trim();
        if (name.Length == 0) { SaveName.Focus(FocusState.Programmatic); return; }
        try { Presets.Save(name, (float[])p.Clone()); }
        catch (Exception ex) { Log.Write("preset save: " + ex.Message); Toast(L.S("Не сохранилось: ", "Not saved: ") + ex.Message); return; }
        userPresets = Presets.Load();
        preset = name;
        modified = false;
        SavePresetName(name);
        UpdatePresetBtn();
        PresetFlyout.Hide();
        Toast(L.S($"Сохранено: {name}", $"Saved: {name}"));
    }

    // ── дубль ──

    void TakeRec_Click(object sender, RoutedEventArgs e)
    {
        if (core.TakeRecording) core.TakeStop();
        else core.TakeRec();
    }

    void TakePlay_Click(object sender, RoutedEventArgs e)
    {
        if (lastTake.playing) core.TakeHalt();
        else core.PlayTake(takeDry);
    }

    void TakeAb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TakeAb == null || core == null) return;
        takeDry = TakeAb.SelectedIndex == 1;
        core.TakeDry(takeDry);
    }

    (bool rec, float len, bool playing, float pos) lastTake = (false, -1, false, 0);

    void RenderTake(BoosterCore.Frame f)
    {
        var t = f == null ? (false, 0f, false, 0f) : (f.TakeRec, f.TakeLen, f.TakePlaying, f.TakePos);
        if (t == lastTake) return;
        lastTake = t;
        var (rec, len, playing, pos) = t;
        int max = f?.TakeMax ?? 15;
        TakeRec.IsChecked = rec;
        ToolTipService.SetToolTip(TakeRec, rec ? L.S("Стоп", "Stop") : len > 0 ? L.S("Записать заново (до 15 с)", "Record again (up to 15 s)") : Tx.TakeRecTip);
        TakePlay.IsEnabled = !rec && len > 0;
        TakePlay.IsChecked = playing;
        TakePlayIcon.Glyph = playing ? "" : "";
        TakeBar.Value = rec ? len / max : playing ? pos : 0;
        TakeTime.Text = rec ? $"{len:0.0}/{max}s" : len > 0 ? $"{len:0.0}s" : "";
    }

    // ── кадры ──

    void OnFrame(BoosterCore.Frame f)
    {
        if (f.Fs != fs && f.Fs > 1000)
        {
            fs = f.Fs;
            foreach (var pn in panels.Values) if (pn.View != null) pn.View.Fs = fs;
        }
        bool alive = f.Alive;
        float N(float v) => alive && float.IsFinite(v) ? v : -120;
        meters.Update(N(f.InPk), N(f.InRms), N(f.OutPk), N(f.OutRms));
        meters.Invalidate();
        Spectrum.Update(alive ? f.Spec : null, alive ? f.Pre : null);
        live.Gate = alive ? -f.Gate : 0;
        live.Comp = alive ? f.Comp : 0;
        live.Ds = alive ? f.Ds : 0;
        live.Lim = alive ? f.Lim : 0;
        for (int i = 0; i < 4; i++)
        {
            live.MbIn[i] = alive ? f.MbIn[i] : -120;
            live.MbGr[i] = alive ? f.MbGr[i] : 0;
        }
        live.Mb = live.MbGr.Max();
        bool voiced = alive && f.TuneHz > 0;
        live.Tune = voiced ? MathF.Min(MathF.Abs(f.TuneShift) * 8, 12) : 0;
        tuneGraph.Push(voiced ? f.TuneHz : 0, f.TuneMidi, f.TuneTarget, f.TuneShift);

        ioGraph.Push(N(f.InRms), N(f.OutRms), 0);
        gateGraph.Push(N(f.InRms), N(f.OutRms), p[P.GATE_ON] >= 0.5f ? live.Gate : 0);
        compGraph.Push(N(f.InRms), N(f.OutRms), p[P.COMP_ON] >= 0.5f ? live.Comp : 0);
        limGraph.Push(N(f.InPk), N(f.OutPk), p[P.LIM_ON] >= 0.5f ? live.Lim : 0);
        if (current != null && panels.TryGetValue(current, out var pn2) && pn2.View is { Live: true } v) v.Invalidate();

        foreach (var it in items.Values)
        {
            if (it.Gr == null) continue;
            float g = p[it.Power] >= 0.5f ? it.Gr switch
            {
                "gate" => live.Gate, "comp" => live.Comp, "ds" => live.Ds, "lim" => live.Lim, "mb" => live.Mb, "tune" => live.Tune, _ => 0,
            } : 0;
            it.GrHold = g > it.GrHold ? g : MathF.Max(g, it.GrHold - 1);
            it.GrW = Math.Min(it.GrHold / 12, 1) * 34;
        }
        UpdateMbLive();

        RenderTake(f);
        if (f.MatchRec)
        {
            match.Len = f.MatchLen;
            match.Max = f.MatchMax;
            if (match.State != "rec") { match.State = "rec"; SyncMatch(); }
            if (current == "match") matchGraph.Invalidate();
        }
    }

    // ── состояние ──

    string micKey, refKey;

    void RenderStatus(BoosterCore.Status s)
    {
        var key = string.Join("|", s.Mics.Select(m => m.id + m.installed)) + "#" + s.Selected;
        if (key != micKey)
        {
            micKey = key;
            micListSync = true;
            MicBox.Items.Clear();
            foreach (var m in s.Mics)
            {
                var ci = new ComboBoxItem { Content = m.device + (m.installed ? "" : L.S(" (не подключён)", " (not connected)")), Tag = m.id };
                MicBox.Items.Add(ci);
                if (m.id == s.Selected) MicBox.SelectedItem = ci;
            }
            micListSync = false;
        }

        // эталон для MATCH — любой другой микрофон (список пересобираем, только когда он поменялся)
        var refs = s.Mics.Where(m => m.id != s.Selected).ToList();
        var rk = string.Join("|", refs.Select(m => m.id));
        if (rk != refKey)
        {
            refKey = rk;
            var keep = (matchRef.SelectedItem as ComboBoxItem)?.Tag as string;
            matchRef.Items.Clear();
            foreach (var m in refs)
            {
                var ci = new ComboBoxItem { Content = m.device, Tag = m.id };
                matchRef.Items.Add(ci);
                if (m.id == keep) matchRef.SelectedItem = ci;
            }
            if (matchRef.SelectedItem == null && matchRef.Items.Count > 0) matchRef.SelectedIndex = 0;
        }

        OwnBtn.Content = s.Own ? "OWN" : "SHARED";
        OwnBtn.IsChecked = s.Own;
        OwnBtn.IsEnabled = s.Installed;

        var mic = s.SelectedMic;
        bool installed = s.Installed && s.Dll;
        string cls = "", text;
        if (s.Busy) text = L.S("подключаю…", "connecting…");
        else if (mic == null) { cls = "bad"; text = L.S("микрофон не найден", "microphone not found"); }
        else if (!installed) { cls = "bad"; text = L.S("не подключено", "not connected"); }
        else if (s.FxOff) { cls = "bad"; text = L.S("Windows выключила эффекты", "Windows turned effects off"); }
        else if (!s.Alive) text = s.CaptureError != null ? L.S("нет звука: ", "no sound: ") + L.Msg(s.CaptureError) : L.S("ждёт звук…", "waiting for sound…");
        else if (p[P.BYPASS] >= 0.5f) { cls = "warn"; text = L.S("обход: обработка выключена", "bypass: processing is off"); }
        else
        {
            cls = "ok";
            text = s.Listeners.Count > 0 ? string.Join(", ", s.Listeners) : L.S("активно", "active");
        }
        StatusText.Text = text;
        StatusDot.Fill = cls switch
        {
            "ok" => Th.B("AccentFillColorDefaultBrush"),
            "warn" => Th.B("SystemFillColorCautionBrush"),
            "bad" => Th.B("SystemFillColorCriticalBrush"),
            _ => Th.B("TextFillColorTertiaryBrush"),
        };
        ToolTipService.SetToolTip(StatusPanel, s.Listeners.Count > 0 ? L.S("Слушают микрофон: ", "Listening to the microphone: ") + string.Join(", ", s.Listeners) : text);

        MonBtn.IsChecked = s.Monitor;

        bool forceSetup = Environment.GetEnvironmentVariable("VR_SETUP") == "1";   // для проверки вида экрана подключения
        Setup.Visibility = (mic != null && !installed && !s.Busy) || setupBusy || forceSetup ? Visibility.Visible : Visibility.Collapsed;
        SetupBtn.Content = mic != null ? L.S($"Подключить к {mic.device}", $"Connect to {mic.device}") : Tx.Connect;

        if (installed && s.FxOff) ShowBanner(L.S("Windows выключила эффекты у микрофона — обработка не работает.", "Windows turned off effects for the microphone — processing is not working."), L.S("Включить", "Turn on"), () => core.Install(false));
        else if (installed && s.Outdated) ShowBanner(L.S("Есть новая версия обработки.", "A new version of the processing is available."), L.S("Обновить", "Update"), () => core.Install(false));
        else Banner.IsOpen = false;
    }

    // VR_SHOT=1 — снимки для README: без плашек
    static readonly bool shotMode = Environment.GetEnvironmentVariable("VR_SHOT") == "1";

    void ShowBanner(string text, string btn, Action act)
    {
        if (shotMode) return;
        Banner.Message = text;
        BannerBtn.Content = btn;
        bannerAct = act;
        Banner.IsOpen = true;
    }

    bool setupBusy;

    void OnBusy(string what, string state, string msg)
    {
        if (state == "start")
        {
            setupBusy = what == "install" && Setup.Visibility == Visibility.Visible;
            SetupBtn.IsEnabled = false;
            SetupRing.Visibility = Visibility.Visible;
            SetupRing.IsActive = true;
            SetupMsg.Foreground = Th.B("TextFillColorSecondaryBrush");
            SetupMsg.Text = L.S("Подтверди запрос Windows…", "Confirm the Windows prompt…");
            Toast(L.S("Подтверди запрос Windows", "Confirm the Windows prompt"));
            return;
        }
        setupBusy = false;
        SetupBtn.IsEnabled = true;
        SetupRing.IsActive = false;
        SetupRing.Visibility = Visibility.Collapsed;
        if (state == "done") { SetupMsg.Text = ""; Toast(what == "install" ? L.S("Обработка подключена", "Processing connected") : L.S("Обработка снята", "Processing removed")); }
        else if (state == "cancel") SetupMsg.Text = L.S("Отменено", "Cancelled");
        else
        {
            SetupMsg.Foreground = Th.B("SystemFillColorCriticalBrush");
            var why = L.S("Не вышло: ", "Failed: ") + (L.Msg(msg) ?? L.S("ошибка", "error"));
            SetupMsg.Text = why;
            Toast(why, InfoBarSeverity.Error);
        }
    }

    void Toast(string text, InfoBarSeverity sev = InfoBarSeverity.Informational)
    {
        ToastBar.Severity = sev;
        ToastBar.Message = text;
        ToastBar.IsOpen = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    // тема Windows сменилась на ходу: перекрашиваем то, что собрано кодом, и графики
    void ApplyTheme(bool light)
    {
        if (light == Th.Light) return;
        Th.Set(light);
        Pal.Refresh(light);
        SyncAll();
        if (core?.Last != null) RenderStatus(core.Last);
        foreach (var pn in panels.Values) pn.View?.Invalidate();
        meters.Invalidate();
    }

    // язык меняется перезапуском окна: так все подписи гарантированно на одном языке
    void Lang_Click(object sender, RoutedEventArgs e)
    {
        var mode = (sender as FrameworkElement)?.Tag as string ?? "system";
        if (mode == L.Mode) return;
        var st = Settings.Load();
        st.Lang = mode;
        st.Save();
        SavePlacement();
        App.Restart();
    }

    // ── окно: где и какого размера, поверх ли остальных ──

    void RestorePlacement()
    {
        var s = Settings.Load();
        try
        {
            uint dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
            double k = dpi > 0 ? dpi / 96.0 : 1;
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                op.PreferredMinimumWidth = (int)(760 * k);
                op.PreferredMinimumHeight = (int)(480 * k);
            }
            int w = s.WinW > 0 ? s.WinW : (int)(Math.Max(s.Width, 980) * k);
            int h = s.WinH > 0 ? s.WinH : (int)(Math.Max(s.Height, 620) * k);
            // VR_SIZE=1180x720 — размер для снимков, настройки не трогает
            if (Environment.GetEnvironmentVariable("VR_SIZE") is { } size && size.Split('x') is [var sw, var sh]
                && int.TryParse(sw, out var vw) && int.TryParse(sh, out var vh)) { w = (int)(vw * k); h = (int)(vh * k); }
            int? x = s.WinX, y = s.WinY;
            if (x == null && s.Left != null) { x = (int)(s.Left.Value * k); y = (int)((s.Top ?? 0) * k); }
            var area = DisplayArea.GetFromPoint(new PointInt32(x ?? 0, y ?? 0), DisplayAreaFallback.Primary).WorkArea;
            w = Math.Min(w, area.Width);
            h = Math.Min(h, area.Height);
            bool inside = x != null && x >= area.X - 50 && x < area.X + area.Width - 100 && y >= area.Y - 10 && y < area.Y + area.Height - 60;
            if (!inside) { x = area.X + (area.Width - w) / 2; y = area.Y + (area.Height - h) / 2; }
            AppWindow.MoveAndResize(new RectInt32(x.Value, y.Value, w, h));
            if (s.Topmost && AppWindow.Presenter is OverlappedPresenter op2) op2.IsAlwaysOnTop = true;
            PinBtn.IsChecked = s.Topmost;
        }
        catch (Exception ex) { Log.Write("placement: " + ex.Message); }
    }

    internal void SavePlacement()
    {
        if (Environment.GetEnvironmentVariable("VR_SIZE") != null) return;   // снимки не меняют сохранённое окно
        if (AppWindow.Presenter is not OverlappedPresenter op || op.State != OverlappedPresenterState.Restored) return;
        var s = Settings.Load();
        s.WinX = AppWindow.Position.X;
        s.WinY = AppWindow.Position.Y;
        s.WinW = AppWindow.Size.Width;
        s.WinH = AppWindow.Size.Height;
        s.Topmost = op.IsAlwaysOnTop;
        s.Save();
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
}

// выбранный модуль: шапка, график, ручки
sealed class ModulePanel
{
    public string Id, Title;
    public Grid Root;
    public GraphView View;
}
