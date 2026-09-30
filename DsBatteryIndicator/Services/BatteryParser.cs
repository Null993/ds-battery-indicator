using DsBatteryIndicator.Models;

namespace DsBatteryIndicator.Services;

/// <summary>
/// DualSense USB HID 输入报告解析器。
/// HidReport.Data 不包含 Report ID：USB 完整报告 byte[53] 对应 Data[52]。
/// 低四位为约 10% 一档的电量，高四位为充电状态；不是精确百分比。
/// </summary>
public static class BatteryParser
{
    public static DualSenseDevice? Parse(byte reportId, byte[] data, string deviceId)
    {
        // Only USB's full 64-byte input report is supported. BT uses a different header/CRC.
        if (reportId != 0x01 || data.Length != 63) return null;

        int rawBattery = data[52];
        int level = rawBattery & 0x0F;

        int chargeState = (rawBattery >> 4) & 0x0F;
        // Do not interpret reserved levels or charging errors as an empty battery.
        if (level > 10 || chargeState > 2) return null;
        int batteryLevel = chargeState == 2 ? 100 : level * 10;
        bool isCharging = chargeState == 1;

        DeviceStatus status;
        if (chargeState == 2)
            status = DeviceStatus.FullyCharged;
        else if (isCharging)
            status = DeviceStatus.Charging;
        else if (batteryLevel <= AppSettings.Instance.LowBatteryThreshold)
            status = DeviceStatus.LowBattery;
        else
            status = DeviceStatus.Normal;

        return new DualSenseDevice
        {
            DeviceId = deviceId,
            BatteryLevel = batteryLevel,
            IsCharging = isCharging,
            Status = status
        };
    }
}
