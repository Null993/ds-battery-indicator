using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using DsBatteryIndicator;
using DsBatteryIndicator.Models;
using DsBatteryIndicator.Services;
using DsBatteryIndicator.ViewModels;
using DsBatteryIndicator.Views;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var cfg = AppSettings.Instance;
        cfg.LowBatteryThreshold = 10;
        cfg.LowBatteryAlertEnabled = false;
        cfg.RtssEnabled = false;
        Check(!JsonSerializer.Deserialize<AppSettings>("{}")!.AutoWindowVisibility,
            "Existing settings default to manual visibility");
        var saved = JsonSerializer.Serialize(new AppSettings { AutoWindowVisibility = true });
        Check(JsonSerializer.Deserialize<AppSettings>(saved)!.AutoWindowVisibility,
            "Auto visibility survives JSON round trip");

        // Full USB wire report: ID at 0, status at 53, adjacent byte deliberately different.
        var wire = new byte[64];
        wire[0] = 1;
        wire[52] = 0x09;
        wire[53] = 0x02;
        wire[54] = 0x18;
        var hidReport = new HidLibrary.HidReport(64,
            new HidLibrary.HidDeviceData(wire, HidLibrary.HidDeviceData.ReadStatus.Success));
        var result = BatteryParser.Parse(hidReport.ReportId, hidReport.Data, "fixture");
        Check(result is { BatteryLevel: 20, IsCharging: false, Status: DeviceStatus.Normal },
            "HidLibrary strips ID; parser reads wire byte 53, not adjacent status byte");

        for (byte level = 0; level <= 10; level++)
        {
            var data = new byte[63];
            data[52] = level;
            var device = BatteryParser.Parse(1, data, "fixture")!;
            Check(device.BatteryLevel == level * 10 && !device.IsCharging
                && device.Status == (level <= 1 ? DeviceStatus.LowBattery : DeviceStatus.Normal),
                $"Discharging level {level} keeps coarse battery and threshold semantics");
        }

        var payload = new byte[63];
        payload[52] = 0x11;
        Check(BatteryParser.Parse(1, payload, "fixture") is
            { BatteryLevel: 10, IsCharging: true, Status: DeviceStatus.Charging },
            "Charging suppresses low battery status");
        payload[52] = 0x29;
        var full = BatteryParser.Parse(1, payload, "fixture")!;
        Check(full is { BatteryLevel: 100, IsCharging: false, Status: DeviceStatus.FullyCharged },
            "Full status overrides stale capacity and stops charging animation");
        foreach (byte raw in new byte[] { 0x0B, 0x0F, 0xA2, 0xB2, 0xF2, 0x32 })
        {
            payload[52] = raw;
            Check(BatteryParser.Parse(1, payload, "fixture") == null,
                $"Reserved/error battery byte 0x{raw:X2} is rejected");
        }
        Check(BatteryParser.Parse(0x31, new byte[77], "fixture") == null, "BT report rejected");
        Check(BatteryParser.Parse(2, new byte[63], "fixture") == null, "Wrong ID rejected");
        Check(BatteryParser.Parse(1, new byte[9], "fixture") == null, "Short report rejected");
        Check(BatteryParser.Parse(1, new byte[64], "fixture") == null, "ID-inclusive payload rejected");

        // Real WPF window and App's actual PropertyChanged handler, without starting HID monitoring.
        var app = new App();
        app.InitializeComponent();
        cfg.AutoWindowVisibility = true;
        cfg.WindowVisible = true;
        var window = new MainWindow();
        typeof(App).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, window);
        var handler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
            typeof(PropertyChangedEventHandler), app,
            typeof(App).GetMethod("OnViewModelPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
        window.ViewModel.PropertyChanged += handler;
        window.ApplyWindowVisibility();
        Check(!window.IsVisible && cfg.WindowVisible, "Disconnected startup hides without changing preference");
        var connectionChanged = typeof(MainViewModel).GetMethod("OnConnectionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        connectionChanged.Invoke(window.ViewModel, new object[] { true });
        Check(window.IsVisible && !window.ShowActivated, "Connection shows window without activating it");
        var batteryReceived = typeof(MainViewModel).GetMethod("OnBatteryDataReceived",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        batteryReceived.Invoke(window.ViewModel, new object[] { full });
        Check(window.ViewModel.BatteryText == "100%" && !window.ViewModel.IsCharging,
            "Full charge renders 100% without charging animation");
        window.Hide();
        connectionChanged.Invoke(window.ViewModel, new object[] { true });
        Check(!window.IsVisible, "Repeated unchanged connection does not override manual hide");
        connectionChanged.Invoke(window.ViewModel, new object[] { false });
        Check(!window.IsVisible && window.ViewModel.BatteryText == "——"
            && window.ViewModel.BatteryLevel == 0 && !window.ViewModel.IsCharging,
            "Disconnect clears stale data and hides window");
        connectionChanged.Invoke(window.ViewModel, new object[] { true });
        Check(window.IsVisible, "Reconnect restores auto visibility");
        cfg.AutoWindowVisibility = false;
        cfg.WindowVisible = false;
        window.ApplyWindowVisibility();
        Check(!window.IsVisible, "Disabling automation restores saved hidden preference");
        connectionChanged.Invoke(window.ViewModel, new object[] { false });
        connectionChanged.Invoke(window.ViewModel, new object[] { true });
        Check(!window.IsVisible, "Manual hidden preference survives connection changes");
        cfg.WindowVisible = true;
        window.ApplyWindowVisibility();
        connectionChanged.Invoke(window.ViewModel, new object[] { false });
        Check(window.IsVisible, "Manual visible preference survives disconnect");
        window.Hide();
        connectionChanged.Invoke(window.ViewModel, new object[] { true });
        Check(!window.IsVisible, "Automation off never overrides an explicitly hidden window");
        var settings = new HapticSettingsWindow();
        Check(settings.FindName("ChkAutoWindowVisibility") is System.Windows.Controls.CheckBox,
            "Other Settings exposes the checkbox");
        window.Hide();
        window.ViewModel.Dispose();
        Console.WriteLine($"PASS: {_checks} regression checks. No hardware used; no settings written.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
    }
}
