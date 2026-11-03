#!/usr/bin/env python3
"""Compiler launcher for the console build: keeps 64-bit high-word readers
away from the instruction that wrote their register.

The third hardware run (docs/VERIFICATION.md) showed a long built by
`sldi; or` and tested by `cmpdi` on the next instruction taking its low
word's sign, while the same register read a few instructions later held the
full value; compares and every load and store form were right on settled
values. An xSDK title runs in 32-bit mode. The working assumption, which
probe64.c measures on hardware: an instruction that reads a register's high
word right after that register is written can see a wrong high word.

This launcher (CMake's <LANG>_COMPILER_LAUNCHER) compiles each C/C++ source
to assembly, and before every high-word reader (doubleword compares and
traps, shifts and rotates that keep high bits, cntlzd, doubleword multiply
and divide, doubleword stores) inserts nops so that at least GAP
instructions separate it from the last write of the register it reads, then
assembles. Writes before a label or a conditional branch still count (the
code can fall through); a call, return or jump starts afresh.

    hazard.py <compiler> <args...>     (as CMake calls a launcher)
    hazard.py --self-test
"""
import os
import re
import subprocess
import sys

GAP = 4
UNCONDITIONAL = {"b", "ba", "bl", "bla", "blr", "blrl", "bctr", "bctrl"}
LINE = re.compile(r"^(\s+)([a-z][a-z0-9.+-]*)\s*(.*?)\s*(#.*)?$")
LABEL = re.compile(r"^[A-Za-z0-9_.$@]+:")
# Mnemonics that write no GPR (or write an FPR/VR) and read none of interest.
NO_GPR_DEST = re.compile(r"^(st|cmp|b|tw|td|mt|dc|ic|sync|lwsync|isync|eieio|nop|cr|mcrf|f|lf|v|lv|dss|trap|sc)")


def reg(t):
    t = t.strip().lstrip("%")
    if t.startswith("r"):
        t = t[1:]
    return t if t.isdigit() and int(t) < 32 else None


def high_reads(op, ops):
    """The registers whose high word this instruction reads."""
    if op in ("cmpd", "cmpld", "cmpdi", "cmpldi"):
        src = ops[1:] if len(ops) == 3 else ops
        return [reg(x) for x in (src if op in ("cmpd", "cmpld") else src[:1])]
    if op in ("td", "tdi") or op.startswith(("tdeq", "tdne", "tdlt", "tdgt", "tdle", "tdge", "tdl")):
        return [reg(x) for x in ops[1:]]
    if op in ("std", "stdu", "stdbrx", "stdx", "stdux", "stdcx."):
        return [reg(ops[0])]
    if op in ("clrldi", "clrldi."):
        return [] if len(ops) == 3 and ops[2].isdigit() and int(ops[2]) >= 32 else [reg(ops[1])]
    if op in ("rldicl", "rldicl."):
        if len(ops) == 4 and ops[2] == "0" and ops[3].isdigit() and int(ops[3]) >= 32:
            return []
        return [reg(ops[1])]
    if op.rstrip(".") in ("srdi", "sradi", "rldicr", "rldic", "rotldi", "rotld", "extrdi", "cntlzd", "popcntd", "extldi",
                          "rldcl", "rldcr"):
        return [reg(ops[1])]
    if op.rstrip(".") in ("rldimi", "insrdi"):
        return [reg(ops[0]), reg(ops[1])]
    if op.rstrip(".").rstrip("o") in ("srd", "srad", "sld", "mulld", "mulhd", "mulhdu", "divd", "divdu"):
        return [reg(x) for x in ops[1:3]]
    return []


def dest(op, ops):
    if not ops or NO_GPR_DEST.match(op):
        return None
    return reg(ops[0])


def pad(text):
    out, recent, inserted = [], [], 0   # recent: destination registers, newest last
    for line in text.split("\n"):
        if LABEL.match(line):
            out.append(line)    # code falling through keeps its recent writes
            continue
        if line.startswith("\t.section") or line.startswith("\t.text"):
            recent = []
            out.append(line)
            continue
        m = LINE.match(line)
        if not m or m.group(2).startswith("."):
            out.append(line)
            continue
        ind, op, args = m.group(1), m.group(2), m.group(3)
        ops = [a.strip() for a in args.split(",")] if args else []
        need = 0
        for r in high_reads(op, ops):
            if r is None:
                continue
            for back, w in enumerate(reversed(recent)):
                if w == r:
                    need = max(need, GAP - back)
                    break
        for _ in range(need):
            out.append(ind + "nop")
            recent.append(None)
        inserted += need
        out.append(line)
        if op.rstrip("+-") in UNCONDITIONAL or op in ("sc", "trap"):
            recent = []         # a call, return or jump: the next instruction runs much later
        else:
            recent.append(dest(op, ops))
        del recent[:-GAP]
    return "\n".join(out), inserted


def compile_padded(argv):
    cc, args = argv[0], argv[1:]
    src = next((a for a in args if a.endswith((".c", ".cpp", ".cc", ".cxx"))), None)
    if "-c" not in args or "-o" not in args or src is None:
        os.execvp(cc, argv)
    out = args[args.index("-o") + 1]
    asm, padded = out + ".s", out + ".pad.s"
    first = [a for a in args if a != "-c"]
    first[first.index("-o") + 1] = asm
    r = subprocess.call([cc, "-S"] + first)
    if r != 0:
        return r
    with open(asm) as f:
        text, _ = pad(f.read())
    lines = text.split("\n")
    machine = [a for a in args if a.startswith(("-mcpu", "-m32", "-mpowerpc64", "-mcall-"))]
    range_error = re.compile(r"\.pad\.s:(\d+): Error: operand out of range")
    inverse = {"beq": "bne", "bne": "beq", "blt": "bge", "bge": "blt", "bgt": "ble", "ble": "bgt", "bnl": "blt",
               "bng": "bgt", "bso": "bns", "bns": "bso", "bdnz": "bdz", "bdz": "bdnz"}
    r = 1
    for _ in range(12):
        with open(padded, "w") as f:
            f.write("\n".join(lines))
        p = subprocess.run([cc] + machine + ["-c", padded, "-o", out], stderr=subprocess.PIPE, universal_newlines=True)
        far = sorted({int(m.group(1)) for m in range_error.finditer(p.stderr)}, reverse=True)
        if p.returncode == 0 or not far:
            sys.stderr.write(p.stderr)
            r = p.returncode
            break
        # The nops pushed a conditional branch past its 32 KB reach: branch around an unconditional one.
        for n in far:
            m = LINE.match(lines[n - 1])
            op = m.group(2).rstrip("+-") if m else ""
            if op not in inverse:
                sys.stderr.write("hazard: %s: out of range, not a conditional branch: %s\n" % (src, lines[n - 1]))
                return 1
            parts = [a.strip() for a in m.group(3).split(",")]
            lines[n - 1:n] = [m.group(1) + inverse[op] + " " + ",".join(parts[:-1] + ["1f"]), m.group(1) + "b " + parts[-1], "1:"]
    for p in (asm, padded):
        try:
            os.remove(p)
        except OSError:
            pass
    return r


def self_test():
    src = "\n".join(["f:", "\tsldi 29,29,32", "\tor 29,29,7", "\tcmpdi 0,29,0", "\tb .L3",
                     "\tld 9,0(3)", "\taddi 4,4,1", "\taddi 5,5,1", "\taddi 6,6,1", "\taddi 7,7,1", "\tsrdi 3,9,32",
                     "\tclrldi 10,9,32", "\tlwz 11,0(3)", "\tstd 11,8(1)", "\tfadd 1,2,3", "\tcmpw 0,11,4"])
    out, n = pad(src)
    lines = out.split("\n")
    # cmpdi right after `or 29`: 4 nops; srdi 4 instructions after `ld 9`: none; clrldi 32: none;
    # std of r11 right after `lwz 11`: 4 nops; cmpw: not a high-word reader.
    i = lines.index("\tcmpdi 0,29,0")
    assert lines[i - 4:i] == ["\tnop"] * 4 and lines[i - 5] == "\tor 29,29,7", lines
    i = lines.index("\tsrdi 3,9,32")
    assert lines[i - 1] == "\taddi 7,7,1", lines
    i = lines.index("\tstd 11,8(1)")
    assert lines[i - 4:i] == ["\tnop"] * 4, lines
    assert n == 8, n
    out, n = pad("\n".join(["\txori 9,9,1", "\tbeq 0,.L5", ".L4:", "\tcmpdi 7,9,0"]))
    assert n == 2, out
    assert high_reads("cmpd", ["7", "9", "10"]) == ["9", "10"]
    assert high_reads("rldicl", ["9", "9", "0", "32"]) == []
    assert dest("fadd", ["1", "2", "3"]) is None and dest("mfcr", ["3"]) == "3"
    print("hazard self-test passed")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    sys.exit(compile_padded(sys.argv[1:]))
