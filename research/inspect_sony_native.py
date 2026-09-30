"""Static PE analysis; never loads or executes the inspected DLL."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent / "artifacts" / "python-libs"))
import argparse
import hashlib
import json
import capstone
import pefile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dll", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--rva", action="append", default=[], help="Additional function RVA (hex)")
    parser.add_argument("--range", action="append", default=[], help="Explicit start:end RVA range (hex), may include code/data")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    data = args.dll.read_bytes()
    pe = pefile.PE(data=data)
    exports = [{"name": s.name.decode() if s.name else None, "ordinal": s.ordinal, "rva": s.address}
               for s in pe.DIRECTORY_ENTRY_EXPORT.symbols]
    imports = [{"dll": entry.dll.decode(), "functions": [s.name.decode() if s.name else f"ordinal:{s.ordinal}"
               for s in entry.imports]} for entry in pe.DIRECTORY_ENTRY_IMPORT]
    functions = [{"begin": e.struct.BeginAddress, "end": e.struct.EndAddress}
                 for e in getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", [])]
    summary = {"sourcePath": str(args.dll), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest().upper(),
               "machine": f"0x{pe.FILE_HEADER.Machine:04X}", "imageBase": hex(pe.OPTIONAL_HEADER.ImageBase),
               "exports": exports, "imports": imports,
               "sections": [{"name": s.Name.decode(errors="replace").strip("\0"),
                             "rva": s.VirtualAddress, "rawSize": s.SizeOfRawData} for s in pe.sections]}
    (args.output / "pe-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    if pe.FILE_HEADER.Machine != 0x8664:
        raise ValueError("This analyser only disassembles x64 PE code.")
    disasm = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    targets = exports + [{"name": f"helper-{rva}", "rva": int(rva, 16)} for rva in args.rva]
    for value in args.range:
        begin, end = (int(part, 16) for part in value.split(":"))
        if not 0 < end - begin <= 8192:
            raise ValueError("Explicit range must be 1..8192 bytes.")
        targets.append({"name": f"range-{begin:x}-{end:x}", "rva": begin, "explicitSize": end - begin})
    for export in targets:
        if not export["name"] or not any(t in export["name"].lower() for t in ("battery", "charge", "telemetry", "read", "info", "test", "power", "helper", "range")):
            continue
        rva = export["rva"]
        boundary = next((f for f in functions if f["begin"] <= rva < f["end"]), None)
        size = export.get("explicitSize", min((boundary["end"] - rva) if boundary else 512, 8192))
        lines = [f"Export {export['name']}, RVA 0x{rva:X}, unwind boundary {boundary}"]
        for instruction in disasm.disasm(pe.get_data(rva, size), pe.OPTIONAL_HEADER.ImageBase + rva):
            lines.append(f"{instruction.address:016X} {instruction.bytes.hex():24} {instruction.mnemonic:8} {instruction.op_str}")
        (args.output / f"{export['name']}.asm").write_text("\n".join(lines), encoding="utf-8")
    print(f"PE {summary['machine']}, {len(exports)} exports. Saved static evidence to {args.output}")
    print("Exports: " + ", ".join(e["name"] or str(e["ordinal"]) for e in exports))


if __name__ == "__main__":
    main()
