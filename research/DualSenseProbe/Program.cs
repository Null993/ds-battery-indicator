using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HidLibrary;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2)
        {
            Console.Error.WriteLine("Usage: DualSenseProbe OUTPUT_DIRECTORY [SECONDS=10, range 1..3600]");
            return 2;
        }
        int seconds = args.Length == 2 ? int.Parse(args[1]) : 10;
        if (seconds < 1 || seconds > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var devices = HidDevices.Enumerate(0x054C, 0x0CE6)
            .Where(d => d.Capabilities.UsagePage == 1 && d.Capabilities.Usage == 5
                && d.Capabilities.InputReportByteLength == 64).ToArray();
        Save(output, "inventory.json", devices.Select(d => new
        {
            pathSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.DevicePath))),
            d.Description, d.Capabilities, d.Attributes
        }));
        if (devices.Length != 1)
        {
            Console.Error.WriteLine($"Expected one USB DualSense gamepad; found {devices.Length}. No device accessed.");
            return 3;
        }

        using var device = devices[0];
        device.OpenDevice();
        if (!device.IsOpen) throw new IOException("HID device could not be opened.");
        Save(output, "descriptor-capabilities.json", ReadDescriptorCapabilities(device));
        ReadKnownFeatures(device, output, "before");
        var sets = Enumerable.Range(0, 64).Select(_ => new HashSet<byte>()).ToArray();
        var counts = new Dictionary<string, int>();
        int samples = 0, failures = 0;
        var clock = Stopwatch.StartNew();
        using (var log = new StreamWriter(Path.Combine(output, "input-reports.jsonl"), false))
        {
            while (clock.Elapsed.TotalSeconds < seconds && device.IsConnected)
            {
                var report = device.ReadReport(100);
                if (report.ReadStatus != HidDeviceData.ReadStatus.Success || !report.Exists)
                {
                    failures++;
                    continue;
                }
                byte[] wire = report.GetBytes();
                log.WriteLine(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, elapsedMs = clock.Elapsed.TotalMilliseconds,
                    reportId = report.ReportId, length = wire.Length, hex = Convert.ToHexString(wire)
                }));
                if (report.ReportId != 1 || wire.Length != 64) continue;
                samples++;
                for (int i = 0; i < wire.Length; i++) sets[i].Add(wire[i]);
                string batteryByte = $"0x{wire[53]:X2}";
                counts[batteryByte] = counts.GetValueOrDefault(batteryByte) + 1;
            }
        }
        ReadKnownFeatures(device, output, "after");
        Save(output, "summary.json", new
        {
            utc = DateTimeOffset.UtcNow, requestedSeconds = seconds,
            actualSeconds = clock.Elapsed.TotalSeconds, samples, failures,
            batteryStatusByteCounts = counts,
            wireBytes = sets.Select((s, i) => new { index = i, uniqueCount = s.Count,
                values = s.Order().Select(x => $"{x:X2}").ToArray() }),
            limitation = "A short USB sample cannot establish true SOC precision or rule out hidden protocols."
        });
        var files = Directory.GetFiles(output).Where(f => !f.EndsWith("hashes.json"))
            .Order().Select(f => new { file = Path.GetFileName(f), bytes = new FileInfo(f).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) }).ToArray();
        Save(output, "hashes.json", files);
        Console.WriteLine($"Captured {samples} valid USB reports, {failures} failed reads. Artifacts: {output}");
        Console.WriteLine("Only input reads, capability queries, and GET_FEATURE 0x05/0x20 were issued.");
        return samples > 0 ? 0 : 4;
    }

    private static void ReadKnownFeatures(HidDevice device, string output, string phase)
    {
        // USB only. Known calibration and firmware-info queries used by Linux/SDL.
        // No unknown-ID scan, SET_FEATURE, output report, pairing, or firmware update commands.
        foreach (byte id in new byte[] { 0x05, 0x20 })
        {
            bool ok = device.ReadFeatureData(out byte[] data, id);
            Save(output, $"feature-{id:X2}-{phase}.json", new
            {
                utc = DateTimeOffset.UtcNow, reportId = id, success = ok,
                expectedLogicalBytes = id == 5 ? 41 : 64,
                apiBufferBytes = data?.Length ?? 0, hex = ok ? Convert.ToHexString(data!) : null,
                note = "HidLibrary uses FeatureReportByteLength buffer; success does not expose actual transfer length."
            });
        }
    }

    private static object ReadDescriptorCapabilities(HidDevice device)
    {
        if (!HidD_GetPreparsedData(device.ReadHandle, out var preparsed))
            throw new IOException($"HidD_GetPreparsedData failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var result = new List<object>();
            for (int type = 0; type <= 2; type++)
            {
                ushort count = checked((ushort)(type switch
                {
                    0 => device.Capabilities.NumberInputValueCaps,
                    1 => device.Capabilities.NumberOutputValueCaps,
                    _ => device.Capabilities.NumberFeatureValueCaps
                }));
                if (count == 0) continue;
                // Windows HIDP_VALUE_CAPS is 72 bytes, verified against hidpi.h layout.
                var buffer = new byte[count * 72];
                int status = HidP_GetValueCaps(type, buffer, ref count, preparsed);
                if (status != 0x00110000) throw new IOException($"HidP_GetValueCaps: 0x{status:X8}");
                for (int n = 0; n < count; n++)
                {
                    int offset = n * 72;
                    result.Add(new
                    {
                        reportType = new[] { "input", "output", "feature" }[type],
                        usagePage = BitConverter.ToUInt16(buffer, offset), reportId = buffer[offset + 2],
                        isRange = buffer[offset + 12] != 0,
                        bitSize = BitConverter.ToUInt16(buffer, offset + 18),
                        reportCount = BitConverter.ToUInt16(buffer, offset + 20),
                        logicalMin = BitConverter.ToInt32(buffer, offset + 40),
                        logicalMax = BitConverter.ToInt32(buffer, offset + 44),
                        usageMinOrUsage = BitConverter.ToUInt16(buffer, offset + 56),
                        usageMaxIfRange = BitConverter.ToUInt16(buffer, offset + 58),
                        rawValueCapsHex = Convert.ToHexString(buffer.AsSpan(offset, 72))
                    });
                }
            }
            return result;
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private static void Save(string output, string name, object value) =>
        File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Json));

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort count, IntPtr preparsed);
}
