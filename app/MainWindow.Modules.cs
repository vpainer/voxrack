using CommunityToolkit.WinUI.Controls;
using Golos.Controls;
using Golos.Graphs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Golos;

// Модули цепочки: у каждого шапка (выключатель, имя, подсказка), график и ряд ручек.
public sealed partial class MainWindow
{
    EqGraph eqGraph;
    DynGraph mbGraph;
    TimeGraph ioGraph, gateGraph, compGraph, limGraph;
    TuneGraph tuneGraph;
    MatchGraph matchGraph;
    Mv7Graph mv7Graph;
    ComboBox matchRef;

    static readonly Knob.Spec Thr = new("Threshold", -60, 0, 0.5f, -24, Fmt: F.Dbfs);
    static readonly Knob.Spec Ratio = new("Ratio", 1, 20, Def: 3, Log: true, Fmt: F.Ratio);
    static readonly Knob.Spec Att = new("Attack", 0.1f, 200, Def: 8, Log: true, Fmt: F.Ms);
    static readonly Knob.Spec Rel = new("Release", 5, 3000, Def: 120, Log: true, Fmt: F.Ms);

    // ── строительные блоки ──

    Knob K(int idx, Knob.Spec spec)
    {
        var k = new Knob(spec);
        k.OnInput = v => { p[idx] = v; Changed(); };
        k.Set(p[idx]);
        syncers.Add(() => k.Set(p[idx]));
        return k;
    }

    Segmented Seg(int idx, (float v, string label)[] opts, string tip = null)
    {
        var s = new Segmented();
        foreach (var o in opts) s.Items.Add(new SegmentedItem { Content = o.label, FontSize = 11 });
        s.SelectionChanged += (_, _) =>
        {
            // при первом показе Segmented ещё раз сообщает о выборе — без изменения ничего не пишем
            if (syncing || s.SelectedIndex < 0 || MathF.Abs(p[idx] - opts[s.SelectedIndex].v) < 1e-4f) return;
            p[idx] = opts[s.SelectedIndex].v;
            Changed();
        };
        syncers.Add(() =>
        {
            int i = Array.FindIndex(opts, o => MathF.Abs(o.v - p[idx]) < 1e-3f);
            s.SelectedIndex = i < 0 ? 0 : i;
        });
        if (tip != null) ToolTipService.SetToolTip(s, tip);
        return s;
    }

    // подпись над контролом; сам контрол — на уровне центра ручек
    static FrameworkElement Group(string label, FrameworkElement el)
    {
        var g = new Grid { RowSpacing = 4 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock { Text = label, Style = Res<Style>("CtrlLabelStyle"), HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(t);
        el.VerticalAlignment = VerticalAlignment.Center;
        el.Margin = new Thickness(0, 0, 0, 20);
        Grid.SetRow(el, 1);
        g.Children.Add(el);
        return g;
    }

    static FrameworkElement Sep()
    {
        var r = new Rectangle { Width = 1, Height = 46, Margin = new Thickness(2, 10, 2, 18), VerticalAlignment = VerticalAlignment.Top };
        Th.On(() => r.Fill = Th.B("DividerStrokeColorDefaultBrush"));
        return r;
    }

    static ToggleButton Led(string tip)
    {
        var b = new ToggleButton { Style = Res<Style>("LedToggleStyle") };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    static ToggleButton RecButton(string tip)
    {
        var b = new ToggleButton { Width = 34, Height = 30, Padding = new Thickness(0), Content = new Ellipse { Width = 10, Height = 10, Fill = Res<Brush>("RecBrush") } };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    readonly List<ToggleButton> preBoxes = new();

    // спектр до обработки — кнопка-переключатель, как кнопки в заголовке окна
    ToggleButton PreToggle()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new FontIcon { Glyph = "", FontSize = 13 });
        sp.Children.Add(new TextBlock { Text = L.S("Спектр входа", "Input spectrum"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var c = new ToggleButton { Content = sp, Height = 30, Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = Spectrum.ShowPre };
        ToolTipService.SetToolTip(c, L.S("Показать и спектр до обработки — серой линией", "Also show the spectrum before processing as a grey line"));
        c.Click += (_, _) =>
        {
            Spectrum.ShowPre = c.IsChecked == true;
            foreach (var x in preBoxes) x.IsChecked = Spectrum.ShowPre;
            foreach (var pn in panels.Values) pn.View?.Invalidate();
        };
        preBoxes.Add(c);
        return c;
    }

    ModulePanel Panel(string id, string name, string title, int power, GraphView view, FrameworkElement[] tools, params FrameworkElement[] ctrls)
    {
        var root = new Grid { RowSpacing = 8, Visibility = Visibility.Collapsed };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // шапка: выключатель и имя в строку, под ними — описание
        var head = new Grid { ColumnSpacing = 10, RowSpacing = 0 };
        for (int i = 0; i < 4; i++) head.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var knobs = new List<Knob>();
        foreach (var c in ctrls) FindKnobs(c, knobs);
        if (power >= 0)
        {
            var sw = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -2, 0) };
            ToolTipService.SetToolTip(sw, Tx.OnOff);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sw, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sw, "Power_" + id);
            sw.Toggled += (_, _) =>
            {
                if (syncing || sw.IsOn == p[power] >= 0.5f) return;
                p[power] = sw.IsOn ? 1 : 0;
                Changed();
                SyncAll();
            };
            syncers.Add(() =>
            {
                bool on = p[power] >= 0.5f;
                sw.IsOn = on;
                foreach (var k in knobs) k.Active = on;
            });
            head.Children.Add(sw);
        }
        var nameEl = new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 80, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameEl, power >= 0 ? 1 : 0);
        if (power < 0) Grid.SetColumnSpan(nameEl, 2);
        head.Children.Add(nameEl);
        var titleEl = new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        ToolTipService.SetToolTip(titleEl, title);
        Th.On(() => titleEl.Foreground = Th.B("TextFillColorSecondaryBrush"));
        Grid.SetRow(titleEl, 1);
        Grid.SetColumnSpan(titleEl, 4);
        head.Children.Add(titleEl);
        if (tools.Length > 0)
        {
            var tp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRowSpan(tp, 2);
            foreach (var t in tools) tp.Children.Add(t);
            Grid.SetColumn(tp, 3);
            head.Children.Add(tp);
        }
        root.Children.Add(head);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 6, 6, 2),
            Child = view.Host,
        };
        Th.On(() =>
        {
            card.Background = Th.B("CardBackgroundFillColorDefaultBrush");
            card.BorderBrush = Th.B("CardStrokeColorDefaultBrush");
        });
        Grid.SetRow(card, 1);
        root.Children.Add(card);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(4, 2, 4, 0) };
        foreach (var c in ctrls) row.Children.Add(c);
        var sv = new ScrollViewer
        {
            Content = row,
            HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(sv, 2);
        root.Children.Add(sv);

        Stage.Children.Add(root);
        view.Fs = fs;
        var pn = new ModulePanel { Id = id, Title = title, Root = root, View = view };
        panels[id] = pn;
        return pn;
    }

    static void FindKnobs(object el, List<Knob> acc)
    {
        switch (el)
        {
            case Knob k: acc.Add(k); break;
            case Microsoft.UI.Xaml.Controls.Panel pnl: foreach (var c in pnl.Children) FindKnobs(c, acc); break;
            case Border b: FindKnobs(b.Child, acc); break;
        }
    }

    // ── модули ──

    void BuildModules()
    {
        BuildMv7();
        BuildMatch();

        ioGraph = new TimeGraph("io", () => p);
        Panel("io", "I/O", L.S("Вход и выход", "Input and output"), -1, ioGraph, Array.Empty<FrameworkElement>(),
            K(P.IN_GAIN, new("Input", -24, 24, 0.5f, 0, Bipolar: true, Fmt: F.Db)),
            K(P.OUT_GAIN, new("Output", -24, 24, 0.5f, 0, Bipolar: true, Fmt: F.Db)));

        var hpf = new HpfGraph(() => p) { OnEdit = Changed };
        Panel("hpf", "HPF", L.S("Срез низа: гул, стук по столу, дыхание", "Low cut: rumble, desk knocks, breath"), P.HPF_ON, hpf, Array.Empty<FrameworkElement>(),
            K(P.HPF_FREQ, new("Freq", 20, 400, Def: 80, Log: true, Fmt: F.Hz)),
            Group("Slope", Seg(P.HPF_SLOPE, new[] { (12f, "12 dB"), (24f, "24 dB") }, L.S("дБ на октаву", "dB per octave"))));
        hpf.OnEdit = () => { Changed(); SyncAll(); };

        gateGraph = new TimeGraph("gate", () => p) { OnEdit = () => { Changed(); SyncAll(); } };
        Panel("gate", "GATE", L.S("Гейт: тишина между фразами", "Gate: silence between phrases"), P.GATE_ON, gateGraph, Array.Empty<FrameworkElement>(),
            K(P.GATE_THRESH, Thr with { Min = -90, Def = -50 }),
            K(P.GATE_RANGE, new("Range", 0, 80, 1, 20, Fmt: v => $"{v:0} dB")),
            K(P.GATE_ATTACK, Att with { Max = 100, Def = 2 }),
            K(P.GATE_HOLD, new("Hold", 0, 1000, 5, 80, Fmt: F.Ms)),
            K(P.GATE_RELEASE, Rel with { Max = 2000, Def = 150 }));

        BuildTune();
        BuildEq();
        BuildMb();

        compGraph = new TimeGraph("comp", () => p) { OnEdit = () => { Changed(); SyncAll(); } };
        Panel("comp", "COMP", L.S("Компрессор · порог тянется на графике", "Compressor · drag the threshold on the graph"), P.COMP_ON, compGraph, Array.Empty<FrameworkElement>(),
            K(P.COMP_THRESH, Thr),
            K(P.COMP_RATIO, Ratio),
            K(P.COMP_KNEE, new("Knee", 0, 24, 0.5f, 6, Fmt: v => $"{v:0.0} dB")),
            K(P.COMP_ATTACK, Att),
            K(P.COMP_RELEASE, Rel),
            K(P.COMP_MAKEUP, new("Makeup", -12, 36, 0.5f, 0, Bipolar: true, Fmt: F.Db)),
            K(P.COMP_MIX, new("Mix", 0, 1, 0.01f, 1, Fmt: F.Pct, Tip: L.S("Меньше 100% — параллельная компрессия", "Below 100% — parallel compression"))),
            Group("Detect", Seg(P.COMP_RMS, new[] { (0f, "PEAK"), (1f, "RMS") })));

        var ds = new DsGraph(() => p, () => live);
        ds.OnEdit = () => { Changed(); SyncAll(); };
        Panel("ds", "DE-ESS", L.S("Де-эссер: мягче «с», «ш», «ч»", "De-esser: softer s, sh, ch"), P.DS_ON, ds, Array.Empty<FrameworkElement>(),
            K(P.DS_FREQ, new("Freq", 2000, 16000, Def: 6500, Log: true, Fmt: F.Hz)),
            K(P.DS_THRESH, Thr with { Def = -30 }),
            K(P.DS_RANGE, new("Range", 0, 24, 0.5f, 8, Fmt: v => $"{v:0.0} dB")));

        Panel("dly", "DELAY", L.S("Делей: эхо для стрима (слышно во всех программах)", "Delay: echo for streams (heard in every app)"), P.DLY_ON, new DelayGraph(() => p), Array.Empty<FrameworkElement>(),
            K(P.DLY_TIME, new("Time", 20, 1500, Def: 320, Log: true, Fmt: F.Ms)),
            K(P.DLY_FB, new("Feedback", 0, 0.95f, 0.01f, 0.3f, Fmt: F.Pct)),
            K(P.DLY_TONE, new("Tone", 800, 16000, Def: 5000, Log: true, Fmt: F.Hz, Tip: L.S("Срез верха у повторов: ниже — темнее, как плёнка", "High cut on the repeats: lower is darker, like tape"))),
            K(P.DLY_MIX, new("Mix", 0, 1, 0.01f, 0.2f, Fmt: F.Pct)));

        Panel("rev", "REVERB", L.S("Реверб: зал для стрима (слышно во всех программах)", "Reverb: a room for streams (heard in every app)"), P.REV_ON, new ReverbGraph(() => p), Array.Empty<FrameworkElement>(),
            K(P.REV_SIZE, new("Size", 0, 1, 0.01f, 0.55f, Fmt: F.Pct)),
            K(P.REV_DAMP, new("Damp", 0, 1, 0.01f, 0.45f, Fmt: F.Pct, Tip: L.S("Насколько быстро гаснет верх", "How fast the highs die away"))),
            K(P.REV_PREDELAY, new("Pre-delay", 0, 200, 1, 20, Fmt: F.Ms)),
            K(P.REV_LOWCUT, new("Low cut", 20, 1000, Def: 250, Log: true, Fmt: F.Hz, Tip: L.S("Низ голоса в зал не пускаем — без гула", "Keeps the low end out of the room — no boom"))),
            K(P.REV_MIX, new("Mix", 0, 1, 0.01f, 0.18f, Fmt: F.Pct)));

        limGraph = new TimeGraph("lim", () => p) { OnEdit = () => { Changed(); SyncAll(); } };
        Panel("lim", "LIMIT", L.S("Лимитер: выше потолка не пропустит", "Limiter: nothing gets past the ceiling"), P.LIM_ON, limGraph, Array.Empty<FrameworkElement>(),
            K(P.LIM_CEIL, new("Ceiling", -24, 0, 0.1f, -1, Fmt: F.Dbfs)),
            K(P.LIM_RELEASE, Rel with { Max = 1000, Def = 60 }));
    }

    // ── автотюн ──

    void BuildTune()
    {
        tuneGraph = new TuneGraph(() => p);
        var keySel = new ComboBox { Width = 76, MinWidth = 0 };
        foreach (var n in Params.NoteNames) keySel.Items.Add(n);
        keySel.SelectionChanged += (_, _) =>
        {
            if (syncing || keySel.SelectedIndex < 0 || (((int)MathF.Round(p[P.TUNE_KEY]) % 12) + 12) % 12 == keySel.SelectedIndex) return;
            p[P.TUNE_KEY] = keySel.SelectedIndex;
            Changed();
        };
        syncers.Add(() => keySel.SelectedIndex = (((int)MathF.Round(p[P.TUNE_KEY]) % 12) + 12) % 12);
        Panel("tune", "TUNE", L.S("Автотюн: подтягивает голос к нотам тональности", "Autotune: pulls the voice to the notes of the key"), P.TUNE_ON, tuneGraph, Array.Empty<FrameworkElement>(),
            Group("Key", keySel),
            Group("Scale", Seg(P.TUNE_SCALE, new[] { (0f, "CHROM"), (1f, "MAJ"), (2f, "MIN") }, L.S("Все ноты / мажор / минор", "All notes / major / minor"))),
            Sep(),
            K(P.TUNE_SPEED, new("Speed", 0, 400, 1, 20, Fmt: v => v < 1 ? "ROBOT" : $"{MathF.Round(v):0} ms", Tip: L.S("0 — жёсткий «роботизированный» эффект, 50+ — незаметная подстройка", "0 — hard robotic effect, 50+ — transparent correction"))),
            K(P.TUNE_AMOUNT, new("Amount", 0, 1, 0.01f, 1, Fmt: F.Pct, Tip: L.S("Насколько подтягивать к ноте", "How strongly to pull to the note"))),
            K(P.TUNE_SHIFT, new("Shift", -12, 12, 1, 0, Bipolar: true, Fmt: v => $"{(v > 0 ? "+" : v < 0 ? "−" : "")}{MathF.Abs(MathF.Round(v)):0} st", Tip: L.S("Транспонирование, полутоны", "Transpose, semitones"))));
    }

    // ── эквалайзер: кнопки полос + ручки выбранной полосы ──

    static readonly float[] DefF = { 80, 160, 315, 630, 1250, 2500, 4000, 6300, 10000, 15000 };
    readonly ToggleButton[] eqBtns = new ToggleButton[P.EQ_BANDS];
    Knob eqF, eqG, eqQ;
    ComboBox eqType;
    Button eqOff;
    bool eqSync;

    void BuildEq()
    {
        eqGraph = new EqGraph(() => p);
        eqGraph.OnEdit = () => { Changed(); SyncEqEditor(); };
        eqGraph.OnSelect = _ => SyncEqEditor();
        eqGraph.OnHint = t => Toast(t);

        var grid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        for (int b = 0; b < P.EQ_BANDS; b++)
        {
            int band = b;
            var btn = new ToggleButton { Content = (b + 1).ToString(), Width = 26, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 11 };
            btn.Click += (_, _) =>
            {
                if (!Params.Band(p, band).On)
                {
                    p[P.EQ_ON] = 1;
                    eqGraph.Edit(band, on: true, type: T.BELL, freq: DefF[band], gain: 0, q: 1);
                }
                eqGraph.Select(band);
                SyncAll();
            };
            Grid.SetColumn(btn, b % 5);
            Grid.SetRow(btn, b / 5);
            grid.Children.Add(btn);
            eqBtns[b] = btn;
        }
        grid.Margin = new Thickness(0, 0, 0, 14);

        eqType = new ComboBox { Width = 112, MinWidth = 0 };
        foreach (var l in T.Labels) eqType.Items.Add(l);
        eqType.SelectionChanged += (_, _) =>
        {
            if (eqSync || syncing || eqGraph.Sel < 0 || eqType.SelectedIndex < 0) return;
            int v = eqType.SelectedIndex;
            if (v == Params.Band(p, eqGraph.Sel).Type) return;
            eqGraph.Edit(eqGraph.Sel, type: v, q: v == T.BELL ? 1 : v == T.NOTCH ? 4 : 0.71f);
        };
        eqOff = new Button { Content = "OFF", FontSize = 11, Height = 30, Padding = new Thickness(8, 0, 8, 0) };
        ToolTipService.SetToolTip(eqOff, L.S("Убрать полосу (Delete)", "Remove the band (Delete)"));
        eqOff.Click += (_, _) => { if (eqGraph.Sel >= 0) eqGraph.Remove(eqGraph.Sel); };
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        typeRow.Children.Add(eqType);
        typeRow.Children.Add(eqOff);

        eqF = new Knob(new("Freq", 20, 20000, Def: 1000, Log: true, Fmt: F.Hz)) { OnInput = v => { if (eqGraph.Sel >= 0) eqGraph.Edit(eqGraph.Sel, freq: v); } };
        eqG = new Knob(new("Gain", -18, 18, 0.1f, 0, Bipolar: true, Fmt: F.Db)) { OnInput = v => { if (eqGraph.Sel >= 0) eqGraph.Edit(eqGraph.Sel, gain: v); } };
        eqQ = new Knob(new("Q", 0.1f, 18, Def: 1, Log: true, Fmt: F.Q)) { OnInput = v => { if (eqGraph.Sel >= 0) eqGraph.Edit(eqGraph.Sel, q: v); } };

        var bandGroup = new StackPanel { Spacing = 4 };
        bandGroup.Children.Add(new TextBlock { Text = "Band", Style = Res<Style>("CtrlLabelStyle"), HorizontalAlignment = HorizontalAlignment.Left });
        bandGroup.Children.Add(grid);

        Panel("eq", "EQ", L.S("Эквалайзер · двойной клик — полоса, колесо — ширина", "Equalizer · double-click adds a band, wheel sets width"), P.EQ_ON, eqGraph, new FrameworkElement[] { PreToggle() },
            bandGroup, Sep(), Group("Type", typeRow), eqF, eqG, eqQ);
        syncers.Add(() =>
        {
            if (eqGraph.Sel >= 0 && !Params.Band(p, eqGraph.Sel).On) eqGraph.Sel = -1;
            SyncEqEditor();
        });
    }

    void SyncEqEditor()
    {
        int sel = eqGraph.Sel;
        for (int i = 0; i < P.EQ_BANDS; i++)
        {
            eqBtns[i].IsChecked = i == sel;
            eqBtns[i].Foreground = Th.B(Params.Band(p, i).On ? "TextFillColorPrimaryBrush" : "TextFillColorDisabledBrush");
        }
        bool has = sel >= 0 && Params.Band(p, sel).On;
        eqF.Opacity = eqQ.Opacity = has ? 1 : 0.35;
        eqType.IsEnabled = eqOff.IsEnabled = has;
        eqG.Opacity = has ? 1 : 0.35;
        if (!has) return;
        var x = Params.Band(p, sel);
        eqSync = true;
        eqType.SelectedIndex = Math.Clamp(x.Type, 0, T.Labels.Length - 1);
        eqSync = false;
        eqF.Set(x.Freq);
        eqG.Set(x.Gain);
        eqQ.Set(x.Q);
        eqG.Opacity = T.HasGain(x.Type) ? 1 : 0.35;
    }

    // ── многополосный: как FabFilter Pro-MB — полосы правятся прямо на графике, ручки выбранной полосы
    //    в плавающей панели, которая висит под ней ──

    int mbSel;
    readonly List<(Knob k, int off)> mbKnobs = new();
    ToggleButton mbLed;
    Rectangle mbLvlFill, mbLvlThr;
    TextBlock mbLvlVal, mbBandName, mbBandRange, mbBandGr;
    float mbLvl = -120;

    void BuildMb()
    {
        mbGraph = new DynGraph(() => p, () => live);
        mbGraph.OnEdit = () => { Changed(); SyncMbEditor(); };
        mbGraph.OnSelect = k => { mbSel = k; SyncMbEditor(); };

        mbLed = Led(L.S("Полоса вкл/выкл (двойной клик по точке)", "Band on/off (double-click the dot)"));
        mbLed.Click += (_, _) =>
        {
            p[P.MB_BASE + mbSel * P.MB_STRIDE] = mbLed.IsChecked == true ? 1 : 0;
            Changed();
            SyncMbEditor();
        };
        mbBandName = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 60, VerticalAlignment = VerticalAlignment.Center };
        mbBandRange = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        mbBandGr = new TextBlock { FontFamily = Res<FontFamily>("MonoFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var head = new Grid { ColumnSpacing = 4, Margin = new Thickness(-4, 0, 0, 2) };
        foreach (var wdt in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = wdt });
        head.Children.Add(mbLed);
        Grid.SetColumn(mbBandName, 1);
        head.Children.Add(mbBandName);
        Grid.SetColumn(mbBandRange, 2);
        head.Children.Add(mbBandRange);
        Grid.SetColumn(mbBandGr, 3);
        head.Children.Add(mbBandGr);

        // уровень в полосе и порог
        var lvl = new Grid { Width = 8, Height = 44, BorderThickness = new Thickness(1) };
        mbLvlFill = new Rectangle { VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };
        mbLvlThr = new Rectangle { VerticalAlignment = VerticalAlignment.Bottom, Height = 2, Margin = new Thickness(-4, 0, -4, 0) };
        lvl.Children.Add(mbLvlFill);
        lvl.Children.Add(mbLvlThr);
        ToolTipService.SetToolTip(lvl, L.S("Уровень в полосе и порог", "Band level and threshold"));
        mbLvlVal = new TextBlock { Text = "—", FontFamily = Res<FontFamily>("MonoFont"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
        var lvlBox = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        lvlBox.Children.Add(new TextBlock { Text = "Lvl", Style = Res<Style>("CtrlLabelStyle") });
        lvlBox.Children.Add(lvl);
        lvlBox.Children.Add(mbLvlVal);

        Knob Mk(int off, Knob.Spec spec)
        {
            var k = new Knob(spec);
            k.OnInput = v => { p[P.MB_BASE + mbSel * P.MB_STRIDE + off] = v; Changed(); mbGraph.Invalidate(); };
            mbKnobs.Add((k, off));
            return k;
        }
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        knobs.Children.Add(lvlBox);
        knobs.Children.Add(Mk(1, Thr with { Def = -30 }));
        knobs.Children.Add(Mk(2, Ratio with { Def = 2 }));
        knobs.Children.Add(Mk(3, Att with { Def = 10 }));
        knobs.Children.Add(Mk(4, Rel with { Def = 150 }));
        knobs.Children.Add(Mk(5, new("Gain", -18, 18, 0.1f, 0, Bipolar: true, Fmt: F.Db)));

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(head);
        body.Children.Add(knobs);
        var card = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 10, 2),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
        };
        Th.On(() =>
        {
            card.Background = Th.B("AcrylicInAppFillColorDefaultBrush");
            card.BorderBrush = Th.B("SurfaceStrokeColorFlyoutBrush");
            mbBandRange.Foreground = Th.B("TextFillColorSecondaryBrush");
            mbBandGr.Foreground = Th.B("AccentTextFillColorPrimaryBrush");
            lvl.Background = Th.B("ControlFillColorInputActiveBrush");
            lvl.BorderBrush = Th.B("ControlStrokeColorDefaultBrush");
            mbLvlFill.Fill = Th.B("TextFillColorSecondaryBrush");
            mbLvlThr.Fill = Th.B("AccentFillColorDefaultBrush");
            mbLvlVal.Foreground = Th.B("TextFillColorSecondaryBrush");
        });
        mbGraph.SetOverlay(card);

        Panel("mb", "MULTIBAND", L.S("Многополосный · тяни точку полосы: вверх-вниз — усиление, вбок — сдвиг; колесо — порог, Ctrl+колесо — степень",
                "Multiband · drag a band dot: up/down — gain, sideways — move; wheel — threshold, Ctrl+wheel — ratio"),
            P.MB_ON, mbGraph, new FrameworkElement[] { PreToggle() });
        syncers.Add(SyncMbEditor);
    }

    void SyncMbEditor()
    {
        var b = Params.Mb(p, mbSel);
        mbLed.IsChecked = b.On;
        mbBandName.Text = Params.MbNames[mbSel];
        float lo = mbSel == 0 ? 20 : p[P.MB_X1 + mbSel - 1], hi = mbSel == 3 ? 20000 : p[P.MB_X1 + mbSel];
        mbBandRange.Text = $"{F.HzShort(lo)}–{F.HzShort(hi)} Hz";
        ToolTipService.SetToolTip(mbBandName, Params.MbTitles[mbSel]);
        foreach (var (k, off) in mbKnobs)
        {
            k.Set(p[P.MB_BASE + mbSel * P.MB_STRIDE + off]);
            k.Opacity = b.On ? 1 : 0.4;
            k.Active = b.On && p[P.MB_ON] >= 0.5f;
        }
        mbGraph.Sel = mbSel;
        mbGraph.Invalidate();
    }

    void UpdateMbLive()
    {
        float v = live.MbIn[mbSel], thr = Params.Mb(p, mbSel).T;
        mbLvl = v > mbLvl ? v : MathF.Max(v, mbLvl - 0.8f);
        double T(float db) => Math.Clamp((db + 60) / 60, 0, 1) * 42;
        mbLvlFill.Height = T(mbLvl);
        bool on = p[P.MB_ON] >= 0.5f && Params.Mb(p, mbSel).On;
        bool over = mbLvl > thr && on;
        mbLvlFill.Fill = Th.B(over ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush");
        mbLvlThr.Margin = new Thickness(-4, 0, -4, T(thr) - 1);
        mbLvlVal.Text = mbLvl > -60 ? $"{mbLvl:0}" : "—";
        float g = on ? live.MbGr[mbSel] : 0;
        mbBandGr.Text = g > 0.05f ? $"GR −{g:0.0}" : "GR 0.0";
        mbBandGr.Opacity = g > 0.05f ? 1 : 0.5;
    }

    // ── сам микрофон: подсветка панели, «мигнуть», блокировка касаний ──

    readonly Dictionary<string, ToggleButton> ledBtns = new();
    ToggleButton lockLed;

    void BuildMv7()
    {
        mv7Graph = new Mv7Graph(() => mv7, () => mv7Checked, () => meters.Ch[0].Pk);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var m in LedModes.All)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 5; i++)
            {
                var col = m.Meter && i < 3 ? Graphs.Pal.Green : m.Meter && i == 3 ? Graphs.Pal.Yellow : Windows.UI.Color.FromArgb(255, 58, 62, 68);
                bar.Children.Add(new Rectangle { Width = 7, Height = 5, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(col), Opacity = m.Dim ? 0.4 : 1 });
            }
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(bar);
            content.Children.Add(new TextBlock { Text = m.Label, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var b = new ToggleButton { Content = content, Width = 68, Height = 46, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(b, m.Title);
            var mode = m;
            b.Click += (_, _) =>
            {
                mv7 ??= new Mv7.State();
                mv7.meter = mode.Meter;
                mv7.dim = mode.Dim;
                SyncMv7();
                core.Mv7Led(mode.Meter, mode.Dim);
            };
            ledBtns[m.Id] = b;
            modes.Children.Add(b);
        }
        var ident = new Button { Content = "IDENTIFY", FontSize = 11, Height = 30 };
        ToolTipService.SetToolTip(ident, L.S("Микрофон помигает 2 секунды", "The microphone blinks for 2 seconds"));
        ident.Click += (_, _) => core.Mv7Identify();
        lockLed = Led(L.S("Блокировка касаний: на стриме не замьютишь микрофон случайно", "Touch lock: no accidental mute on stream"));
        lockLed.Click += (_, _) => core.Mv7Lock(lockLed.IsChecked == true);

        var ledGroup = new StackPanel { Spacing = 4 };
        ledGroup.Children.Add(new TextBlock { Text = "LED", Style = Res<Style>("CtrlLabelStyle"), HorizontalAlignment = HorizontalAlignment.Left });
        modes.Margin = new Thickness(0, 0, 0, 12);
        ledGroup.Children.Add(modes);
        Panel("mv7", "MV7", L.S("Сам микрофон: подсветка сенсорной панели и касания", "The microphone itself: touch panel lights and touch"), -1, mv7Graph, Array.Empty<FrameworkElement>(),
            ledGroup, Sep(), Group("Find", ident), Group("Touch lock", lockLed));
        SyncMv7();
    }

    void SyncMv7()
    {
        var s = mv7;
        var mode = LedModes.Of(s);
        bool connected = s?.connected == true;
        foreach (var (id, b) in ledBtns)
        {
            b.IsChecked = connected && id == mode.Id;
            b.IsEnabled = connected;
        }
        lockLed.IsChecked = s?.locked == true;
        lockLed.IsEnabled = connected;
        mv7Graph.Invalidate();
    }

    // ── подгонка цели (микрофон в шапке) под эталон ──

    ToggleButton matchRec;
    readonly Dictionary<string, (ToggleButton btn, TextBlock val)> matchOpts = new();
    Button matchApply;

    void BuildMatch()
    {
        matchGraph = new MatchGraph(() => match, () => meters.Ch[0].Pk);
        matchRef = new ComboBox { Width = 150, MinWidth = 0, PlaceholderText = L.S("Эталон", "Reference") };
        ToolTipService.SetToolTip(matchRef, L.S("Эталон — как должно звучать", "Reference — how it should sound"));
        var secs = new Segmented();
        foreach (var s in new[] { "10s", "15s", "20s" }) secs.Items.Add(new SegmentedItem { Content = s, FontSize = 11, Padding = new Thickness(8, 0, 8, 0) });
        secs.SelectedIndex = 1;
        secs.SelectionChanged += (_, _) => match.Seconds = secs.SelectedIndex switch { 0 => 10, 2 => 20, _ => 15 };
        matchRec = RecButton(L.S("Записать оба микрофона", "Record both microphones"));
        matchRec.Click += (_, _) =>
        {
            matchRec.IsChecked = match.State == "rec";
            if (match.State == "rec") core.MatchStop();
            else if ((matchRef.SelectedItem as ComboBoxItem)?.Tag is string id) core.MatchRec(id, match.Seconds);
            else Toast(L.S("Нужен второй микрофон — эталон", "A second microphone is needed as the reference"));
        };
        var recRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        recRow.Children.Add(secs);
        recRow.Children.Add(matchRec);

        var opts = new Grid { RowSpacing = 4, ColumnSpacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        opts.ColumnDefinitions.Add(new ColumnDefinition());
        opts.ColumnDefinitions.Add(new ColumnDefinition());
        opts.RowDefinitions.Add(new RowDefinition());
        opts.RowDefinitions.Add(new RowDefinition());
        int n = 0;
        foreach (var (k, label, tip) in new[] { ("level", "LEVEL", L.S("Громкость", "Level")), ("eq", "EQ", L.S("Эквалайзер", "Equalizer")), ("dyn", "DYN", L.S("Плотность — компрессор", "Density — compressor")), ("noise", "NOISE", L.S("Шум между фразами — гейт", "Noise between phrases — gate")) })
        {
            var val = new TextBlock { Text = "—", FontFamily = Res<FontFamily>("MonoFont"), FontSize = 10, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(val);
            var b = new ToggleButton { Content = sp, Height = 26, MinWidth = 74, Padding = new Thickness(6, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(b, tip);
            string key = k;
            b.Click += (_, _) =>
            {
                switch (key)
                {
                    case "level": match.Level = !match.Level; break;
                    case "eq": match.Eq = !match.Eq; break;
                    case "dyn": match.Dyn = !match.Dyn; break;
                    default: match.Noise = !match.Noise; break;
                }
                SyncMatch();
            };
            Grid.SetColumn(b, n % 2);
            Grid.SetRow(b, n / 2);
            opts.Children.Add(b);
            matchOpts[k] = (b, val);
            n++;
        }
        var optsGroup = new StackPanel { Spacing = 4 };
        optsGroup.Children.Add(new TextBlock { Text = "Apply", Style = Res<Style>("CtrlLabelStyle"), HorizontalAlignment = HorizontalAlignment.Left });
        optsGroup.Children.Add(opts);
        var amount = new Knob(new("Amount", 0, 1, 0.05f, 1, Fmt: F.Pct)) { OnInput = v => match.Amount = v };
        matchApply = new Button { Content = "APPLY", Style = Res<Style>("AccentButtonStyle"), Height = 30, FontSize = 12 };
        ToolTipService.SetToolTip(matchApply, L.S("Применить к целевому микрофону", "Apply to the target microphone"));
        matchApply.Click += (_, _) => core.MatchApply(match.Level, match.Eq, match.Dyn, match.Noise, match.Amount);

        Panel("match", "MATCH", L.S("Подгонка под эталон: цель зазвучит как другой микрофон", "Match: make the target sound like another microphone"), -1, matchGraph, Array.Empty<FrameworkElement>(),
            Group("Reference", matchRef), Group("Record", recRow), optsGroup, amount, Group(" ", matchApply));
        SyncMatch();
    }

    void OnMatchState(string state, string msg)
    {
        match.State = state == "applied" ? "done" : state;
        match.Error = state == "error" ? msg : null;
        if (state == "applied") Toast(L.S("Подгонка применена — только к этому микрофону (OWN)", "Match applied — to this microphone only (OWN)"));
        if (state == "error") Toast(L.Msg(msg), InfoBarSeverity.Warning);
        SyncMatch();
    }

    void SyncMatch()
    {
        var r = match.Result;
        bool recOn = match.State == "rec";
        matchRec.IsChecked = recOn;
        ToolTipService.SetToolTip(matchRec, recOn ? L.S("Стоп", "Stop") : L.S("Записать оба микрофона", "Record both microphones"));
        var vals = new Dictionary<string, string>
        {
            ["level"] = r != null ? $"{(r.gainDb > 0 ? "+" : "")}{r.gainDb:0.0}" : "—",
            ["eq"] = r != null ? $"{r.bands.Count}" : "—",
            ["dyn"] = r != null ? (r.comp != null ? $"{r.comp.ratio:0.0}:1" : "ok") : "—",
            ["noise"] = r != null ? (r.gate != null ? $"{r.gate.thresh:0}" : "ok") : "—",
        };
        foreach (var (k, (btn, val)) in matchOpts)
        {
            bool has = r != null && (k == "dyn" ? r.comp != null : k == "noise" ? r.gate != null : true);
            bool on = k switch { "level" => match.Level, "eq" => match.Eq, "dyn" => match.Dyn, _ => match.Noise };
            btn.IsChecked = has && on;
            btn.IsEnabled = has;
            val.Text = vals[k];
        }
        // без речи анализ подгоняет шум комнаты — такое применять нельзя
        matchApply.IsEnabled = r != null && match.State != "rec" && match.State != "analyze" && r.speechSec >= 3;
        if (r != null && r.notes?.Count > 0 && match.State == "done" && !match.NotesShown) { Toast(L.Msg(r.notes[0])); match.NotesShown = true; }
        matchGraph.Invalidate();
    }
}
