# VoxRack architecture

VoxRack has three parts that talk through one memory-mapped file:

```
mic → USB driver → audiodg.exe ─[ GolosAPO.dll ]─→ every app that records the mic
                                   ↑ parameters    ↓ meters, spectrum
                      C:\ProgramData\Golos\state.bin
                                   ↑↓
                              VoxRack.exe
```

*Golos* ("voice" in Russian) is the project's codename; it stays in file names, folders, the COM class and
`state.bin`, so existing installs keep working.

## `apo/` — the effect (C++17, MinGW-w64)

`GolosAPO.dll` is a system effect (APO) in SFX mode for one capture endpoint. Windows loads it into
`audiodg.exe` and runs every stream of that microphone through it (default and communications modes;
RAW streams bypass effects by design).

* `apo.cpp` — the COM class: aggregation-aware `IUnknown`, `IAudioProcessingObject*`, `IAudioSystemEffects2`,
  self-registration. Reads the endpoint GUID on `Initialize` to find its per-device settings slot.
* `dsp.h` — the `Engine`: input gain → the nine reorderable stages → output gain → look-ahead limiter (1.5 ms).
  Stages are `stHpf / stGate / stTune / stEq / stMb / stComp / stDs / stDelay / stReverb`, called in the order
  stored in `P_ORDER_A/B` (4 bits per slot). A broken order falls back to the default one.
  * Filters — RBJ biquads with coefficient ramping (no zipper noise when a knob moves).
  * Multiband — 4 bands, Linkwitz–Riley 4th-order crossovers with allpass phase compensation: with no compression
    the bands sum flat.
  * Compressor — soft knee, peak or RMS detector, parallel mix. De-esser — split-band, sidechain on the highs.
  * A NaN guard resets the engine instead of letting garbage reach the apps.
* `fx.h` — `FxTune` (YIN pitch on a ~12 kHz decimated signal refined at full rate, PSOLA with grains of two
  input periods: formants stay put; 36 ms latency while enabled), `FxDelay` (up to 2 s, filters in the feedback
  loop), `FxReverb` (Freeverb with pre-delay and a low cut on the input).
* `params.h` — parameter indices. **Keep in sync with `app/Params.cs`.**
* `shared.h` — layout of `state.bin`. **Keep in sync with `core/Shm.cs`.**
* `dsp_export.cpp` — `GolosDsp.dll`, the same engine for the window's TAKE player.
* `test/test_main.cpp` — `sh apo/build.sh test`: frequency response of every filter type, compressor curve, limiter
  ceiling, gate, de-esser, multiband flatness, autotune pitch accuracy, delay/reverb, chain order, a random-parameter
  storm without NaN, a benchmark and the COM contract (the DLL is loaded the same way audiodg does, with
  `state.bin` in a temp folder).
* `test/golosctl.cpp` — debugging on a live mic: `meters`, `get`, `set <index> <value>`, `listen 3 [--comms|--raw]`.

## `state.bin` — shared memory

No `Global\` objects or privileges: both sides map the same file in `C:\ProgramData\Golos` (writable by users and
LOCAL SERVICE).

* **Parameters** — 128 floats behind a seqlock. The window writes, the APO reads on the next ~10 ms block.
* **Per-device slots** — a table of 8 slots keyed by an FNV-1a hash of the endpoint GUID. A device with a slot
  (OWN) uses its own parameters, otherwise the shared ones (SHARED). `meterEp` tells which device writes meters.
* **Meters** — heartbeat, levels, gain reduction per stage and per band, autotune pitch, and two 16 k-sample rings
  (before / after processing) the window turns into spectra.
* `state.bin` survives a reboot but `GetTickCount64` does not, so a heartbeat "from the future" is treated as stale.

## `core/` — shared C# code

* `Shm.cs` — the reader/writer of `state.bin`.
* `Analyzer.cs` — FFT → 192 log bins with a +3 dB/oct tilt, peak/RMS.
* `AudioHost.cs` — keeps a capture stream open while the window is visible (so the APO runs and meters move even
  when no app records), MON monitor, TAKE player through `GolosDsp.dll`, list of apps using the mic.
* `Installer.cs` — lists microphones, checks whether the effect is attached, runs `tools/install-apo.ps1` elevated.
* `EqMatch.cs` — MATCH: Welch long-term spectra over speech frames, 1/3-octave smoothing, greedy + refined band fit
  with penalties, speech level, density (p90 − p30 → compressor) and noise floor (→ gate).
* `Mv7.cs` — the original Shure MV7 exposes a text console on its vendor HID interface (MI_03); VoxRack uses
  `meterMode`, `dimMode`, `lock`, `identify`. Protocol notes: [shurectl](https://github.com/Humblemonk/shurectl).

## `app/` — the window (WinUI 3, .NET 9)

* `BoosterCore.cs` — 30 frames per second: reads meters and rings, external parameter changes, status once a second.
* `MainWindow*.cs` — title bar, the chain (ListView with drag-reorder inside a SplitView), module panels, presets.
* `Graphs/` — Win2D views: frequency graphs with live spectrum, EQ, multiband (Pro-MB style overlay panel),
  time graphs, autotune, delay, reverb, MATCH, MV7, IN/OUT meters.
* `Controls/Knob.cs` — vector knob: drag, Shift for fine, wheel, double-click to reset, double-click the value to type.
* `Lang.cs` — English/Russian strings and theme-aware brushes for code-built UI.
* `About.cs` — links, About dialog, update check (GitHub Releases API, at most every 12 h).
* For screenshots and checks: `VR_MODULE=eq`, `VR_SETUP=1`, `VR_THEME=light|dark`, `VR_LANG=ru|en`, `VR_RAIL=open|compact`.

## Installing and removing the effect

`tools/install-apo.ps1` (run elevated; the window does it for you):

1. copies `GolosAPO.dll` to `C:\Program Files\Golos` and registers the COM class and the APO;
2. creates `C:\ProgramData\Golos` with the right ACL and `state.bin`;
3. writes the CLSID `{BCC05645-2F4E-4C14-9C6E-24D2FCDE41DE}` as the SFX for the default mode into
   `MMDevices\Audio\Capture\{device}\FxProperties` and enables audio enhancements; whatever was there before is
   saved to `HKLM\SOFTWARE\Golos\Backup` and restored on removal;
4. restarts the audio services.

Logs: `C:\ProgramData\Golos\apo.log`, `install.log`, `%AppData%\Golos\golos.log`.

## Tools

* `tools/matchtest` — MATCH on synthetic data: a "voice" through a known EQ, quieter and noisier → the fit finds the
  inverse correction.
* `tools/mv7probe` — talk to the MV7 console by hand.

## Ideas

* RNNoise-style neural noise suppression as one more stage.
* More MV7 hardware controls (input gain, monitor mix, onboard DSP).
* Code signing for releases.
