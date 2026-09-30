using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shapes;
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
        Check(result!.ChargingPowerWatts == null, "Standard HID parser never invents battery-side power");

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
        batteryReceived.Invoke(window.ViewModel, new object[] { new DualSenseDevice
            { BatteryLevel = 100, Status = DeviceStatus.Normal } });
        Check(window.ViewModel.AccentColor is SolidColorBrush green && green.Color == Color.FromRgb(0x4A, 0xDE, 0x80),
            "100% outside charging state also uses green");
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
        Check(settings.FindName("ChkShowChargingPower") is System.Windows.Controls.CheckBox,
            "Other Settings exposes power display checkbox");
        cfg.ShowChargingPower = false;
        window.ViewModel.RefreshDisplaySettings();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Check(window.Width == 140 && window.Height == 64, "Power disabled uses compact layout");
        cfg.ShowChargingPower = true;
        window.ViewModel.RefreshDisplaySettings();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Check(window.Width == 160 && window.Height == 80, "Power enabled expands layout");
        Check(window.ViewModel.ChargingPowerText == "— W", "Unavailable power is not invented as zero");
        var chargingFixture = new DualSenseDevice { BatteryLevel = 50, IsCharging = true,
            Status = DeviceStatus.Charging, ChargingPowerWatts = 2.37 };
        batteryReceived.Invoke(window.ViewModel, new object[] { chargingFixture });
        Check(window.ViewModel.ChargingPowerText.Contains("2")
            && window.ViewModel.ChargingPowerText.EndsWith(" W"), "Measured power fixture is formatted in watts");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
        {
            batteryReceived.Invoke(window.ViewModel, new object[] { new DualSenseDevice
                { BatteryLevel = 50, IsCharging = true, Status = DeviceStatus.Charging, ChargingPowerWatts = invalid } });
            Check(window.ViewModel.ChargingPowerText == "— W", "Invalid power cannot become a numerical reading");
        }
        connectionChanged.Invoke(window.ViewModel, new object[] { false });
        Check(window.ViewModel.ChargingPowerText == "— W", "Disconnect clears power");
        var ring = new BatteryRing { Progress = 100, AccentColor = Brushes.LimeGreen };
        var fullRing = (Path)ring.FindName("FullRing");
        var arc = (Path)ring.FindName("ForegroundArc");
        Check(fullRing.Visibility == Visibility.Visible && arc.Visibility == Visibility.Collapsed
            && fullRing.Stroke == Brushes.LimeGreen, "100% uses a complete colored ring");
        ring.IsCharging = true;
        Check(fullRing.Visibility == Visibility.Visible, "Charging never replaces 100% progress");
        ring.Progress = 50;
        Check(fullRing.Visibility == Visibility.Collapsed && arc.Visibility == Visibility.Visible,
            "50% while charging retains a half arc");
        var segment = (ArcSegment)ring.FindName("ArcSegment");
        Check(Math.Abs(segment.Point.X - 24) < 0.001 && Math.Abs(segment.Point.Y - 44) < 0.001,
            "50% arc ends at the bottom");
        ring.Progress = 0;
        Check(fullRing.Visibility == Visibility.Collapsed && arc.Visibility == Visibility.Collapsed,
            "0% displays no colored progress dot");
        ring.Progress = 120;
        Check(fullRing.Visibility == Visibility.Visible, "Out-of-range progress is clamped");
        ring.Progress = double.NaN;
        Check(fullRing.Visibility == Visibility.Collapsed && arc.Visibility == Visibility.Collapsed,
            "Nonfinite progress does not generate invalid geometry");
        ring.IsCharging = false;
        if (Environment.GetCommandLineArgs().Length > 1)
        {
            string previews = System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[1]);
            System.IO.Directory.CreateDirectory(previews);
            batteryReceived.Invoke(window.ViewModel, new object[] { full });
            foreach (bool showPower in new[] { false, true })
            {
                cfg.ShowChargingPower = showPower;
                window.ViewModel.RefreshDisplaySettings();
                window.Show();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();
                if (showPower)
                {
                    var powerText = (System.Windows.Controls.TextBlock)window.FindName("PowerDisplay");
                    var batteryText = (System.Windows.Controls.TextBlock)window.FindName("BatteryPercentDisplay");
                    var powerCenter = powerText.TranslatePoint(new Point(powerText.ActualWidth / 2, 0), window);
                    var batteryCenter = batteryText.TranslatePoint(new Point(batteryText.ActualWidth / 2, 0), window);
                    Check(Math.Abs(powerCenter.X - batteryCenter.X) < 0.1,
                        "Power and battery text share the same center axis");
                }
                var bitmap = new RenderTargetBitmap((int)(window.Width * 2), (int)(window.Height * 2),
                    192, 192, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(System.IO.Path.Combine(previews,
                    showPower ? "full-battery-power-enabled.png" : "full-battery-power-disabled.png"));
                encoder.Save(stream);
            }
        }
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
