# RBR Input Manager

A Windows app for managing Richard Burns Rally (RallySimFans) input settings: game bindings, force feedback and the RBRControls plugin.

RBR stores controller bindings by DirectInput *instance GUID*. When Windows gives a device a new GUID (different USB port, driver reinstall, new hardware…) the game silently drops its bindings. This app shows what is bound to what, which devices are missing, and lets you keep named profiles of your setup.

## Features

- **Game controls**: every RBR action with its device, input and axis inversion (`SavedGames\pf<PROFILE>.acm`, `input.ini`; the most recently played RBR profile is used, or the one picked in Settings), plus a green/red status dot for each device. RBR keeps one control map per set of connected controllers; the app shows them all, marks the active one and can copy an old map into it.
- **Binding editor**: bind game actions and RBRControls functions by pressing a button/key or moving an axis. Edits stay pending until *Save to game* (backed up and verified).
- **Find by pressing**: with the app in front, press any input to jump to where it is bound.
- **RBRControls**: JoyKey and HShifter binds and options (`Plugins\RBRControls\RBRControls.ini`).
- **Force feedback**: the in-game strength slider (warns when it isn't at 100%), plus values from `RichardBurnsRally.ini` next to the RSF launcher's copy (`rsf_launcher\Configs\default.ini`) and any per-car overrides.
- **Profiles**: save the current bindings, axis inversion, RBRControls binds and FFB as a named profile; switch from the header selector, which also shows when the game no longer matches.
- **Original files backup**: the first time the app opens an install, it copies the files it can modify, along with their permissions, attributes and timestamps. **Settings → Restore originals** puts them back.
- **Logs**: a tab listing what the app read and changed (files written, each binding change, backups, settings, errors) for one page or all of them, in this session or one of the last 20, which are kept as readable text files.
- **Updates**: checks GitHub releases on startup and can update itself.
- Windows 11 (Fluent / Mica) look with light and dark themes.

## Download

Get `RBRInputManager.exe` from [Releases](https://github.com/Jaizu/RBRInputManager/releases). It is a single self-contained exe, so there is nothing to install.

The app stores its data in `%AppData%\RBRInputManager` (settings, profiles, original-file backups, session logs in `Logs`).

## Building

Requires the .NET 8 SDK.

```
dotnet build src/RBRInputManager
dotnet publish src/RBRInputManager -c Release -o publish
```

## Releasing

Add a section for the version to [CHANGELOG.md](CHANGELOG.md) (it becomes the release notes), then push a version tag; GitHub Actions builds the exe and creates the release:

```
git tag v0.2.0
git push origin v0.2.0
```
