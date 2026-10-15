# Jets Rust Admin

Windows desktop admin panel for Rust servers over WebRCON - the rebuilt and renamed **Jet's Admin Panel**, with a fully rounded, polished dark interface.

**Features:** live dashboard with player-count chart, player list with kick / ban / give / teleport / roles and custom action buttons, live player map (RustMaps image), RCON console with history, live chat, bans, server settings and events, restart scheduler (works with JetsScheduledRestart), Oxide plugins and permissions, saved server profiles, auto-reconnect.

**Install:** download `JetsRustAdmin-Setup.exe` from the latest [Release](../../releases/latest). If the old Jet's Admin Panel is installed, Setup offers to remove it - your saved profiles, RustMaps key and cached maps are copied over automatically on first start. The app has an **Updates** button (bottom right) and checks for new releases on start.

**Build locally:** run `build.bat` (needs only the .NET Framework compiler that ships with Windows).

## Releasing an update
1. Change the number in `VERSION` (e.g. `2.0.1`) and commit to `main`.
2. GitHub Actions builds the exe and installer and publishes release `v2.0.1`.
3. Installed copies see it on next start / when you click **Updates**.
