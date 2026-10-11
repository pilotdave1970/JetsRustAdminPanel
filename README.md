# Jet's Admin Panel for Rust

Windows desktop admin panel for Rust servers over WebRCON: live player map (RustMaps image), player actions, console, chat, bans, restart scheduler, plugins/permissions.

**Install:** download `JetsAdminPanel-Setup.exe` from the latest [Release](../../releases/latest). The app has an **Updates** button (bottom right) and checks for new releases on start.

**Build locally:** run `build.bat` (needs only the .NET Framework compiler that ships with Windows).

## Releasing an update
1. Change the number in `VERSION` (e.g. `1.0.1`) and commit to `main`.
2. GitHub Actions builds the exe and installer and publishes release `v1.0.1`.
3. Installed copies see it on next start / when you click **Updates**.
