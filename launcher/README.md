# CNLauncher

The launcher testers and players run. It installs Crystallized Nexus, keeps it up to date,
and updates itself. Built with Avalonia, published as one self-contained executable per
platform (~23 MB) so nothing has to be installed first.

## For players

Download the file for your platform from the
[latest launcher release](https://github.com/DoGyAUT/crystallized-nexus/releases) and run
it. On Linux and macOS, mark it executable first (`chmod +x`).

The game needs the original Tiberian Sun assets. The launcher shows whether it found them;
if not, the game's own content installer fetches them on first start.

## How it works

- **Release selection.** Walks the GitHub releases list newest-first and takes the first
  release in the selected channel that actually has a package for the current platform. A
  build whose CI run has not finished falls back to the newest one that does.
- **Channels.** *Stable* accepts `release-*` (and the older `v*`) tags; *Playtest* accepts
  those plus `playtest-*`. `launcher-v*` releases are never offered as game builds.
- **Downloads** resume after an interruption, retry up to four times, and are verified
  against the release asset's SHA-256 digest where GitHub provides one.
- **Installing** replaces the install directory wholesale, so files removed by a newer
  build actually disappear. A `Support` folder inside it is carried across, because the
  engine treats that as a portable settings store. On the default per-user path, settings,
  replays and maps live in OpenRA's own support directory and are never touched.
- **Self-update** renames the running executable to `.old`, moves the new build into its
  place and restarts; the leftover is deleted on the next start. This is the one approach
  that works on Windows, where a running executable cannot be overwritten but can be
  renamed.

## Configuration

`launcher.json` in the per-user config directory holds the install path and the selected
channel. The installed game version is recorded as `.cn-version` **inside** the install
directory, so it always travels with the files it describes.

| Platform | Config directory |
|---|---|
| Windows | `%LOCALAPPDATA%\CrystallizedNexus` |
| Linux | `$XDG_CONFIG_HOME/crystallized-nexus` (default `~/.config/crystallized-nexus`) |
| macOS | `~/Library/Application Support/CrystallizedNexus` |

Local rather than roaming on Windows: the default install lives under this directory, and a
roaming profile must not try to sync a gigabyte of game files between machines.

Installations made by the pre-GUI launcher (a `game` folder and `version.txt` next to the
executable) are adopted on first start rather than re-downloaded.

## Cutting a release

Launcher versions are independent of game versions - the game ships every few days, the
launcher rarely.

```powershell
.modsdk\create-launcher-release.ps1 -Version 2.1.0
```

That pushes a `launcher-v2.1.0` tag, which triggers `.github/workflows/launcher.yml`. The
tag is the single source of truth for the version: the workflow passes it as
`-p:Version=`, so the `<Version>` in the csproj is only a local-development default. The
version must increase for installed launchers to offer the update.

Launcher releases are published as **full** releases while game builds are prereleases,
which is what keeps the launcher showing under "Latest release" on the repository front
page - the link to hand to ModDB.

## Building locally

```powershell
cd launcher\CNLauncher
dotnet publish -c Release -r win-x64 -o out
```

Valid runtime identifiers: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. All of them
cross-publish from any host, except that `win-x64` only gets its icon and version resource
embedded when built on Windows - which is why CI builds that one on a Windows runner.

The launcher ships fully trimmed (`TrimMode=full`) and compressed, which takes it from
105 MB to ~23 MB. Trimming is also why the config uses a source-generated
`JsonSerializerContext`: reflection-based JSON is not guaranteed to survive it. Trimming
warnings only appear during `publish`, never `build`, and CI publishes with
`-warnaserror` for exactly that reason.
