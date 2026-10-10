# Optimiser — plan

A personal Windows app that makes Peter's MSI Raider GE66 (i7-12700H, RTX 3070 Ti, 32 GB) run games faster, with hardware detection so it still behaves sensibly on other PCs.

## Decisions so far

| Question | Answer |
|---|---|
| Main goal | Gaming FPS |
| Laptop | MSI Raider GE66 |
| Tweak level | Safe and reversible only |
| Live stats | Yes, as a panel in the app |
| Background | Yes, runs in the tray and switches automatically when a game starts |
| Who uses it | Just Peter |
| Code | Claude owns it |

## What it looks like

Dark Hone-style window with a left icon sidebar and four pages:

- **Dashboard:** "X% optimised" ring, the top recommendations with toggles, "Apply all", and shortcut tiles (Backups, Games, Stats).
- **Optimisations:** every tweak as a card with an on/off toggle, a one-line explanation and an info button. Only tweaks that fit the detected hardware are shown.
- **Games:** the games it found (Steam, Epic, plus any you add), and what happens when one launches.
- **Stats:** live CPU/GPU temps, usage, clocks, RAM, and FPS-relevant warnings (on battery, thermal throttling).

The score is honest: the share of recommended tweaks that are switched on. No made-up "+1.9%" numbers like Hone shows.

## Hardware detection

On launch it reads CPU, GPU(s), RAM, battery present (laptop or desktop), manufacturer and model. Tweaks declare what they need, for example "Nvidia GPU", "Intel hybrid CPU", "laptop" or "MSI". On Peter's laptop it will find Intel P/E cores, the Iris Xe integrated GPU plus the RTX 3070 Ti, and the MSI Raider model.

## The tweaks (all reversible)

**Biggest FPS wins on this laptop**
1. **Run games on the RTX 3070 Ti.** Sets the Windows per-app GPU preference to "High performance" for each detected game, so nothing accidentally runs on the Intel graphics.
2. **Discrete Graphics Mode (MUX switch) check.** The GE66 can bypass the Intel GPU entirely, which is often a 5 to 15% FPS gain on the laptop screen. That switch lives in MSI Center and needs a reboot, so the app detects the current mode, recommends it, and opens MSI Center for you. It won't flip it itself (no safe public way to do that).
3. **MSI Center "Extreme Performance" check.** Same idea: detect and recommend, with a button to open MSI Center.
4. **Plugged-in warning.** On battery the 3070 Ti runs at a fraction of its power, so the app warns when a game starts unplugged.

**Windows settings**
5. Game Mode on.
6. Hardware-accelerated GPU scheduling on (needs a reboot).
7. Xbox Game Bar background recording (Game DVR) off.
8. High-performance power plan while gaming, back to your normal plan afterwards.
9. Power throttling off for game processes, so Windows doesn't push game threads onto the slower E-cores.
10. Game process priority set to "High" while it runs.
11. Telemetry (DiagTrack) service off.
12. Visual effects set to performance (optional; small gain).

**Not included (not safe or not worth it):** turning off Defender or Windows Update, registry "cleaners", RAM "boosters", writing directly to the laptop's embedded controller for fan control, overclocking.

## Safety

- A Windows restore point is created before the first change.
- Every tweak records the exact old value before changing it, so each toggle can be undone on its own, and "Restore everything" puts all of them back.
- Backups page lists what was changed and when.

## Background mode

- Starts with Windows (a scheduled task with admin rights, so you don't get a UAC prompt every boot) and sits in the system tray.
- Watches for game processes from the Games list. When one starts: gaming power plan, high priority, no throttling, plugged-in check. When it closes: everything goes back.
- Uses almost no CPU while idle, because it waits for Windows' process-start events instead of polling.

## Live stats

- GPU: temperature, load, clocks and VRAM from Nvidia's own driver library (NVML), which is safe and needs no extra drivers.
- CPU usage and RAM from Windows performance counters.
- CPU temperature needs a small hardware-reading driver. The plan is LibreHardwareMonitor's library, current version only, since older versions shipped a driver that Windows Defender now blocks. If that's a problem on your laptop, CPU temp gets dropped and the rest still works.

## How it's built

- **C# / .NET 10 / WPF**, published as one self-contained `.exe` (no installer, nothing else to install).
- Runs as administrator, since every tweak touches system settings.
- Code lives in a GitHub repo. GitHub builds the `.exe` on a Windows machine and attaches it to a release you download.
- Not code-signed (it's just for you), so Windows SmartScreen will warn once on first run.

## Build order

1. **Shell and safety:** dark window, sidebar, hardware detection, restore point, backup/undo engine. Nothing tweaks yet. Done.
2. **Tweaks:** toggles, score ring and "Apply all". Done.
3. **Stats panel.** Done.
4. **Background mode:** tray icon, start with Windows, game detection, automatic profile switching. Done.
5. **Polish:** dashboard gaming status, dark scroll bars, final code and security review, write-up. Done; testing on the laptop is next.

## What changed from the plan while building

- **CPU temperature is left out.** Every way of reading it needs a kernel driver (LibreHardwareMonitor's included), which isn't worth the risk in an always-admin app. GPU temperature comes from Nvidia's own driver.
- **Visual effects tweak dropped.** On a 3070 Ti the gain is too small to notice, and it makes Windows look worse.
- **Power mode instead of power plan.** Windows 11's power mode slider (Settings > Power) is the supported way to get full performance on top of the Balanced plan, so the gaming profile sets it to Best performance and back.
- **Start with Windows installs a copy in Program Files.** The startup task runs the app as admin, so it starts a copy only admins can change rather than the file in Downloads.
- **The dashboard has no shortcut tiles.** It shows the recommendations, the gaming profile status and your PC's hardware instead; the sidebar covers the rest.
