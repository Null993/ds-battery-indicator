using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HidLibrary;
using DualSenseResearch;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 5 || args.Skip(2).Any(a => a != "--read-21-22" && a != "--read-battery-voltage" && a != "--read-extra-features"))
        {
            Console.Error.WriteLine("Usage: DualSenseProbe OUTPUT_DIRECTORY [SECONDS=10, range 1..3600] [--read-21-22] [--read-battery-voltage] [--read-extra-features]");
            return 2;
        }
        int seconds = args.Length >= 2 ? int.Parse(args[1]) : 10;
        bool readDiagnosticFeatures = args.Contains("--read-21-22");
        bool readBatteryVoltage = args.Contains("--read-battery-voltage");
        bool readExtraFeatures = args.Contains("--read-extra-features");
        var featureIds = new List<byte> { 0x05, 0x20 };
        if (readDiagnosticFeatures) featureIds.AddRange(new byte[] { 0x21, 0x22 });
        if (readExtraFeatures) featureIds.AddRange(new byte[] { 0x08, 0x0C });
        if (seconds < 1 || seconds > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        string output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Evidence directory is not empty. Choose a new session directory.");
            return 2;
        }
        Directory.CreateDirectory(output);
        Save(output, "session.json", new
        {
            startedUtc = DateTimeOffset.UtcNow, requestedSeconds = seconds,
            plannedGetFeatureIds = string.Join(",", featureIds.Select(id => id.ToString("X2"))),
            plannedBatteryVoltageQuery = readBatteryVoltage ? "SET_FEATURE 80 04 03 (+ zero padding), GET_FEATURE 81, every 2 seconds" : null,
            note = "Planned queries are not evidence of execution; check feature snapshots and process exit status."
        });
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
        var descriptor = ReadDescriptorCapabilities(device);
        Save(output, "descriptor-capabilities.json", descriptor);
        // New raw snapshots require an exact declaration on the connected device.
        // An advertised report is not evidence that GET is implemented or its content is telemetry.
        if (readExtraFeatures && !new byte[] { 0x08, 0x0C }.All(id => descriptor.Any(cap =>
            cap.reportType == "feature" && cap.reportId == id && cap.bitSize == 8
            && cap.reportCount == ExpectedLogicalBytes(id) - 1 && !cap.isRange)))
        {
            Console.Error.WriteLine("Feature 08/0C declarations do not match expected USB lengths. No feature or input queries issued.");
            return 5;
        }
        ReadKnownFeatures(device, output, "before", featureIds);
        var sets = Enumerable.Range(0, 64).Select(_ => new HashSet<byte>()).ToArray();
        var counts = new Dictionary<string, int>();
        int samples = 0, failures = 0;
        var clock = Stopwatch.StartNew();
        using (var log = new StreamWriter(Path.Combine(output, "input-reports.jsonl"), false))
        {
            double nextVoltageSeconds = 0;
            int voltageQuery = 0;
            while (clock.Elapsed.TotalSeconds < seconds && device.IsConnected)
            {
                if (readBatteryVoltage && clock.Elapsed.TotalSeconds >= nextVoltageSeconds)
                {
                    ReadBatteryVoltage(device, output, voltageQuery++);
                    nextVoltageSeconds = clock.Elapsed.TotalSeconds + 2;
                }
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
        ReadKnownFeatures(device, output, "after", featureIds);
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
        Console.WriteLine($"Issued input reads, capability queries, and GET_FEATURE {string.Join("/", featureIds.Select(id => $"0x{id:X2}"))}.");
        if (readBatteryVoltage)
            Console.WriteLine("Additionally submitted fixed battery-voltage query 80 04 03 and read 81; no charging control or calibration commands.");
        return samples > 0 ? 0 : 4;
    }

    private static int ExpectedLogicalBytes(byte id) => id switch
    {
        0x05 => 41, 0x08 => 48, 0x0C => 42, 0x21 => 5, _ => 64
    };

    private static void ReadKnownFeatures(HidDevice device, string output, string phase, IEnumerable<byte> featureIds)
    {
        // USB only. Known calibration and firmware-info queries used by Linux/SDL.
        // Optional 21/22 are advertised on the connected USB firmware; 22 includes HW/FW metadata.
        // Optional 08/0C are raw snapshots only; no payload meanings or units are assumed.
        // This method issues only GET_FEATURE; voltage-query mode separately submits fixed 80 04 03.
        foreach (byte id in featureIds)
        {
            // Preserve the API buffer and immediate error even when a GET fails.
            // This uses the descriptor's maximum Feature buffer, as required by HidD_GetFeature.
            var data = new byte[device.Capabilities.FeatureReportByteLength];
            data[0] = id;
            bool ok = HidD_GetFeature(device.ReadHandle, data, data.Length);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            Save(output, $"feature-{id:X2}-{phase}.json", new
            {
                utc = DateTimeOffset.UtcNow, reportId = id, success = ok, win32Error = error,
                expectedLogicalBytes = ExpectedLogicalBytes(id),
                apiBufferBytes = data.Length, hex = ok ? Convert.ToHexString(data) : null,
                rawApiBufferHex = Convert.ToHexString(data),
                note = "FeatureReportByteLength API buffer; transfer length is unknown. Failed-call buffer is not a valid device response."
            });
        }
    }

    private static void ReadBatteryVoltage(HidDevice device, string output, int index)
    {
        // Opt-in query only; there is deliberately no generic device/action parameter.
        var request = new byte[64];
        request[0] = 0x80;
        request[1] = 4;
        request[2] = 3;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool sent = HidD_SetFeature(device.WriteHandle, request, request.Length);
        int sendError = sent ? 0 : Marshal.GetLastWin32Error();
        var attempts = new List<object>();
        int? millivolts = null;
        var timer = Stopwatch.StartNew();
        if (sent)
        {
            for (int attempt = 0; attempt < 25 && timer.ElapsedMilliseconds < 1000; attempt++)
            {
                var response = new byte[64];
                response[0] = 0x81;
                bool received = HidD_GetFeature(device.ReadHandle, response, response.Length);
                int error = received ? 0 : Marshal.GetLastWin32Error();
                millivolts = received ? BatteryVoltageResponse.ParseMillivolts(response) : null;
                attempts.Add(new { utc = DateTimeOffset.UtcNow, elapsedMs = timer.Elapsed.TotalMilliseconds,
                    success = received, win32Error = error, hex = Convert.ToHexString(response), millivolts });
                // Complete but implausible, unexpected paged data, or API failure: do not reinterpret or retry command.
                if (!received || millivolts.HasValue || (response[1] == 4 && response[2] == 3 && response[3] >= 2)) break;
                Thread.Sleep(10);
            }
        }
        Save(output, $"battery-voltage-{index:D4}.json", new
        {
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
            requestHex = Convert.ToHexString(request), sendSuccess = sent, sendWin32Error = sendError,
            attempts, millivolts,
            source = "daidr/dualsense-tester 62dce1703a468a20af901db5e31e476ce5ec24b3 ds.util.ts getBatteryVoltage",
            limitation = "Voltage query only. No verified current, exact SOC or battery charging watts. Fixed API buffer does not establish transfer length."
        });
        Console.WriteLine($"Battery-voltage query {index}: {(millivolts.HasValue ? $"{millivolts} mV" : "unavailable")}");
    }

    private sealed record DescriptorCapability(string reportType, ushort usagePage, byte reportId,
        bool isRange, ushort bitSize, ushort reportCount, int logicalMin, int logicalMax,
        ushort usageMinOrUsage, ushort usageMaxIfRange, string rawValueCapsHex);

    private static List<DescriptorCapability> ReadDescriptorCapabilities(HidDevice device)
    {
        if (!HidD_GetPreparsedData(device.ReadHandle, out var preparsed))
            throw new IOException($"HidD_GetPreparsedData failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var result = new List<DescriptorCapability>();
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
                    result.Add(new DescriptorCapability(new[] { "input", "output", "feature" }[type],
                        BitConverter.ToUInt16(buffer, offset), buffer[offset + 2], buffer[offset + 12] != 0,
                        BitConverter.ToUInt16(buffer, offset + 18), BitConverter.ToUInt16(buffer, offset + 20),
                        BitConverter.ToInt32(buffer, offset + 40), BitConverter.ToInt32(buffer, offset + 44),
                        BitConverter.ToUInt16(buffer, offset + 56), BitConverter.ToUInt16(buffer, offset + 58),
                        Convert.ToHexString(buffer.AsSpan(offset, 72))));
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
    private static extern bool HidD_SetFeature(IntPtr handle, byte[] data, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(IntPtr handle, [In, Out] byte[] data, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort count, IntPtr preparsed);
}
