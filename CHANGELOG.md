# Changelog

All notable changes to RBR Input Manager. Versions follow [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-10-23

### Added
- **Binding editor** for the game bindings and the RBRControls binds. Press **Bind** on an action, then press a
  button/key or move an axis on any connected device. **+** adds a second binding, **✕** removes one.
  - Each action only accepts what makes sense: steering needs an axis, pedals take an axis or a button, the rest
    buttons and keys.
  - If the input is already used by another action you can move it or keep it on both.
  - Edits stay pending until **Save to game**, which checks RBR and the RSF launcher are closed, backs up the files
    once, writes them and reads them back to verify.
- **Find bindings by pressing inputs**: while the app is in front, pressing a button or key (or moving an axis)
  jumps to where it is bound and highlights it. Can be turned off in Settings.
- **Control maps**: RBR keeps one map per set of connected controllers and creates a fresh default map when that
  set changes, which is why controls "reset". The app shows every map, marks the active one and can
  **copy an old map into the active one**.
- **In-game force feedback strength** (Options → Controls → Force Feedback → Strength) is read from the RBR profile
  and shown as a slider, with a warning when it isn't at 100% (adjust the force in your wheelbase software instead).
- **Profile selector** in the header: switch profiles, see when the game no longer matches the current profile
  ("Modified") and revert to it. A profile the game already matches is picked up automatically.
- Devices are listed under the bindings: the map's devices first (red dot when missing), then the other connected
  controllers.
- Version and update status in the status bar, checked against GitHub on every start.
- Collapsible sections and categories with animations.
- Changelog.

### Changed
- Windows 11 layered look: cards, header strips and accent-coloured categories; RBRControls tab before Force
  Feedback; Profiles and Settings open from the header and the status bar instead of tabs; the Devices tab is gone.
- Buttons are numbered from 0, like DirectInput and RBRControls (`#0`).
- Device input is read on a background thread and tabs are no longer rebuilt when switching, so jumps are instant.
- Applying or switching a profile backs up the files it overwrites.

### Fixed
- Controls files with more than one map failed to load.
- Profiles copied from a read-only controls file couldn't be overwritten or deleted.
- Button text turned black while pressed in the dark theme (WPF-UI template bug).
- Reload no longer greys out the button; its icon spins while reloading.

## [0.1.0] - 2026-10-23

First release.

- Shows RBR's game bindings (`SavedGames\pfMULLIGATAWNY.acm`), axis inversion, force feedback settings from
  `RichardBurnsRally.ini` and the RSF launcher, and the RBRControls plugin binds.
- Named input profiles.
- Backs up the original game files (with permissions, attributes and timestamps) the first time it opens an
  install, with a one-click restore.
- Self-updater from GitHub releases.
- Light and dark theme.

[0.2.0]: https://github.com/Jaizu/RBRInputManager/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Jaizu/RBRInputManager/releases/tag/v0.1.0
