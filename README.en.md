# DS Battery Indicator

Other Settings includes an optional automatic window visibility checkbox (off by default). On Save, the window follows USB controller connection state, including on startup. The tray can still override visibility until the next connection change. Turning the option off restores the manual visibility preference. Automatic display does not take foreground focus; detection follows the configured polling interval.

DualSense's standard HID battery field reports coarse 10% bands, not exact percentages. This app displays each band's lower bound (20% means approximately 20–29%). Full charge displays 100% without a charging animation. Linux, SDL and pydualsense often display the band midpoint (25%); that is still an estimate. See [battery reading review](docs/battery-reading-review.md) for sources and verification steps.

[中文](README.md)

A Windows 10/11 desktop app that displays DualSense controller battery level via a floating overlay. Reads real-time battery data from USB HID reports with low-battery triple-channel alerts.

## Features

- **Floating Battery Overlay**: Dark rounded card with ring progress bar + percentage. Draggable, always-on-top, adjustable opacity
- **Low Battery Alerts**: Tray balloon + system sound + controller haptic/lightbar (three parallel channels, non-intrusive during gaming)
- **Charging Animation**: Static blue ring switches to green rotating dashes. No alerts while charging
- **System Tray**: Hover to see battery status, double-click to toggle window visibility
- **RTSS Overlay**: Display battery level in-game via RivaTuner Statistics Server
- **Bilingual UI**: Auto-detect system language, one-click switch between Chinese and English
- **Auto Start**: Optional registry-based startup
- **Persistent State**: Window position and visibility saved across restarts

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- DualSense controller (USB connection, VID 0x054C / PID 0x0CE6)
- RTSS overlay requires RivaTuner Statistics Server

## Build

```bash
cd DsBatteryIndicator
dotnet build
dotnet run
```

## Publish

Run `publish.bat` or manually:

```bash
cd DsBatteryIndicator
dotnet publish -c Release -o ../publish
```

## Project Structure

```
DsBatteryIndicator/
├── App.xaml/.cs              Entry point + system tray
├── Models/
│   └── DualSenseDevice.cs    Device state model
├── Services/
│   ├── HidService.cs         HID communication (enumerate/read/output report)
│   ├── BatteryParser.cs      Report parser (battery level/charging status)
│   ├── NotificationService.cs Low battery notification
│   └── AppSettings.cs        JSON-based settings persistence
├── Resources/
│   └── Strings.cs            Bilingual string resources
├── Plugins/
│   ├── IPlugin.cs            Plugin interface
│   └── RtssPlugin/
│       └── RtssService.cs    RTSS shared memory writer
├── ViewModels/
│   └── MainViewModel.cs      MVVM ViewModel
└── Views/
    ├── MainWindow.xaml/.cs   Floating window
    ├── BatteryRing.xaml/.cs  Ring progress control
    └── HapticSettingsWindow  Settings window
```

## References

- DualSense USB HID report format: Linux kernel `hid-playstation.c`
- RTSS shared memory: RTSS SDK `RTSSSharedMemory.h`
- Output report format: `daidr/dualsense-tester`
