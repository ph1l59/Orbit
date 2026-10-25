<p align="center">
  <img src="docs/logo.png" width="120" alt="Orbit logo">
</p>

<h1 align="center">Orbit</h1>
<p align="center"><b>by Floshiii</b></p>

<p align="center">
  Your Roblox launcher: accounts, multi-instance, FastFlags, mods, integrations and an auto clicker – in one app.
</p>

---

## Features

- **Accounts** – add accounts by logging in on the real roblox.com page. Orbit only keeps the login cookie, encrypted with Windows DPAPI. Avatars and a live online/offline dot for every account.
- **Create accounts** – pick a birthday and gender once, list `Username:Password` lines, and Orbit fills in Roblox's sign-up page for each one in turn. Names and passwords are checked first; you click Sign Up and solve any check yourself, and every new account lands in your list automatically.
- **Join anything** – place ID or game link, plus an optional *server link*: private server links, share links (`roblox.com/share?code=…`), share codes or Job IDs.
- **Launch several accounts** one after another, with **Multi-Roblox** to keep them all open.
- **Global Settings** – Roblox's in-game settings (graphics quality, framerate limit, transparency, font size, mouse sensitivity, …) with a read-only lock.
- **FastFlags** – presets for mesh detail, MSAA, FRM quality, rendering mode, textures and more, plus a full Fast Flag editor with JSON import/export. Only allowlisted flags are used.
- **Mods** – a mods folder that mirrors Roblox's files (originals are backed up and restored), custom cursor, custom font and Windows compatibility settings.
- **Bootstrapper** – Orbit installs and updates its own copy of Roblox, can be the default launcher for the Play button on roblox.com, and includes a cleaner for cache, logs and old versions.
- **Integrations** – activity tracking, server location notifications, Discord Rich Presence and programs that start/stop with Roblox.
- **Auto Clicker** – 1–1000 clicks per second, global hotkey (toggle or hold), click limit, Roblox-only mode and fixed position.
- **Search** – find any setting from the sidebar.

## Download

Grab `Orbit.exe` from the [latest release](../../releases/latest) and run it. Windows 10 or 11 (64-bit).

Because the app isn't code-signed, Windows SmartScreen may warn you the first time. Click **More info → Run anyway**. The full source is right here.

## Good to know

- **FastFlag allowlist:** since September 2025 Roblox only accepts FastFlags from its official allowlist; other flags are ignored.
- **Multi-Roblox and the auto clicker are at your own risk.** Roblox doesn't officially support several clients, and some games forbid alt accounts or macros.
- **Discord Rich Presence** needs a (free) Discord application – paste its ID under Integrations.
- **Never share** your `accounts.dat`, your `.ROBLOSECURITY` cookie or "rbx-player" links with anyone.
- Settings, mods and accounts live in `%AppData%\Orbit`; Orbit's Roblox install in `%LocalAppData%\Orbit`.

## Build it yourself

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet publish src/Orbit/Orbit.csproj -c Release -o publish
```

Every push to `main` is built by GitHub Actions, and publishing a GitHub release attaches the freshly built `Orbit.exe` to it.

## License

[GNU GPL v3.0](LICENSE) © Floshiii

Inspired by Bloxstrap, Fishstrap and Roblox Account Manager. Orbit is not affiliated with, endorsed by or associated with Roblox Corporation.
