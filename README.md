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

**Live stats.** GPU temperature, load, clock, power and memory (Nvidia only), CPU load, memory and power source, updated every second. The GPU is only read while the Stats page is open or the in-game overlay is showing, because reading it keeps a laptop's RTX GPU awake. The in-game overlay shows Windows' own temperature reading near the CPU where the laptop provides one; an exact core temperature needs a third-party kernel driver, which isn't worth the risk for this.

**In-game overlay.** Switch on **Show over games** on the Stats page and, while a game from your list is in front, stacked bars down the top-right edge show (in the style of Rocket League's performance stats) FPS and average, the 1% and 0.1% lows, CPU usage and temperature, GPU load and clock, and GPU temperature and its highest. Values are green, yellow or red, and anything heat or the charger is slowing down turns orange, with an orange bar saying what's held back. It's Optimiser's own click-through window kept on top, so nothing is added to the game and anti-cheat has nothing to object to. A game in exclusive fullscreen can hide it; borderless fullscreen shows it. On some PCs any window on top of a game makes Windows blend the game's frames with the desktop, which can add a little input lag, so turn it off for competitive play if you notice that.

**Heat.** The **Heat** page shows each of Windows' own temperature sensors and, on MSI laptops, both fan speeds (read through MSI's firmware, the same readings MSI Center shows; nothing is written to the fans), with a warning when the temperature climbs and the fans don't speed up. **Watch for heat spikes** checks the sensors every 10 seconds without waking the GPU, and when one passes 90 °C it logs the time, the sensor and the five programs using the most CPU. **Quiet profile on the desktop** switches the CPU's turbo boost off in the power plan and Windows' power mode to Best power efficiency while no game runs, and lifts both the moment a game from your list starts.

**Benchmark and FPS history.** **Run benchmark** on the Stats page loads the GPU and every CPU thread for 60 seconds and scores it, and the list shows each run's score against the one before and which switches changed in between. It only runs when you press it. **Each game over time** charts every game's average across its sessions and flags a drop of 10% or more, naming the Nvidia driver or Windows update that came in between.

**Network check.** Ping, jitter and lost packets on the overlay and the Stats page, measured only. Epic and Activision don't publish server addresses, so it pings Cloudflare's nearest server (1.1.1.1) unless you type a game server's address in Settings.

**Customise the overlay.** The panel under Frame rate on the Stats page, with a live preview. Styles: Bars (Rocket League-style), Cards, Minimal (text only) and Strip (one row). Ctrl+Shift+O shows or hides the overlay from inside a game; pick other keys there. Layouts: Full, Compact (FPS, 1% low, GPU temperature and ping) and FPS only, and you can untick any stat (including the orange warnings). Also: second lines on or off, any corner of the screen, Standard, Colour-blind or White colours (heat warnings stay orange), size (80% by default, 50 to 150%), background, opacity and the gap from the top or bottom. **Reset** puts it back to how it looked at first.

**Nvidia driver check.** Each time Optimiser starts (it starts when you sign in) and each time the laptop wakes from sleep, it asks Nvidia's driver site for the newest Game Ready driver for your GPU, the same lookup as nvidia.com's driver page. If it's newer than yours, a notification says so, at most once a day, and **Up-to-date Nvidia driver** on the Optimisations page opens the NVIDIA App to install it. It only reads; nothing is downloaded or installed. If Nvidia's site can't be reached it goes by the driver's age instead.

**PC health, cleanup and weekly report.** The Dashboard scores temperatures, whether the Nvidia driver is the newest, startup apps, free disk space and Windows updates out of 100. **Disk cleanup** finds temp files over a week old, shader caches over a month old and leftover Nvidia and AMD driver installers, lists them with their sizes, and deletes only the ones you leave switched on when you press Delete. **This week** sums up the hottest temperature and heat spikes, each game's FPS against the week before, and what changed, and a notification brings it once a week.

**RGB.** One page for every RGB app on the PC. Pick a colour and Optimiser shows it on SteelSeries GG (MSI laptop keyboards and SteelSeries gear), Razer Synapse (Razer Chroma, plus anything linked with Chroma Connect, like a Wooting keyboard) and Logitech G HUB, using the ways each app offers other programs, and keeps it there while it runs. **Hand back to each app** puts their own lighting back. Apps without a way in (Wootility, Redragon, iCUE, Armoury Crate and others) get a button that opens them.

**Shot Trainer.** Controls the Rocket League shot trainer in `Documents\rl-shot-trainer`: start it, retry or skip a shot, set Necto's skill and reaction time, ball speed, time per shot, countdown and variation, pick which shots to practise, and switch on goal replays or infinite boost. The **Clips** tab plays the clips you save with D-pad up (or **Save clip**), which the trainer records through OBS's replay buffer into `rl-shot-trainer\clips`. Deleting a clip sends it to the Recycle Bin. Star a clip to favourite it (and filter to favourites), watch it in **Full screen** (or double-click the video), and **Edit** it: trim the time by hand or with **Cut to the shot** and **Just the goal** (from the touches and goal the trainer records), and change brightness, contrast, colour and warmth, or pick a look. Edits are kept in the clip's `.json` and applied as it plays (the colours through a pixel shader compiled at startup with Windows' `d3dcompiler_47.dll`), with Undo, Redo and Reset. **Save** writes the edit into the video with ffmpeg (from `%LOCALAPPDATA%\Programs\ffmpeg`, else PATH): the first save keeps the recording in `clips\unedited`, every save renders from it, and Reset then Save puts it back. **Rename** changes the clip's name and its files' names. Clicking a clip plays it. It writes the trainer's `settings.json`, which the trainer reads before every shot, so changes apply while you play. Start training runs the trainer as you, not as admin, through a `play.bat` it writes next to `start.bat` (the same, without the old settings window). **Launch Rocket League** opens the game on its own through Epic, without the trainer. **Leave training** stops the trainer and reopens Rocket League normally for online play (in game: Menu, then Y), through `leave.bat` in the trainer folder. **Play a match** starts a normal 1v1 against Necto at the Bot skill setting, through `match.bat` in the trainer folder. If Epic is updating Rocket League, the page says so instead of seeming to do nothing. Switch on **Start with Rocket League** and opening the game starts the trainer too (RLBot reopens the game once to attach); it needs Optimiser running in the background. **Open Shift with Rocket League** opens Shift (from `%APPDATA%\Shift`) when the game starts, or before it when started from this page, and opens it minimised, and closes it 20 seconds after the game closes (so RLBot reopening the game doesn't close it).

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

The quiet profile isn't a registry change, so it isn't listed on the Backups page. It saves the power plan's turbo boost setting and your power mode before changing them, and puts them back when a game starts, when you switch it off (on the Heat or Optimisations page, or with **Undo all changes**) and when Optimiser closes, or the next time it starts after a crash. The "Full refresh rate only while gaming" switch also puts the desktop rate back if the NVIDIA App raises it. Disk cleanup can't be undone, which is why it shows the list first and only deletes old files from those folders.

Nothing here turns off Defender or Windows Update, "cleans" the registry, overclocks the CPU, or controls the fans.

## Building

Needs the .NET 10 SDK.

```
dotnet run --project tests/Optimiser.Tests      # self-checks (Windows, as admin)
dotnet publish src/Optimiser -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

GitHub Actions runs the self-checks and builds `Optimiser.exe` on every pull request, takes a picture of every page, and publishes the exe as the `latest` release on every push to `main`.
The plan the app was built from is in [docs/plan.md](docs/plan.md).
