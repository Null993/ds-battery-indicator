"""Offline analysis only; preserves input files and records hashes and limitations."""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import zlib


def entropy(data):
    if not data:
        return 0
    return -sum((n / len(data)) * math.log2(n / len(data)) for n in collections.Counter(data).values())


def firmware_analysis(path):
    data = path.read_bytes()
    strings = [(m.start(), m.group().decode("ascii")) for m in re.finditer(rb"[ -~]{8,}", data)]
    return {
        "file": path.name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest().upper(),
        "header128Hex": data[:128].hex().upper(), "entropyBitsPerByte": entropy(data),
        "header": {
            "buildDateAt0x40": data[0x40:0x50].split(b"\0")[0].decode("ascii", errors="replace"),
            "buildTimeAt0x50": data[0x50:0x60].split(b"\0")[0].decode("ascii", errors="replace"),
            "pidAt0x62": f"0x{struct.unpack_from('<H', data, 0x62)[0]:04X}",
            "updateVersionAt0x78": f"0x{struct.unpack_from('<H', data, 0x78)[0]:04X}",
        },
        "ascii8Strings": [{"offset": offset, "text": text} for offset, text in strings],
        "keywordStrings": [{"offset": offset, "text": text} for offset, text in strings
                           if re.search(r"battery|voltage|current|power|charging", text, re.I)],
        "blocks16KiB": [{"offset": i, "bytes": len(data[i:i + 16384]),
                         "entropy": entropy(data[i:i + 16384])} for i in range(0, len(data), 16384)],
        "interpretationLimit": "High entropy alone cannot distinguish encryption, compression, or opaque binary data. No ISA or load address identified.",
    }


def capture_analysis(capture):
    reports = [json.loads(line) for line in (capture / "input-reports.jsonl").read_text().splitlines()]
    reports = [r for r in reports if r["reportId"] == 1 and r["length"] == 64]
    wires = [bytes.fromhex(r["hex"]) for r in reports]
    feature_changes = {}
    for report_id in ("05", "20"):
        before = json.loads((capture / f"feature-{report_id}-before.json").read_text())
        after = json.loads((capture / f"feature-{report_id}-after.json").read_text())
        feature_changes[report_id] = {"successBefore": before["success"], "successAfter": after["success"],
                                     "identicalHex": before["hex"] == after["hex"]}
    crc_matches = collections.Counter()
    for wire in wires:
        for end in (56, 60):
            expected = struct.unpack_from("<I", wire, end)[0]
            for prefix in (b"", b"\xA1"):
                for start in (0, 1):
                    key = f"end={end},start={start},prefix={prefix.hex()}"
                    if zlib.crc32(prefix + wire[start:end]) == expected:
                        crc_matches[key] += 1
    counters = []
    for offset in (28, 49):
        values = [struct.unpack_from("<I", w, offset)[0] for w in wires]
        deltas = [(b - a) & 0xFFFFFFFF for a, b in zip(values, values[1:])]
        counters.append({"wireOffset": offset, "first": values[0], "last": values[-1],
                         "medianDelta": sorted(deltas)[len(deltas) // 2] if deltas else None,
                         "mostCommonDeltas": collections.Counter(deltas).most_common(8)})
    firmware = bytes.fromhex(json.loads((capture / "feature-20-before.json").read_text())["hex"])
    return {
        "capture": str(capture), "samples": len(wires),
        "batteryStatusCounts": dict(collections.Counter(f"0x{w[53]:02X}" for w in wires)),
        "featureBeforeAfter": feature_changes,
        "firmwareInfo": {
            "buildDate": firmware[1:12].decode("ascii"), "buildTime": firmware[12:20].decode("ascii"),
            "fwType": struct.unpack_from("<H", firmware, 20)[0],
            "swSeries": f"0x{struct.unpack_from('<H', firmware, 22)[0]:04X}",
            "hardwareInfo": f"0x{struct.unpack_from('<I', firmware, 24)[0]:08X}",
            "firmwareVersion": f"0x{struct.unpack_from('<I', firmware, 28)[0]:08X}",
            "updateVersion": f"0x{struct.unpack_from('<H', firmware, 44)[0]:04X}",
        },
        "counterCandidates": counters, "crc32Matches": dict(crc_matches),
        "tailByteEntropies": [{"wireOffset": i, "uniqueValues": len({w[i] for w in wires}),
                               "entropy": entropy(bytes(w[i] for w in wires))} for i in range(40, 64)],
        "interpretationLimit": "No discharge, charging, independent voltage/current measurement, or SOC transitions in this sample; changing bytes cannot be assigned units from correlation here.",
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("artifact_root", type=Path)
    args = parser.parse_args()
    root = args.artifact_root
    result = {"firmware": [firmware_analysis(p) for p in sorted((root / "firmware").glob("*.bin"))],
              "captures": [capture_analysis(p) for p in sorted((root / "captures").iterdir())
                           if (p / "input-reports.jsonl").exists()]}
    if len(result["firmware"]) >= 2:
        files = sorted((root / "firmware").glob("*.bin"))
        a, b = (f.read_bytes() for f in files[:2])
        result["firmwareComparison"] = {
            "files": [f.name for f in files[:2]], "equalLength": len(a) == len(b),
            "sameBytesAtSameOffset": sum(x == y for x, y in zip(a, b)),
            "comparedBytes": min(len(a), len(b)),
            "unchanged16KiBBlocks": [i for i in range(0, min(len(a), len(b)), 16384)
                                     if a[i:i + 16384] == b[i:i + 16384]],
        }
    output = root / "offline-analysis.json"
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Saved {output}; {len(result['firmware'])} firmware files, {len(result['captures'])} captures.")


if __name__ == "__main__":
    main()
