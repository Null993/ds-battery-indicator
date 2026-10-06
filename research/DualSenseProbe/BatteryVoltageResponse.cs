using System.Buffers.Binary;

namespace DualSenseResearch;

// Fixed query documented by daidr/dualsense-tester at commit 62dce1703a468a20af901db5e31e476ce5ec24b3.
// The second payload word has not been verified as a current measurement.
public static class BatteryVoltageResponse
{
    public static int? ParseMillivolts(ReadOnlySpan<byte> wire)
    {
        if (wire.Length != 64 || wire[0] != 0x81 || wire[1] != 4
            || wire[2] != 3 || wire[3] != 2) return null;
        int value = BinaryPrimitives.ReadUInt16LittleEndian(wire[4..6]);
        return value is >= 2000 and <= 5000 ? value : null;
    }
}
