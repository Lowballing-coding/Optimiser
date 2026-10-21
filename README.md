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

**RGB.** One page for every RGB app on the PC. Pick a colour and Optimiser shows it on SteelSeries GG (MSI laptop keyboards and SteelSeries gear), Razer Synapse (Razer Chroma, plus anything linked with Chroma Connect, like a Wooting keyboard) and Logitech G HUB, using the ways each app offers other programs, and keeps it there while it runs. **Hand back to each app** puts their own lighting back. Apps without a way in (Wootility, Redragon, iCUE, Armoury Crate and others) get a button that opens them.

**Shot Trainer.** Controls the Rocket League shot trainer in `Documents\rl-shot-trainer`: start it, retry or skip a shot, set Necto's skill and reaction time, ball speed, time per shot, countdown and variation, pick which shots to practise, and switch on goal replays or infinite boost. The **Clips** tab plays the clips you save with D-pad up (or **Save clip**), which the trainer records through OBS's replay buffer into `rl-shot-trainer\clips`. Deleting a clip sends it to the Recycle Bin. Star a clip to favourite it (and filter to favourites), watch it in **Full screen** (or double-click the video), and **Edit** it: trim the time by hand or with **Cut to the shot** and **Just the goal** (from the touches and goal the trainer records), and change brightness, contrast, colour and warmth, or pick a look. Edits are saved in the clip's `.json` and applied as it plays, so the video file is never changed; the colours use a pixel shader compiled at startup with Windows' `d3dcompiler_47.dll`. It writes the trainer's `settings.json`, which the trainer reads before every shot, so changes apply while you play. Start training runs the trainer as you, not as admin, through a `play.bat` it writes next to `start.bat` (the same, without the old settings window). **Launch Rocket League** opens the game on its own through Epic, without the trainer. **Leave training** stops the trainer and reopens Rocket League normally for online play (in game: Menu, then Y), through `leave.bat` in the trainer folder. **Play a match** starts a normal 1v1 against Necto at the Bot skill setting, through `match.bat` in the trainer folder. If Epic is updating Rocket League, the page says so instead of seeming to do nothing. Switch on **Start with Rocket League** and opening the game starts the trainer too (RLBot reopens the game once to attach); it needs Optimiser running in the background. **Open Shift with Rocket League** opens Shift (from `%APPDATA%\Shift`) when the game starts, or before it when started from this page, and opens it minimised, and closes it 20 seconds after the game closes (so RLBot reopening the game doesn't close it).

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
