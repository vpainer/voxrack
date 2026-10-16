using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Golos;

// Язык окна: как в Windows (русский — если Windows по-русски, иначе английский) или выбранный вручную.
// Подписи ручек и имена модулей (HPF, Threshold…) — английские в обоих языках, как на железе.
public static class L
{
    public static string Mode { get; private set; } = "system";   // system | ru | en
    public static bool Ru { get; private set; }

    public static void Init(string mode)
    {
        Mode = mode is "ru" or "en" ? mode : "system";
        Ru = Mode == "ru" || Mode == "system" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    }

    public static string S(string ru, string en) => Ru ? ru : en;

    // сообщения общих файлов ядра (app/*.cs) написаны по-русски — переводим по началу строки
    static readonly (string ru, string en)[] msgs =
    {
        ("Мало речи в записи — запиши ещё раз и говори все 15 секунд.", "Too little speech in the recording — record again and keep talking for all 15 seconds."),
        ("У одного из микрофонов звук только до ", "One of the microphones only goes up to "),
        ("Микрофоны отличаются по громкости больше чем на 24 дБ — подтяни усиление цели вручную.", "The microphones differ in level by more than 24 dB — adjust the target gain by hand."),
        ("микрофон не подключён", "microphone is not connected"),
        ("вывод звука: ", "audio output: "),
        ("код ", "code "),
    };

    public static string Msg(string ru)
    {
        if (Ru || string.IsNullOrEmpty(ru)) return ru;
        foreach (var (r, e) in msgs)
            if (ru.StartsWith(r, StringComparison.Ordinal))
                return e + ru.Substring(r.Length).Replace(" кГц — выше подгонять нечего.", " kHz — nothing to match above that.");
        return ru;
    }
}

// строки разметки окна (x:Bind к статическим свойствам)
public static class Tx
{
    public static string OnOff => L.S("Вкл/выкл", "On/off");
    public static string Up => L.S("Выше по цепи", "Move up the chain");
    public static string Down => L.S("Ниже по цепи", "Move down the chain");
    public static string Mic => L.S("Микрофон", "Microphone");
    public static string MicTip => L.S("Микрофон, который настраивает окно", "The microphone this window adjusts");
    public static string OwnTip => L.S("OWN — у этого микрофона свои настройки; SHARED — общие для всех микрофонов с VoxRack",
        "OWN — this microphone has its own settings; SHARED — settings shared by all microphones with VoxRack");
    public static string PresetsTip => L.S("Пресеты", "Presets");
    public static string PresetsHead => L.S("ПРЕСЕТЫ", "PRESETS");
    public static string MineHead => L.S("МОИ", "MINE");
    public static string SaveAs => L.S("Сохранить как…", "Save as…");
    public static string Save => L.S("Сохранить", "Save");
    public static string MonTip => L.S("MON — слушать обработанный голос в наушниках (задержка ≈ 40 мс)", "MON — listen to the processed voice in headphones (≈ 40 ms delay)");
    public static string PinTip => L.S("Поверх всех окон", "Always on top");
    public static string PowerTip => L.S("Обработка вкл/выкл — для сравнения, действует на все программы", "Processing on/off — for A/B, affects every app");
    public static string More => L.S("Ещё", "More");
    public static string Reinstall => L.S("Переподключить обработку", "Reconnect processing");
    public static string Uninstall => L.S("Снять обработку с микрофона", "Remove processing from the microphone");
    public static string Language => L.S("Язык", "Language");
    public static string SupportTip => L.S("Поддержать разработку", "Support development");
    public static string AboutText => L.S("О программе", "About VoxRack");
    public static string CheckUpdates => L.S("Проверить обновления", "Check for updates");
    public static string Download => L.S("Скачать", "Download");
    public static string LangSystem => L.S("Как в Windows", "Same as Windows");
    public static string TakeRecTip => L.S("Записать фразу (до 15 с)", "Record a phrase (up to 15 s)");
    public static string TakePlayTip => L.S("Слушать дубль по кругу через текущие настройки", "Loop the take through the current settings");
    public static string WetDryTip => L.S("WET — с обработкой, DRY — как было", "WET — processed, DRY — as recorded");
    public static string MetersTip => L.S("Клик — сбросить индикацию перегруза", "Click to reset the clip indicators");
    public static string SetupTitle => L.S("Подключение к микрофону", "Connect to the microphone");
    public static string SetupText => L.S("Обработка встраивается в сам микрофон Windows: OBS, Discord и Telegram получают уже обработанный звук, без виртуальных кабелей.",
        "Processing is built into the Windows microphone itself: OBS, Discord and Telegram get already processed sound, no virtual cables.");
    public static string Setup1 => L.S("•  Windows один раз спросит права администратора.", "•  Windows will ask for administrator rights once.");
    public static string Setup2 => L.S("•  Звук пропадёт на пару секунд: перезапустятся аудиослужбы.", "•  Sound drops for a couple of seconds while audio services restart.");
    public static string Setup3 => L.S("•  Снимается в любой момент, всё возвращается как было.", "•  Remove it any time — everything goes back as it was.");
    public static string Connect => L.S("Подключить", "Connect");
}

// Тема: ресурсы WinUI для кода, который строит элементы сам. Значения берём из словаря текущей темы окна,
// а при смене темы Windows всё, что зарегистрировано через On(), перекрашивается заново.
static class Th
{
    public static bool Light { get; private set; }
    static readonly Dictionary<string, object> cache = new();
    static readonly List<Action> appliers = new();

    public static Brush B(string key) => Get(key) as Brush;

    public static object Get(string key)
    {
        if (cache.TryGetValue(key, out var v)) return v;
        var res = Application.Current.Resources;
        v = Find(res, key, Light ? "Light" : "Default") ?? (Light ? null : Find(res, key, "Dark"));
        if (v == null) res.TryGetValue(key, out v);
        cache[key] = v;
        return v;
    }

    static object Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary td)
        {
            if (td.TryGetValue(key, out var v)) return v;
            foreach (var m in td.MergedDictionaries)
                if (Find(m, key, theme) is { } mv) return mv;
        }
        foreach (var m in d.MergedDictionaries)
            if (Find(m, key, theme) is { } mv) return mv;
        return null;
    }

    // выполнить сейчас и повторять при каждой смене темы
    public static void On(Action apply)
    {
        apply();
        appliers.Add(apply);
    }

    public static void Set(bool light)
    {
        Light = light;
        cache.Clear();
        foreach (var a in appliers) a();
    }
}
