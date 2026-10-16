using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace Golos;

// строка цепочки модулей
public sealed class ChainItem : INotifyPropertyChanged
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Tip { get; set; }
    public int Power { get; init; } = -1;     // индекс параметра «вкл»; -1 — модуль без выключателя
    public string Gr { get; init; }           // какое подавление показывать полоской
    public string Glyph { get; init; } = "";

    // свёрнутая цепочка: видна только иконка (выключенный модуль — бледная иконка)
    bool compact;
    public bool Compact
    {
        get => compact;
        set
        {
            if (compact == value) return;
            compact = value;
            Raise(nameof(LedVisibility));
            Raise(nameof(GrVisibility));
            Raise(nameof(NameVisibility));
        }
    }

    public Visibility LedVisibility => Power >= 0 && !compact ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GrVisibility => Gr != null && !compact ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NameVisibility => compact ? Visibility.Collapsed : Visibility.Visible;

    bool isOn = true;
    public bool IsOn
    {
        get => isOn;
        set { if (isOn == value) return; isOn = value; Raise(nameof(IsOn)); Raise(nameof(NameOpacity)); }
    }

    public double NameOpacity => Power < 0 || isOn ? 1 : 0.55;

    double grW;
    public double GrW
    {
        get => grW;
        set { if (Math.Abs(grW - value) < 0.25) return; grW = value; Raise(nameof(GrW)); }
    }

    public float GrHold;   // плавный спад полоски

    public event PropertyChangedEventHandler PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
