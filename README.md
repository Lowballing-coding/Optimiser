# Optimiser

A personal Windows app that tunes a gaming laptop for higher FPS, with safe, reversible tweaks.
Built for an MSI Raider GE66 (i7-12700H, RTX 3070 Ti), and it detects the hardware so it also behaves sensibly on other PCs.

**Download:** [Optimiser.exe (latest build)](https://github.com/Lowballing-coding/Optimiser/releases/download/latest/Optimiser.exe)

Windows SmartScreen will warn the first time because the app isn't code-signed: choose "More info", then "Run anyway".
It asks for administrator rights because every tweak changes a system setting.

## Getting started

1. Run `Optimiser.exe`. The Dashboard scans the PC and shows how optimised it is.
2. Press **Apply all recommended**, or switch things on one at a time on the **Optimisations** page.
3. Restart if a tweak says it needs one (hardware-accelerated GPU scheduling does).
4. On an MSI laptop, use the **Open MSI Center** buttons to switch on Discrete Graphics Mode and Extreme Performance. Optimiser can check the first but can't change either itself.

## What it does

**Biggest wins on this laptop.** Checks for MSI's Discrete Graphics Mode (the screen wired straight to the RTX GPU, often 5 to 15% more FPS) and points you at MSI Center's Extreme Performance mode.

**While a game is running.** Optimiser finds your Steam and Epic games (add anything else on the **Games** page) and switches on a gaming profile while one runs:
- Best performance power mode, with your usual mode put back when the game closes
- High CPU priority for the game
- No power throttling, so Windows doesn't push the game onto the efficiency cores
- Every game set to the RTX GPU in Windows' graphics settings
- A warning if you start a game on battery

**Windows settings.** Game Mode on, background game recording off, hardware-accelerated GPU scheduling on, and the telemetry service off.

**Live stats.** GPU temperature, load, clock, power and memory (Nvidia only), CPU load, memory and power source, updated every second. The GPU is only read while the Stats page is open, because reading it keeps a laptop's RTX GPU awake. There's no CPU temperature: reading it needs a third-party kernel driver, which isn't worth the risk for this.

## Running in the background

Switch on **Run in the background and start with Windows** (it's in the recommendations). Optimiser then:
- copies itself to `C:\Program Files\Optimiser` and starts from there in the system tray when you sign in, without a UAC prompt
- keeps running in the tray when you close the window. Click the tray icon to open it, or right-click and choose Exit.

To update, download the new `Optimiser.exe` and run it. It closes the old copy and replaces the installed one.
Switching background mode off removes the startup task and the installed copy.

## Undoing things

Every switch saves the original setting before changing it, and turning the switch off puts it back exactly.
The **Backups** page lists every change with its own Undo button, plus **Undo all changes**.
A Windows restore point is also made before the first change.

Nothing here turns off Defender or Windows Update, "cleans" the registry, overclocks, or controls the fans.

## Building

Needs the .NET 10 SDK.

```
dotnet run --project tests/Optimiser.Tests      # self-checks (Windows, as admin)
dotnet publish src/Optimiser -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

GitHub Actions runs the self-checks and builds `Optimiser.exe` on every pull request, takes a picture of every page, and publishes the exe as the `latest` release on every push to `main`.
The plan the app was built from is in [docs/plan.md](docs/plan.md).
