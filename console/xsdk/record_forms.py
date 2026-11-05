#!/usr/bin/env python3
"""The console build's compiler launcher (CMake's C/CXX_COMPILER_LAUNCHER).

A title runs in 32-bit mode (MSR[SF] clear), where a record-form
instruction ("mr.", "and.", "rldicl." ...) sets CR0 from its result's low
word only. With -mpowerpc64 GCC keeps a 64-bit value in one register and
takes CR0 to judge all of it: `if (value >= 0)` on a long became
`mr. r7,r5; bge`, so long.ToString printed 2147483648 as
-18446744071562067968 on the console (round 11).

This compiles to assembly with GCC's insn names (-dp), follows each record
form of a 64-bit (DImode) pattern with `cmpdi 0,rD,0`, which sets CR0 from
all 64 bits as GCC meant (but not those whose result can't reach bit 31:
low_bits_only), and assembles the result. 64-bit carry patterns
(CA comes from bit 32 in 32-bit mode) are reported, not changed. A line per
object says what it found; CH360_KEEP_ASM=1 keeps the .s files.
"""
import os
import re
import subprocess
import sys

# "\tmr. 10,4\t # 17\t[c=4 l=4]  *movdi_internal2/1"
RECORD = re.compile(r'^\t([a-z][a-z0-9]*)\.\s+(\d+)\b.*#\s*\d+\s*\[[^\]]*\]\s*(\S+)')
INSN = re.compile(r'^\t[a-z].*#\s*\d+\s*\[[^\]]*\]\s*(\S+)')
DIMODE = re.compile(r'di(\d|_|/|$)')
CARRY = re.compile(r'carry|ltu|geu')
OPERANDS = re.compile(r'^\t[a-z0-9]+\.\s+([^\s#]+)')


def low_bits_only(mnemonic, line):
    """Whether the result has no bit set above bit 30, so CR0 from the low
    word equals CR0 from all 64 bits: andi. (a 16-bit mask), andis. with a
    mask below 0x8000, rldicl. keeping bits 33-63. GCC uses these for most
    64-bit bit tests; leaving them alone keeps functions within branch range
    (a compare after each pushed one past +-32 KB)."""
    ops = OPERANDS.match(line).group(1).split(',')
    try:
        if mnemonic == 'andi':
            return True
        if mnemonic == 'andis':
            return int(ops[2], 0) < 0x8000
        if mnemonic == 'rldicl':
            return int(ops[3], 0) >= 33
    except (IndexError, ValueError):
        pass
    return False


def main(argv):
    cmd = argv[1:]
    if '-c' not in cmd or '-o' not in cmd:
        return subprocess.call(cmd)
    src = cmd[cmd.index('-c') + 1]
    if not src.endswith(('.c', '.cc', '.cpp', '.cxx')):
        return subprocess.call(cmd)
    obj = cmd[cmd.index('-o') + 1]
    asm = obj + '.s'
    compile_cmd = list(cmd)
    compile_cmd[compile_cmd.index('-c')] = '-S'
    compile_cmd[compile_cmd.index('-o') + 1] = asm
    compile_cmd.insert(1, '-dp')
    rc = subprocess.call(compile_cmd)
    if rc:
        return rc

    fixed, carries, out = {}, {}, []
    with open(asm, 'r', encoding='utf-8', errors='surrogateescape') as f:
        for line in f:
            out.append(line)
            m = RECORD.match(line)
            if m and m.group(1)[0] not in 'fvx' and m.group(1) not in ('stwcx', 'stdcx', 'stbcx', 'sthcx'):
                name = m.group(3)
                if DIMODE.search(name.split('/')[0]) and not low_bits_only(m.group(1), line):
                    out.append('\tcmpdi 0,%s,0\t # record_forms.py: CR0 from all 64 bits\n' % m.group(2))
                    fixed[name] = fixed.get(name, 0) + 1
                continue
            m = INSN.match(line)
            if m:
                name = m.group(1).split('/')[0]
                if CARRY.search(name) and DIMODE.search(name):
                    carries[name] = carries.get(name, 0) + 1
    with open(asm, 'w', encoding='utf-8', errors='surrogateescape') as f:
        f.writelines(out)

    asm_cmd = [cmd[0]] + [a for a in cmd[1:] if a.startswith('-m') or a.startswith('-Wa,')] + ['-c', asm, '-o', obj]
    rc = subprocess.call(asm_cmd)
    if os.environ.get('CH360_KEEP_ASM') != '1':
        os.remove(asm)
    if fixed or carries:
        what = ', '.join('%s %d' % kv for kv in sorted(fixed.items()))
        line = 'record_forms: %s: %d fixed (%s)' % (os.path.basename(src), sum(fixed.values()), what)
        if carries:
            line += '; 64-bit carry patterns left: ' + ', '.join('%s %d' % kv for kv in sorted(carries.items()))
        print(line, flush=True)
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv))
