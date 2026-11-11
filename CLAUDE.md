# Optimiser

A personal Windows WPF app (.NET 10, `net10.0-windows`) that tunes Peter's MSI Raider GE66 gaming laptop for FPS, plus a Rocket League Shot Trainer page, Clips editor, RGB, stats and a safe GPU overclock. Only Peter uses it, and Claude owns the code. The original plan is in `docs/plan.md`.

## You can't build or run this on the Mac

Development happens on macOS with no .NET SDK, and the app needs Windows anyway. GitHub Actions (`.github/workflows/build.yml`, `windows-latest`) is the only way to check a change:
1. Push a branch and open a PR. CI runs the self-checks, publishes `Optimiser.exe` and runs `Optimiser.exe --snapshot` to take a picture of every page.
2. The snapshots are force-pushed to the `snapshots` branch. Fetch that branch and look at the PNGs to check UI changes.
3. CI expects exactly 22 snapshots. Adding or removing a page means updating that count in `build.yml`.
4. Merging to `main` publishes the `latest` GitHub release. The app's update check reads the release title, and the commit message becomes "What's new".

If a run sits queued and GitHub refuses to cancel it, start a fresh one with `gh api -X POST repos/Lowballing-coding/Optimiser/actions/workflows/build.yml/dispatches -f ref=<branch>`.

Don't call a change done until CI is green and, for UI changes, you've looked at the snapshots.

## Layout

- `src/Optimiser/`: one project. Pages are `*Page.xaml` with code-behind; system logic lives in plain classes (`Tweaks.cs`, `Backup.cs`, `Display.cs`, `GameWatcher.cs`, `Fps.cs`, `Etw.cs`, ...).
- `tests/Optimiser.Tests/`: a console self-check program, not a test framework. It uses a throwaway registry key under HKCU and a temp folder. Add checks there for anything that touches the system.
- WPF projects don't have implicit `System.IO` usings, so add them explicitly.

## Rules

- Tweaks must be safe and reversible. Registry changes go through the `Backup` class (`Backup.cs`), which saves the original value first (and can make a restore point) so it can be undone, and the README's "Undoing things" section must stay true.
- Detect the hardware rather than assuming the GE66, so the app behaves sensibly on other PCs.
- The app runs as administrator. Be conservative with anything that writes to the registry, services, power plans or the GPU.
- Commit and PR titles are short, plain-English descriptions of what changed for the user, prefixed by the area when it fits (`Shot Trainer: ...`, `Clips: ...`). British English in the UI.
- Update the README when a feature visible to the user changes.

## Keeping this file current

When a session teaches you something lasting about this repo (a rule Peter stated or corrected, a gotcha, a needed check), add it here in the same PR as the work, and fix any line here that turned out to be wrong. Skip one-off details and anything secret.
