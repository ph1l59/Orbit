<p align="center">
  <img src="src/Orbit/Assets/logo.png" width="96" alt="Orbit logo">
</p>

<h1 align="center">Orbit</h1>
<p align="center"><b>by Floshiii</b></p>

<p align="center">
  Roblox account manager, multi-instance launcher and FPS/graphics tweaks in one app.
</p>

---

## Features

- **Account manager** – add accounts by logging in on the real roblox.com page. Orbit only keeps the login cookie, encrypted with Windows DPAPI so it can only be read by your Windows user on your PC. Your password is never stored.
- **Join anything** – Place ID, game link, a specific server (Job ID) or a private server / share link.
- **Launch several accounts** – select accounts and start them one after another with a configurable delay.
- **Multi-Roblox** – run several Roblox clients at the same time (off by default, see below).
- **FPS limit** – any cap from 5 to 1000 FPS or unlimited, using Roblox's own `FramerateCap` setting.
- **Graphics** – quality level 1–21, texture quality, MSAA, graphics API, DPI-scaling fix, exclusive fullscreen, no grass, gray sky, plus your own FastFlags.
- **Modern dark UI** – one portable `.exe`, no installer.

## Download

Grab `Orbit.exe` from the [latest release](../../releases/latest) and run it. Windows 10 or 11 (64-bit) required.

Because the app is new and not code-signed, Windows SmartScreen may warn you the first time. Click **More info → Run anyway**. The full source is right here if you want to check it or build it yourself.

## Good to know

- **FastFlag allowlist:** since September 2025 Roblox only accepts FastFlags from its official allowlist. Everything Orbit offers is on that list; other custom flags are simply ignored by Roblox.
- **Multi-Roblox is at your own risk.** Roblox does not officially support several clients at once, and some games forbid alt accounts.
- **Never share** your `accounts.dat`, your `.ROBLOSECURITY` cookie or "rbx-player" links with anyone.
- Settings and accounts are stored in `%AppData%\Orbit`.

## Build it yourself

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet publish src/Orbit/Orbit.csproj -c Release -o publish
```

The finished `Orbit.exe` is in the `publish` folder. Every push to `main` is also built automatically by GitHub Actions, and publishing a GitHub release attaches the freshly built `Orbit.exe` to it.

## License

[GNU GPL v3.0](LICENSE) © Floshiii

Orbit is not affiliated with, endorsed by or associated with Roblox Corporation.
