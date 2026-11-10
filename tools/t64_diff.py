#!/usr/bin/env python3
"""t64_diff.py: the console's 64-bit test build's results against the PC's.

    t64_diff.py CONSOLE_LOG PC_LOG

CONSOLE_LOG is the test build's cuphead360_test*.txt; PC_LOG the same build's player run on the PC
(host_native ... -- run --console 1 ... --test64 1, with GameData/fonts/arial.ttf where the console has it).
Test64.cs prints its checks (T64 ok/FAIL) and probes (T64 probe NAME = result) the same way on both, so a line
that differs names code the console runs wrong. Prints the probes and checks that differ, those missing on one
side, and the last T64 step and any CRASH line of each log.
"""
import re
import sys

LINE = re.compile(r"^(T64 (?:ok|FAIL) +|T64 probe )(.+?)( = | threw )(.*)$")


def read(path):
    results, steps, crash = {}, [], []
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\r\n")
        m = LINE.match(line)
        if m:
            results[m.group(2)] = m.group(3).strip() + " " + m.group(4)
        elif line.startswith("T64 step") or line.startswith("T64 done") or line.startswith("T64 fonts"):
            steps.append(line)
        elif line.startswith("CRASH") or line.startswith("Process terminated") or "Unhandled exception" in line:
            crash.append(line)
    return results, steps, crash


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    console, csteps, ccrash = read(sys.argv[1])
    pc, psteps, pcrash = read(sys.argv[2])
    differ = [k for k in pc if k in console and console[k] != pc[k]]
    print("%d results on the console, %d on the PC; %d differ, %d only on the PC, %d only on the console"
          % (len(console), len(pc), len(differ), len([k for k in pc if k not in console]), len([k for k in console if k not in pc])))
    for k in differ:
        print("DIFFERS %s\n  console %s\n  pc      %s" % (k, console[k], pc[k]))
    for k in pc:
        if k not in console:
            print("ONLY ON THE PC %s: %s" % (k, pc[k]))
    for name, steps, crash in (("console", csteps, ccrash), ("pc", psteps, pcrash)):
        print("%s: last step %s%s" % (name, steps[-1] if steps else "(none)", "; " + " | ".join(crash) if crash else ""))
    return 1 if differ else 0


if __name__ == "__main__":
    sys.exit(main())
