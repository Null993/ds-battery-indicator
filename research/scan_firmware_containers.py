"""Bounded offline container scan; positive magic matches alone are not proof."""
from pathlib import Path
import hashlib
import json
import sys
import zlib
import bz2

root = Path(sys.argv[1])
results = []
for path in sorted(root.glob("FWUPDATE*.bin")):
    data = path.read_bytes()
    magic_matches = {}
    for label, magic in {"gzip": b"\x1f\x8b\x08", "xz": b"\xfd7zXZ\x00",
                         "bzip2": b"BZh", "lz4Frame": b"\x04\x22\x4d\x18",
                         "zstdFrame": b"\x28\xb5\x2f\xfd", "zip": b"PK\x03\x04"}.items():
        magic_matches[label] = [i for i in range(len(data)) if data.startswith(magic, i)]
    magic_validation = []
    for label in ("gzip", "bzip2"):
        for offset in magic_matches[label]:
            try:
                stream = zlib.decompressobj(31) if label == "gzip" else bz2.BZ2Decompressor()
                unpacked = stream.decompress(data[offset:], 4 * 1024 * 1024)
                magic_validation.append({"format": label, "offset": offset, "complete": stream.eof,
                                         "decodedBytes": len(unpacked)})
            except (zlib.error, OSError, EOFError) as error:
                magic_validation.append({"format": label, "offset": offset,
                                         "complete": False, "error": str(error)})
    candidates = 0
    decoded = []
    for i in range(len(data) - 2):
        cmf, flg = data[i:i+2]
        if cmf & 15 != 8 or cmf >> 4 > 7 or (cmf * 256 + flg) % 31:
            continue
        candidates += 1
        try:
            stream = zlib.decompressobj()
            unpacked = stream.decompress(data[i:], 4 * 1024 * 1024)
            if stream.eof and len(unpacked) >= 64:
                decoded.append({"offset": i, "consumedBytes": len(data) - i - len(stream.unused_data),
                                "decodedBytes": len(unpacked),
                                "decodedSha256": hashlib.sha256(unpacked).hexdigest()})
        except zlib.error:
            pass
    results.append({"file": path.name, "sha256": hashlib.sha256(data).hexdigest(),
                    "magicCandidates": magic_matches, "magicValidation": magic_validation,
                    "zlibHeaderCandidates": candidates,
                    "validCompleteZlibStreamsAtLeast64Bytes": decoded})
report = {"method": "All offsets; zlib header + complete stream checksum validation; max output 4 MiB.",
          "limitations": "No raw compression, proprietary format, dictionary or cipher detection. Magic can occur randomly.",
          "files": results}
(root / "container-scan.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
