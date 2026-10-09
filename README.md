# Optimiser

A personal Windows app that tunes a gaming laptop for higher FPS, with safe, reversible tweaks.
Built for an MSI Raider GE66 (i7-12700H, RTX 3070 Ti), and it detects the hardware so it also behaves sensibly on other PCs.

**Download:** [Optimiser.exe (latest build)](https://github.com/Lowballing-coding/Optimiser/releases/download/latest/Optimiser.exe)

Windows SmartScreen will warn the first time because the app isn't code-signed: choose "More info", then "Run anyway".
It asks for administrator rights because every tweak changes a system setting.

The plan and the order things get built in are in [docs/plan.md](docs/plan.md).

## Building

Needs the .NET 10 SDK.

```
dotnet run --project tests/Optimiser.Tests      # self-checks (Windows only)
dotnet publish src/Optimiser -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
```

GitHub Actions builds `Optimiser.exe` on every pull request, and publishes it as the `latest` release on every push to `main`.
