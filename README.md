<p align="center">
  <img src="docs/logo.png" width="110" alt="Orbit">
</p>

<h1 align="center">Orbit</h1>

<p align="center">
  A Roblox launcher for Windows that handles your accounts, runs several clients at once and lets you tune the game.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/ph1l59/Orbit?color=a855f7&label=download&cacheSeconds=300" alt="Download"></a>
  <img src="https://img.shields.io/badge/windows-10%20%7C%2011-a855f7" alt="Windows 10 | 11">
  <img src="https://img.shields.io/github/license/ph1l59/Orbit?color=a855f7" alt="License">
</p>

<br>

## What it does

| | |
|---|---|
| **Accounts** | Log in on the real roblox.com page. Orbit only keeps the login cookie, encrypted with Windows DPAPI. Every account shows its avatar and whether it's online. |
| **Create accounts** | Set birthday and gender once, paste a list of `Username:Password`, and Orbit fills out the sign up page for each one. You click Sign Up yourself. |
| **Joining** | Place ID, game link, private server link, share link, share code or Job ID. Orbit figures it out. |
| **Multi-Roblox** | Launch several accounts in a row and keep them all open. Close every Roblox window at once with one button. |
| **Game settings** | Graphics quality, FPS limit, transparency, font size, mouse sensitivity and more. Can be locked so Roblox doesn't reset them. |
| **FastFlags** | Presets for mesh detail, MSAA, textures, rendering mode and more, plus a full editor with JSON import and export. |
| **Mods** | Custom cursor, custom font and a mods folder that mirrors Roblox's files. Originals get backed up. |
| **Bootstrapper** | Orbit installs and updates its own copy of Roblox and can take over the Play button on the website. Includes a cleaner for cache, logs and old versions. |
| **Integrations** | Discord Rich Presence, server location, activity tracking and programs that start and stop with Roblox. |
| **Auto clicker** | 1 to 1000 CPS, hotkey (toggle or hold), click limit, Roblox only mode. |
| **Updates** | When a new version is out, a notice slides in. One click downloads it, checks it and restarts Orbit. |

Can't find a setting? There's a search bar in the sidebar.

## Download

Get `Orbit.exe` from the **[latest release](../../releases/latest)** and run it. No installer needed. After that, Orbit keeps itself up to date.

Windows might show a SmartScreen warning the first time, because the app isn't code signed. Click **More info**, then **Run anyway**. All the code is right here if you want to check it.

## Good to know

**FastFlags:** Since September 2025 Roblox only accepts flags from its official allowlist. Orbit only uses those.

**Multi-Roblox and auto clicker:** Use at your own risk. Roblox doesn't officially support multiple clients and some games don't allow alts or macros.

**Discord:** Rich Presence needs a free Discord application. Paste its ID under Integrations.

**Stay safe:** Never share your `accounts.dat`, your `.ROBLOSECURITY` cookie or `rbx-player` links with anyone.

**Where things are:** Settings, mods and accounts are in `%AppData%\Orbit`, Orbit's Roblox install is in `%LocalAppData%\Orbit`.

## Building

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet publish src/Orbit/Orbit.csproj -c Release -o publish
```

GitHub Actions builds every push to `main`. When a release is published, the fresh `Orbit.exe` gets attached automatically.

## License

[GPL v3.0](LICENSE) · made by Floshiii

Orbit is not affiliated with or endorsed by Roblox Corporation.
