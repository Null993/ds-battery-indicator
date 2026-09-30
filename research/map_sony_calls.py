"""Offline direct-call and RIP-relative import map. Never executes target code.

Linear decoding can include embedded data; xrefs are candidates for manual review.
"""
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parent / "artifacts" / "python-libs"))
import capstone
import pefile
import json
import hashlib

dll, output = map(Path, sys.argv[1:3])
output.mkdir(parents=True, exist_ok=True)
data = dll.read_bytes()
pe = pefile.PE(data=data)
base = pe.OPTIONAL_HEADER.ImageBase
imports = {s.address: s.name.decode() if s.name else str(s.ordinal)
           for entry in pe.DIRECTORY_ENTRY_IMPORT for s in entry.imports}
boundaries = [(e.struct.BeginAddress, e.struct.EndAddress)
              for e in pe.DIRECTORY_ENTRY_EXCEPTION]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
md.skipdata = True
calls = []
lines = []
for section in pe.sections:
    if not section.Characteristics & 0x20000000:
        continue
    for ins in md.disasm(section.get_data(), base + section.VirtualAddress):
        rva = ins.address - base
        lines.append(f"{rva:08X} {ins.mnemonic:8} {ins.op_str}")
        if ins.mnemonic not in ("call", "jmp") or not ins.id:
            continue
        op = ins.operands[0]
        target = None
        if op.type == capstone.x86.X86_OP_IMM:
            target = hex(op.imm - base)
        elif op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
            target = imports.get(ins.address + ins.size + op.mem.disp)
        if target is not None:
            boundary = next((b for b in boundaries if b[0] <= rva < b[1]), None)
            calls.append({"at": hex(rva), "kind": ins.mnemonic,
                          "target": target, "unwind": boundary})
(output / "linear-text.asm").write_text("\n".join(lines), encoding="utf-8")
(output / "call-map.json").write_text(json.dumps({"sha256": hashlib.sha256(data).hexdigest(),
    "caveat": "Linear xrefs require control-flow validation; unwind chunks are not whole functions.",
    "calls": calls}, indent=2), encoding="utf-8")
for call in calls:
    if call["target"] in ("0x45b0", "0x4e40", "HidD_GetFeature", "HidD_SetFeature", "ReadFile"):
        print(call)
