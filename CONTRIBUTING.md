# Contributing to VoxRack

Thanks for wanting to make VoxRack better! Bug reports, ideas, translations and code are all welcome.

## Reporting a problem

Open an [issue](https://github.com/vpainer/voxrack/issues/new/choose) and include:

* Windows version, microphone model and the app you record with (OBS, Discord, …);
* what you did, what you expected, what happened;
* logs if something fails: `C:\ProgramData\Golos\apo.log`, `C:\ProgramData\Golos\install.log`,
  `%AppData%\Golos\golos.log`.

## Building

See [Build from source](README.md#build-from-source). In short:

```sh
sh apo/build.sh test
dotnet publish app -c Release -r win-x64 --self-contained false -p:PublishDir=dist/
```

`apo/build.sh test` must stay green. Testing the effect on a real microphone changes the sound of every app that
uses it — remember to put your settings back.

## Making changes

* Open an issue first for anything bigger than a small fix, so we agree on the approach.
* Keep pull requests focused: one feature or fix per PR, with a short description of what and why.
* Match the surrounding code: naming, comment density, idioms. Many existing comments are in Russian —
  new comments in English are perfectly fine.
* Parameter indices live in **two places** — `apo/params.h` and `app/Params.cs`; the shared-memory layout in
  `apo/shared.h` and `core/Shm.cs`. Change them together. New parameters go into free slots.
* The APO runs inside `audiodg.exe` on the real-time audio thread: no allocations, locks, file or registry access
  in `APOProcess`. Everything must be NaN-safe.
* UI strings go through `L.S("русский", "English")` (`app/Lang.cs`); please provide both if you can — or just
  English and say so in the PR.
* UI uses stock WinUI controls; custom drawing (graphs, meters, knobs) uses the system accent color only.

## Translations

The app is in English and Russian. Want another language? Open an issue — the strings are collected in
`app/Lang.cs` and the `L.S(...)` calls.

## License

By contributing you agree that your contribution is licensed under the
[GPL-3.0-or-later](LICENSE), like the rest of the project.
