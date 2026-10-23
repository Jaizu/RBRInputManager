# Changelog

All notable changes to RBR Input Manager. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Invert axes**: the Inverted column in Game controls is now a checkbox for each axis action (steering,
  throttle, brake, combined, handbrake, clutch). Changes are pending like binding edits and Save writes them to
  `input.ini` (backed up first). RBR keeps one flag per action, so it applies to every map. The live input
  display follows the pending value.

## [0.4.0] - 2026-10-23

### Added
- **Secondary gear up/down**: with NGPCarMenu's `SecondaryGearUpDownControls` on (the default) and a steering
  axis bound, RBR uses the Steer left / Steer right slots as Gear Up (Secondary) / Gear Down (Secondary). The
  app now names and binds them that way, like RBR's control screen, and the live display no longer treats them
  as steering.

### Fixed
- **RBR stuck loading the profile** after "Set to 100%" (in-game force feedback strength): the app patched
  pf<PROFILE>.rbr without updating the checksum in its first four bytes, so RBR rejected the profile and kept
  retrying. The real encoding and checksum are now known (taken from the game's own code: a bytewise cipher with
  a fixed 500-byte key, and a CRC-32 with RBR's own, faulty table), so the button writes a valid profile and
  reads it back to verify, putting the old file back if it doesn't. If RBR got stuck before this fix, put back
  the untouched copy from `%AppData%\RBRInputManager\ProfileBackups` (game closed).

## [0.3.0] - 2026-10-23

### Added
- Built-in **Default** profile holding the original game setup (taken from the first-run backup). It can be
  applied like any other profile but not overwritten or deleted.
- **Use a map with the connected controllers**: when no control map matches the controllers plugged in right now,
  the app says which controllers differ and can rewrite the selected map's device list to match, so RBR uses its
  bindings instead of creating a default map. Bindings on controllers that are no longer connected are removed
  (and listed); the controls file is backed up first.
- **Live input display** next to the tabs, styled after RBR's input HUD: steering wheel plus clutch, brake,
  throttle and handbrake bars, driven by the active map's bindings (including unsaved edits).
- Missing devices show when Windows last saw them ("last seen 21 Oct 2026, 18:02 (2 days ago)"). Devices seen in
  the last month get an amber dot instead of red.
- RBRControls binds on disconnected devices are matched by name against controllers Windows remembers.

### Changed
- Picking a profile in the header selector no longer writes it straight away. An icon next to it shows whether the
  game files match that profile (✓) or differ from it (⚠), and **Apply** writes it to the game when they differ.
  This replaces the "Modified" badge and the Revert button.
- The RBR profile is no longer assumed to be `MULLIGATAWNY`. The app finds every `SavedGames\pf*.acm`, uses the
  most recently played profile and lets you pick another one in **Settings → RBR profile**.
- New app logo: shown in the title bar and the install header, and used as the exe/taskbar icon.
- The window reopens at its last position and size (and maximized if it was). If that spot no longer fits on any
  screen, for example after unplugging a monitor, it opens centered at the default size.
- Tooltips are dark bubbles placed beside, below or above their target with an arrow pointing at it.

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

[0.4.0]: https://github.com/Jaizu/RBRInputManager/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Jaizu/RBRInputManager/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Jaizu/RBRInputManager/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Jaizu/RBRInputManager/releases/tag/v0.1.0
