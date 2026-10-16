using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Golos;

// Ссылки проекта, «О программе» и проверка новой версии на GitHub.
static class Links
{
    public const string Repo = "https://github.com/vpainer/voxrack";
    public const string Releases = Repo + "/releases/latest";
    public const string Issues = Repo + "/issues/new/choose";
    public const string License = Repo + "/blob/main/LICENSE";
    // способы поддержать проект — раздел README: ссылки там можно менять, не выпуская новую версию программы
    public const string Support = Repo + "#support";
    const string Api = "https://api.github.com/repos/vpainer/voxrack/releases/latest";

    public static Version Current => typeof(Links).Assembly.GetName().Version ?? new Version(1, 0);
    public static string CurrentText => Current.ToString(3);

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open link: " + ex.Message); }
    }

    // последний релиз на GitHub: версия и страница загрузки; null — нет сети, релизов или ответ непонятный
    public static async Task<(Version ver, string url)?> LatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VoxRack/" + CurrentText);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : Releases;
            if (Version.TryParse(tag, out var v)) return (v, url ?? Releases);
        }
        catch (Exception ex) { Log.Write("update check: " + ex.Message); }
        return null;
    }

    static bool Newer(Version a, Version b) =>
        new Version(a.Major, a.Minor, Math.Max(a.Build, 0)) > new Version(b.Major, b.Minor, Math.Max(b.Build, 0));

    public static bool IsNewer(Version v) => Newer(v, Current);
}

public sealed partial class MainWindow
{
    string updateUrl;

    // проверка новой версии: при запуске — не чаще раза в 12 часов, из меню — сразу
    async void CheckUpdates(bool manual)
    {
        if (!manual)
        {
            var st = Settings.Load();
            if (st.LastUpdateCheck is DateTime t && DateTime.UtcNow - t < TimeSpan.FromHours(12)) return;
        }
        var r = await Links.LatestAsync();
        if (!manual || r != null)
        {
            var st = Settings.Load();
            st.LastUpdateCheck = DateTime.UtcNow;
            st.Save();
        }
        if (r is { } x && Links.IsNewer(x.ver) && Environment.GetEnvironmentVariable("VR_SHOT") != "1")
        {
            updateUrl = x.url;
            UpdateBar.Title = L.S($"Доступна VoxRack {x.ver.ToString(3)}", $"VoxRack {x.ver.ToString(3)} is available");
            UpdateBar.Message = L.S($"У тебя {Links.CurrentText}.", $"You have {Links.CurrentText}.");
            UpdateBar.IsOpen = true;
        }
        else if (manual)
            Toast(r == null
                ? L.S("Не получилось проверить обновления — нет связи с GitHub", "Couldn’t check for updates — GitHub is unreachable")
                : L.S($"У тебя последняя версия — {Links.CurrentText}", $"You have the latest version — {Links.CurrentText}"));
    }

    void UpdateBtn_Click(object sender, RoutedEventArgs e) => Links.Open(updateUrl ?? Links.Releases);
    void Support_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Support);
    void CheckUpdates_Click(object sender, RoutedEventArgs e) => CheckUpdates(true);

    async void About_Click(object sender, RoutedEventArgs e)
    {
        var body = new StackPanel { Spacing = 10, MaxWidth = 420 };
        body.Children.Add(new TextBlock
        {
            Text = L.S($"Версия {Links.CurrentText}", $"Version {Links.CurrentText}"),
            Style = Res<Style>("BodyStrongTextBlockStyle"),
        });
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = L.S("Студийная цепочка обработки голоса, встроенная прямо в микрофон Windows: эквалайзер, многополосный и обычный компрессор, гейт, де-эссер, автотюн, делей, реверб и лимитер. OBS, Discord и любые программы получают уже обработанный звук — без виртуальных кабелей.",
                "A studio voice chain built right into your Windows microphone: EQ, multiband and regular compression, gate, de-esser, autotune, delay, reverb and limiter. OBS, Discord and every other app get the processed sound — no virtual cables."),
        });
        var links = new StackPanel { Spacing = 0, Margin = new Thickness(-12, 0, 0, 0) };
        void Link(string text, string url)
        {
            var b = new HyperlinkButton { Content = text };
            b.Click += (_, _) => Links.Open(url);
            links.Children.Add(b);
        }
        Link(L.S("Исходный код на GitHub", "Source code on GitHub"), Links.Repo);
        Link(L.S("Сообщить о проблеме или предложить идею", "Report a problem or suggest an idea"), Links.Issues);
        Link(L.S("Лицензия GPL-3.0", "License: GPL-3.0"), Links.License);
        body.Children.Add(links);
        var fine = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Th.B("TextFillColorSecondaryBrush"),
            Text = L.S("© 2026 Victor Painer. Shure и MV7 — товарные знаки Shure Incorporated; VoxRack — независимый проект и с Shure не связан.",
                "© 2026 Victor Painer. Shure and MV7 are trademarks of Shure Incorporated; VoxRack is an independent project and is not affiliated with Shure."),
        };
        body.Children.Add(fine);

        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "VoxRack",
            Content = body,
            PrimaryButtonText = L.S("♥ Поддержать разработку", "♥ Support development"),
            SecondaryButtonText = L.S("Проверить обновления", "Check for updates"),
            CloseButtonText = L.S("Закрыть", "Close"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.ActualTheme,
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary) Links.Open(Links.Support);
        else if (res == ContentDialogResult.Secondary) CheckUpdates(true);
    }
}
