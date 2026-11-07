# GhostScreen

A Windows desktop display-curtain prototype for covering or dimming the local screen while working with a remote session. It combines a WinForms control window, global recovery shortcuts, overlay modes and an optional virtual-display integration.

The interface contains ambitious “0% light” and remote-visibility labels. Actual results depend on the monitor, display driver, Windows topology and remote-capture method; those labels are not a hardware-independent test result.

## Modes and controls

- A software blackout curtain, plus a dimmed-overlay option.
- Optional WMI backlight adjustment on supported displays.
- Optional Parsec Virtual Display Driver integration to switch display topology.
- An eight-second safe test with automatic restoration.
- Tray controls and a single-instance mechanism: launching another copy brings back the existing UI.

| Shortcut | Action |
|---|---|
| Ctrl+Alt+B | Toggle blackout/restore |
| Ctrl+Alt+H | Hide/show the control window |
| Ctrl+Alt+U | Emergency physical-screen restoration and show the UI |

Global shortcuts can conflict with other software. Confirm they work on the target machine before leaving the physical display covered.

## Requirements

- Windows desktop.
- **.NET 10 SDK** with Windows Forms support for source builds.
- Hardware/driver support for whichever brightness or virtual-display mode is selected.

The app targets `net10.0-windows`. The source-only repository does **not** contain the Parsec driver installer. The built-in installation path expects `drivers/parsec-vdd-0.45.0.0.exe`; without that external dependency, the installer action cannot work. Obtain compatible driver software separately from its legitimate vendor and configure it deliberately.

## Build and run

```powershell
dotnet restore GhostScreen.csproj
dotnet build GhostScreen.csproj -c Release
dotnet run --project GhostScreen.csproj -c Release
```

The program also accepts `--start-blackout` or `-b` for an active start. Begin with a normal launch and the safe test rather than using that option on an unfamiliar display setup.

`Launch-GhostScreen.bat` is an existing local convenience launcher. Check the executable path it expects; a prebuilt executable is not part of this GitHub snapshot.

## Suggested first-use workflow

1. Launch normally and review driver/backlight availability in the UI.
2. Verify the recovery hotkeys and tray controls.
3. Run the eight-second safe test in software-curtain mode.
4. Check both the local display and your own authorised remote session.
5. Enable virtual-display switching only after the separate driver and restore behaviour have been verified.

## Source map

| File | Responsibility |
|---|---|
| `Program.cs` | Startup arguments, single instance and show-window signalling |
| `MainForm.cs` | UI, hotkeys, tray, mode selection and test countdown |
| `BlackoutOverlayForm.cs` | Curtain window |
| `BrightnessController.cs` | Supported backlight queries/adjustment and restore |
| `ParsecVddController.cs` | Optional virtual-display driver/topology operations |
| `NativeMethods.cs` | Windows API declarations |

## Limitations and validation

Backlight level zero can still leave visible light on some panels. A software overlay can be visible to, or excluded from, different capture mechanisms. Driver topology switching can behave differently across GPUs and remote tools. The application does not provide or authenticate the remote-desktop service itself.

This README was checked against source structure and control definitions; no new display-driver, remote-session or physical-panel test was performed. See [backup notes](BACKUP_NOTES.md) for source exclusions. No new license is assigned by this documentation.
