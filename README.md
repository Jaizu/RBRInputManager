# RBR Input Manager

A Windows app for managing Richard Burns Rally (RallySimFans) input settings: game bindings, force feedback and the RBRControls plugin.

RBR stores controller bindings by DirectInput *instance GUID*. When Windows gives a device a new GUID (different USB port, driver reinstall, new hardware…) the game silently drops its bindings. This app shows what is bound to what, which devices are missing, and lets you keep named profiles of your setup.

## Features

- **Game controls**: every RBR action with its device, input and axis inversion (`SavedGames\pfMULLIGATAWNY.acm`, `input.ini`), plus a green/red status dot for each device.
- **Force feedback**: values from `RichardBurnsRally.ini` next to the RSF launcher's copy (`rsf_launcher\Configs\default.ini`), plus any per-car overrides.
- **RBRControls**: JoyKey and HShifter binds and options (`Plugins\RBRControls\RBRControls.ini`).
- **Devices**: controllers DirectInput sees right now.
- **Profiles**: save the current bindings, axis inversion, RBRControls binds and FFB as a named profile and apply it later.
- **Original files backup**: the first time the app opens an install, it copies the files it can modify, along with their permissions, attributes and timestamps. **Settings → Restore originals** puts them back.
- **Updates**: checks GitHub releases on startup and can update itself.
- Windows 11 (Fluent / Mica) look with light and dark themes.

## Download

Get `RBRInputManager.exe` from [Releases](https://github.com/Jaizu/RBRInputManager/releases). It is a single self-contained exe, so there is nothing to install.

The app stores its data in `%AppData%\RBRInputManager` (settings, profiles, original-file backups).

## Building

Requires the .NET 8 SDK.

```
dotnet build src/RBRInputManager
dotnet publish src/RBRInputManager -c Release -o publish
```

## Releasing

Push a version tag; GitHub Actions builds the exe and creates the release:

```
git tag v0.2.0
git push origin v0.2.0
```
