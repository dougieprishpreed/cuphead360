#!/usr/bin/env python3
"""Compiler launcher for the console build: 64-bit compares the CPU gets right.

An xSDK title runs with MSR[SF] clear (32-bit mode, xsdk docs/HARDWARE.md),
and on the console a doubleword compare (cmpd, cmpdi, cmpld, cmpldi: L=1) then
compares only the registers' low words; the first hardware run formatted path
ids such as 5848540871337674781 as -12598203202371876835, its sign taken from
the low word (round 7, docs/VERIFICATION.md). xsdk's GCC (-m32 -mpowerpc64)
keeps 64-bit values in single registers and compares them with those
instructions.

This launcher (CMake's <LANG>_COMPILER_LAUNCHER) compiles each C/C++ source to
assembly with r12 kept free (-ffixed-r12), rewrites every doubleword compare
into word compares of the high words, then the low words, and assembles the
result. CR field, LT/GT/EQ and SO come out as the doubleword compare's:

    cmpd  cr,a,b    srdi r12,a,32; rotldi b,b,32; cmpw cr,r12,b; rotldi b,b,32
                    bne cr,1f; cmplw cr,a,b; 1:
    cmpld cr,a,b    the same with cmplw for the high words
    cmpdi cr,a,s    srdi r12,a,32; cmpwi cr,r12,(s<0?-1:0); bne cr,1f
                    s>=0: cmplwi cr,a,s   s<0: li r12,s; cmplw cr,a,r12; 1:
    cmpldi cr,a,u   srdi r12,a,32; cmplwi cr,r12,0; bne cr,1f; cmplwi cr,a,u; 1:

Doubleword traps (td, tdi), which compare like cmpd, stop the build, so a
new one is seen, not missed. Record forms ("rldicl." and the like) stay: in
32-bit mode CR0 reflects the result's low word, as GCC expects there.

The longer sequences can push a conditional branch past its 32 KB reach; the
assembler names each such line, and the launcher turns it into the inverted
branch over an unconditional one ("bne 7,.L5" -> "beq 7,1f; b .L5; 1:") and
assembles again.

    safecmp.py <compiler> <args...>     (as CMake calls a launcher)
    safecmp.py --self-test
"""
import os
import re
import subprocess
import sys

SCRATCH = "12"
SPECIAL = {"1", "2", "13", SCRATCH}   # stack pointer, TOC/small data, thread pointer, scratch
INSN = re.compile(r"^(\s*)(cmpd|cmpld|cmpdi|cmpldi)\s+([^#;]*?)\s*(#.*)?$")
# Doubleword compares in other spellings, which this rewriter does not handle.
REFUSED = re.compile(r"^\s*(td|tdi|tdeq|tdne|tdlt|tdgt|tdle|tdge|tdlgt|tdllt|tdlle|tdlge|tdlng|tdlnl|tdeqi|tdnei|tdlti|tdgti|"
                     r"tdlei|tdgei|tdlgti|tdllti|tdllei|tdlgei|cmp|cmpi|cmpl|cmpli)\s")
# Conditional branches and their inverses (CR-field and CTR forms).
INVERSE = {"beq": "bne", "bne": "beq", "blt": "bge", "bge": "blt", "bgt": "ble", "ble": "bgt", "bnl": "blt", "bng": "bgt",
           "bso": "bns", "bns": "bso", "bun": "bnu", "bnu": "bun", "bdnz": "bdz", "bdz": "bdnz"}
BRANCH = re.compile(r"^(\s*)([a-z]+?)([+-]?)\s+(.*?)\s*$")
RANGE_ERROR = re.compile(r"\.safe\.s:(\d+): Error: operand out of range")


def reg(text):
    t = text.strip()
    if t.startswith("%"):
        t = t[1:]
    if t.startswith("r"):
        t = t[1:]
    if not t.isdigit() or int(t) > 31:
        raise ValueError("not a register: %r" % text)
    return t


def crf(text):
    t = text.strip()
    if t.startswith("cr"):
        t = t[2:]
    if not t.isdigit() or int(t) > 7:
        raise ValueError("not a CR field: %r" % text)
    return t


def far_branch(line):
    """A conditional branch as the inverted branch over an unconditional one."""
    m = BRANCH.match(line)
    if not m or m.group(2) not in INVERSE:
        raise ValueError("out of range, not a conditional branch: " + line.strip())
    ind, op, _, args = m.groups()
    parts = [a.strip() for a in args.split(",")]
    return [ind + INVERSE[op] + " " + ",".join(parts[:-1] + ["1f"]), ind + "b " + parts[-1], "1:"]


def rewrite_line(line):
    """The replacement lines for one doubleword compare, or None for any other line."""
    m = INSN.match(line)
    if not m:
        if REFUSED.match(line):
            # cmp/cmpi/cmpl/cmpli with L as an operand: only L=0 is safe.
            ops = line.split(None, 1)
            if ops[0] in ("cmp", "cmpi", "cmpl", "cmpli"):
                parts = [p.strip() for p in ops[1].split(",")]
                if len(parts) == 4 and parts[1] == "0":
                    return None
            raise ValueError("mode-dependent instruction: " + line.strip())
        return None
    ind, op, args, _ = m.groups()
    ops = [a.strip() for a in args.split(",")]
    if len(ops) == 2:
        ops = ["0"] + ops
    if len(ops) != 3:
        raise ValueError("unexpected operands: " + line.strip())
    cr, a = crf(ops[0]), reg(ops[1])
    if a == SCRATCH:
        raise ValueError("compare of r12, the scratch register: " + line.strip())
    out = ["srdi %s,%s,32" % (SCRATCH, a)]
    if op in ("cmpd", "cmpld"):
        b = reg(ops[2])
        if b in SPECIAL:
            raise ValueError("compare against a reserved register: " + line.strip())
        out += ["rotldi %s,%s,32" % (b, b),
                "%s %s,%s,%s" % ("cmpw" if op == "cmpd" else "cmplw", cr, SCRATCH, b),
                "rotldi %s,%s,32" % (b, b),
                "bne %s,1f" % cr,
                "cmplw %s,%s,%s" % (cr, a, b)]
    else:
        v = int(ops[2], 0)
        if op == "cmpdi":
            if not -32768 <= v <= 32767:
                raise ValueError("immediate out of range: " + line.strip())
            out += ["cmpwi %s,%s,%d" % (cr, SCRATCH, -1 if v < 0 else 0), "bne %s,1f" % cr]
            out += ["cmplwi %s,%s,%d" % (cr, a, v)] if v >= 0 else ["li %s,%d" % (SCRATCH, v), "cmplw %s,%s,%s" % (cr, a, SCRATCH)]
        else:
            if not 0 <= v <= 65535:
                raise ValueError("immediate out of range: " + line.strip())
            out += ["cmplwi %s,%s,0" % (cr, SCRATCH), "bne %s,1f" % cr, "cmplwi %s,%s,%d" % (cr, a, v)]
    return [ind + x for x in out] + ["1:"]


def rewrite(text):
    out, n = [], 0
    for line in text.split("\n"):
        r = rewrite_line(line)
        if r is None:
            out.append(line)
        else:
            out.extend(r)
            n += 1
    return "\n".join(out), n


def compile_safe(argv):
    cc, args = argv[0], argv[1:]
    src = next((a for a in args if a.endswith((".c", ".cpp", ".cc", ".cxx"))), None)
    if "-c" not in args or "-o" not in args or src is None:
        os.execvp(cc, argv)
    out = args[args.index("-o") + 1]
    asm, safe = out + ".s", out + ".safe.s"
    first = [a for a in args if a != "-c"]
    first[first.index("-o") + 1] = asm
    r = subprocess.call([cc, "-S", "-ffixed-r" + SCRATCH] + first)
    if r != 0:
        return r
    try:
        with open(asm) as f:
            text, n = rewrite(f.read())
    except ValueError as e:
        sys.stderr.write("safecmp: %s: %s\n" % (src, e))
        return 1
    # Assemble: the machine flags only (the dependency file came from the first step).
    machine = [a for a in args if a.startswith(("-mcpu", "-m32", "-mpowerpc64", "-mcall-"))]
    lines = text.split("\n")
    r = 1
    for _ in range(12):
        with open(safe, "w") as f:
            f.write("\n".join(lines))
        p = subprocess.run([cc] + machine + ["-c", safe, "-o", out], stderr=subprocess.PIPE, universal_newlines=True)
        far = sorted({int(m.group(1)) for m in RANGE_ERROR.finditer(p.stderr)}, reverse=True)
        if p.returncode == 0 or not far:
            sys.stderr.write(p.stderr)
            r = p.returncode
            break
        try:
            for n in far:
                lines[n - 1:n] = far_branch(lines[n - 1])
        except ValueError as e:
            sys.stderr.write("safecmp: %s: %s\n" % (src, e))
            break
    else:
        sys.stderr.write("safecmp: %s: branches still out of range after 12 passes\n" % src)
    for p in (asm, safe):
        try:
            os.remove(p)
        except OSError:
            pass
    return r


def self_test():
    cases = {
        "\tcmpd 7,9,10": ["\tsrdi 12,9,32", "\trotldi 10,10,32", "\tcmpw 7,12,10", "\trotldi 10,10,32", "\tbne 7,1f",
                          "\tcmplw 7,9,10", "1:"],
        "\tcmpld 0,3,4": ["\tsrdi 12,3,32", "\trotldi 4,4,32", "\tcmplw 0,12,4", "\trotldi 4,4,32", "\tbne 0,1f",
                          "\tcmplw 0,3,4", "1:"],
        "\tcmpdi 0,3,-1": ["\tsrdi 12,3,32", "\tcmpwi 0,12,-1", "\tbne 0,1f", "\tli 12,-1", "\tcmplw 0,3,12", "1:"],
        "\tcmpdi 7,29,0": ["\tsrdi 12,29,32", "\tcmpwi 7,12,0", "\tbne 7,1f", "\tcmplwi 7,29,0", "1:"],
        "\tcmpldi 1,5,0x10": ["\tsrdi 12,5,32", "\tcmplwi 1,12,0", "\tbne 1,1f", "\tcmplwi 1,5,16", "1:"],
        "\tcmpw 7,9,10": None,
        "\tcmplwi 0,3,5": None,
        "\tcmp 7,0,9,10": None,
        "\trldicl. 9,9,0,40": None,
        "\trldicl. 7,8,0,32": None,
    }
    assert far_branch("\tbne 7,.L5") == ["\tbeq 7,1f", "\tb .L5", "1:"]
    assert far_branch("\tblt- 0,.L12") == ["\tbge 0,1f", "\tb .L12", "1:"]
    assert far_branch("\tbdnz .L3") == ["\tbdz 1f", "\tb .L3", "1:"]
    for src, want in cases.items():
        got = rewrite_line(src)
        assert got == want, (src, got)
    for bad in ("\tcmpd 7,9,12", "\ttdi 0,3,0", "\tcmp 7,1,9,10", "\tcmpd 7,9,1"):
        try:
            rewrite_line(bad)
        except ValueError:
            continue
        raise AssertionError("accepted " + bad)
    # Every compare outcome, on values that differ in the high word only, low word only, or both.
    vals = [0, 1, 5, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 1 << 32, (1 << 32) + 5, 0x512A30EF826F881D, 0xDC0618A67C751E35,
            (1 << 63) - 1, 1 << 63, (1 << 64) - 1, (1 << 64) - 5]
    def s64(x):
        return x - (1 << 64) if x >> 63 else x
    def word_cmp(x, y, signed):
        if signed:
            x, y = (x - (1 << 32) if x >> 31 else x), (y - (1 << 32) if y >> 31 else y)
        return (x > y) - (x < y)
    for x in vals:
        for y in vals:
            for signed in (True, False):
                hi = word_cmp(x >> 32, y >> 32, signed)
                got = hi if hi else word_cmp(x & 0xFFFFFFFF, y & 0xFFFFFFFF, False)
                want = ((s64(x) > s64(y)) - (s64(x) < s64(y))) if signed else ((x > y) - (x < y))
                assert got == want, (hex(x), hex(y), signed)
    print("safecmp self-test passed")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    sys.exit(compile_safe(sys.argv[1:]))
