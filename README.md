<p align="center">
  <img src="docs/images/icon.png" width="76" alt="VoxRack">
</p>

<h1 align="center">VoxRack</h1>

<p align="center">
  <b>A studio voice chain built right into your Windows microphone.</b><br>
  EQ · multiband compression · compressor · gate · de-esser · autotune · delay · reverb · limiter —<br>
  for OBS, Discord, Zoom, Telegram and every other app. No virtual cables.
</p>

<p align="center">
  <a href="https://github.com/vpainer/voxrack/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/vpainer/voxrack?label=download&color=2ea44f"></a>
  <a href="https://github.com/vpainer/voxrack/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/vpainer/voxrack/total?color=blue"></a>
  <img alt="Windows 10/11 x64" src="https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D4">
  <a href="LICENSE"><img alt="GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
</p>

<p align="center">
  <a href="https://github.com/vpainer/voxrack/releases/latest"><b>Download</b></a> ·
  <a href="#features">Features</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#build-from-source">Build</a> ·
  <a href="#support">Support</a> ·
  <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="docs/images/hero-dark.png" alt="VoxRack — multiband compressor, dark theme">
</p>

## Why

Microphone apps usually give you a timid compressor and a 3-band EQ. Routing your voice through a DAW or
"virtual audio cables" works, but adds latency, extra devices and things that break on every update.

VoxRack installs a real-time audio effect (a Windows **APO**) on the microphone itself. Windows runs it inside
its own audio engine, so **every app** — OBS, Discord, Zoom, Teams, Telegram, the browser — simply opens your
usual microphone and gets already processed sound. The driver stays the stock one, nothing is re-routed.

The window is only for tuning: close it and the processing keeps working, survives reboots and costs about
1.5 % of one CPU core with everything enabled.

## Features

| | |
|---|---|
| **A chain you can rearrange** | Low cut, gate, autotune, 10-band EQ, 4-band multiband, compressor, de-esser, delay, reverb — drag them into any order. Input/output first, limiter last. |
| **Multiband like the pros** | Bands live right on the live spectrum: drag a band's dot up/down for gain and sideways to move it, wheel for threshold, Ctrl+wheel for ratio. The selected band's knobs float under it. |
| **See what happens** | Live spectrum (before/after), gain-reduction history for gate, compressor and limiter, a transfer curve with the current operating point, pitch track for autotune, echo and decay previews. |
| **Autotune** | YIN pitch detection + PSOLA shifting that keeps formants: key, scale, speed (0 = robot), amount, transpose. |
| **Per-microphone settings** | Several mics with VoxRack? Each can have its own settings (OWN) or share one set (SHARED). |
| **MATCH** | Record the same phrase into two microphones — VoxRack fits EQ, level, density and noise gate so the target sounds like the reference. |
| **TAKE** | Record a phrase and loop it through the chain while you tweak, A/B against the dry take. |
| **MON** | Hear the processed voice in your headphones (≈ 40 ms). |
| **Presets** | Podcast, Radio, Stream, Calls — or save your own. |
| **Shure MV7 extras** | LED modes, touch lock and "identify" for the original Shure MV7 over its USB HID channel. |
| **Native Windows 11 app** | WinUI 3 with Mica, light/dark theme and accent color from Windows, English and Russian, collapsible sidebar. |

<p align="center">
  <img src="docs/images/eq-light.png" width="49%" alt="EQ, light theme">
  <img src="docs/images/compact-dark.png" width="49%" alt="Collapsed sidebar, dark theme">
</p>

## Download & install

1. Download **`VoxRack-x.y.z-win-x64.zip`** from the [latest release](https://github.com/vpainer/voxrack/releases/latest).
2. Unzip it anywhere you like (for example `%LOCALAPPDATA%\Programs\VoxRack`) and run **`VoxRack.exe`**.
   Everything is inside the folder — no .NET or Windows App SDK to install.
3. Pick your microphone in the title bar and press **Connect**. Windows asks for administrator rights once and the
   audio services restart (sound drops for a couple of seconds).

That's it: your apps keep using the same microphone, now processed.

> **Windows SmartScreen** may warn about an unknown publisher — the app is not code-signed yet.
> Click *More info → Run anyway*, or build it yourself from source.

**Requirements:** Windows 10 (2004+) or Windows 11, x64. Mica needs Windows 11.

**Uninstall:** `⋯ → Remove processing from the microphone`, then delete the folder. Settings live in
`%AppData%\Golos`, the effect in `C:\Program Files\Golos` (Golos is the project's codename).

**Good to know**

* Apps that open the microphone in *raw* or *exclusive* mode (some DAWs) bypass Windows effects by design.
  Communication apps (Discord, Zoom, Telegram, Teams) and OBS are processed.
* The headphone jack on a USB mic is a hardware monitor: it plays the unprocessed signal. Use **MON** to hear yourself processed.
* If your microphone has its own DSP (for example in Shure MOTIV), turn it off to avoid processing twice.
* VoxRack checks GitHub for a new release at most twice a day; nothing else leaves your computer.

## How it works

```
mic → USB driver → audiodg.exe ─[ GolosAPO.dll ]─→ OBS / Discord / Zoom / …
                                   ↑ parameters    ↓ meters, spectrum
                      C:\ProgramData\Golos\state.bin  (shared memory)
                                   ↑↓
                              VoxRack.exe  (WinUI 3)
```

* **`apo/`** — the effect itself, C++17 with no dependencies, built with MinGW-w64: RBJ biquads with coefficient
  ramping, Linkwitz–Riley crossovers with phase compensation, soft-knee compressor, look-ahead limiter,
  YIN + PSOLA autotune, Freeverb. Windows loads it into `audiodg.exe` as an SFX APO for the chosen microphone.
* **`state.bin`** — a memory-mapped file both sides open: the window writes parameters (seqlock), the APO
  picks them up on the next 10 ms block and writes back levels, gain reduction and before/after sample rings.
* **`core/`** — shared memory, WASAPI (monitor and the take player), installer, EQ matching, MV7 HID.
* **`app/`** — the WinUI 3 window: Win2D graphs, vector knobs, everything else is stock WinUI.

More details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build from source

You need Windows, the [.NET 9 SDK](https://dotnet.microsoft.com/download) and MinGW-w64 (UCRT64) from
[MSYS2](https://www.msys2.org/): `pacman -S mingw-w64-ucrt-x86_64-gcc`. Visual Studio is not required.

```sh
sh apo/build.sh test          # GolosAPO.dll, GolosDsp.dll, golosctl.exe → build/ + DSP and COM tests
dotnet publish app -c Release -r win-x64 --self-contained false -p:PublishDir=dist/
```

If `g++` is not on your `PATH`, point the script at it: `MINGW_BIN=/c/msys64/ucrt64/bin sh apo/build.sh`.
Every push is built by GitHub Actions; a `v*` tag produces a release with a self-contained zip.

## Contributing

Bug reports, ideas and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
Please open an issue before large changes so we can agree on the approach.

## Support

VoxRack is free and open source, made by an independent developer in their spare time. If it makes your voice sound better:

* ⭐ **Star the repository** — it really helps others find it.
* 🗣 **Tell a friend** or show it on your stream.
* 🐞 **Report bugs and share ideas** in [Issues](https://github.com/vpainer/voxrack/issues).
* 💛 **Donate** — links are coming soon.

## License

[GPL-3.0-or-later](LICENSE) © 2026 Victor Painer. You can use, study, share and modify VoxRack;
modified versions you distribute must stay open under the same license.

*Shure and MV7 are trademarks of Shure Incorporated. VoxRack is an independent project and is not affiliated
with or endorsed by Shure.*
