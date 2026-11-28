#!/usr/bin/env python3
"""Build 360convert.exe (tools/convert360.py and the conversion tools in one Windows program) with PyInstaller.

    python tools/build_convert360.py [--out dist]

Needs the conversion's Python packages (docs/BUILDING.md: UnityPy==1.25.4 numpy Pillow texture2ddecoder lz4 soundfile)
and pyinstaller. Bundles console/artscale.txt, the read-ahead lists (work/console/learned, the PC bot's, where there
are any, then console/learned, the console's plays, over them) and THIRD_PARTY_NOTICES.txt, written here from the
bundled packages' own license files (360convert.exe --licenses prints it; it is also written beside the exe).
Ends with the exe's --selftest (its libraries load; no game needed).
"""
import argparse
import importlib.metadata as md
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = ("convert_objects", "convert_textures", "convert_console", "cluster_console", "simplify_meshes", "scale_pages",
         "convert_audio", "stage_console", "chob", "bundle_names")
TOP = ("UnityPy", "numpy", "Pillow", "texture2ddecoder", "lz4", "soundfile")

NOTES = """360convert.exe holds Cuphead360's conversion tools (https://github.com/dougieprishpreed/cuphead360),
the Python runtime (PSF License) and the packages below, each under its own license (texts follow).

FMOD: UnityPy decodes the game's sound banks with the FMOD Engine library (fmod.dll, from the fmod_toolkit package):
FMOD Engine, copyright Firelight Technologies Pty Ltd. (https://www.fmod.com).
libsndfile (LGPL-2.1, https://libsndfile.github.io/libsndfile/), with libvorbis and libogg (BSD), encodes the sounds:
it is the separate DLL that the soundfile package carries, unpacked beside the program when it runs.
The program itself is made with PyInstaller (GPL-2.0 with its bootloader exception).
"""


def deps(names):
    """The distributions named and everything they require (their extras left out)."""
    seen, todo = {}, list(names)
    while todo:
        n = todo.pop()
        key = re.sub(r"[-_.]+", "-", n).lower()
        if key in seen:
            continue
        try:
            d = md.distribution(n)
        except md.PackageNotFoundError:
            continue
        seen[key] = d
        for r in d.requires or []:
            if "extra ==" in r:
                continue
            todo.append(re.split(r"[ ;<>=!~\[(]", r, maxsplit=1)[0])
    return seen


def notices(path):
    out = [NOTES]
    for key, d in sorted(deps(TOP).items()):
        texts = []
        for f in d.files or []:
            base = os.path.basename(str(f)).upper()
            if base.startswith(("LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS")):
                try:
                    texts.append(open(f.locate(), encoding="utf-8", errors="replace").read().strip())
                except (OSError, UnicodeDecodeError):
                    pass
        lic = d.metadata.get("License-Expression") or d.metadata.get("License") or ""
        out.append("=" * 78 + "\n%s %s%s\n%s" % (d.metadata["Name"], d.version, (" (" + lic.splitlines()[0] + ")") if lic else "",
                                                d.metadata.get("Home-page") or ""))
        out.extend(texts or ["(no license file in the package; see its project page)"])
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n\n".join(out) + "\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(ROOT, "dist"))
    a = ap.parse_args()
    build = os.path.join(ROOT, "build", "convert360")
    shutil.rmtree(build, ignore_errors=True)
    os.makedirs(os.path.join(build, "data", "learned"))
    learned = os.path.join(build, "data", "learned")
    n = {}
    for src in (os.path.join(ROOT, "work", "console", "learned"), os.path.join(ROOT, "console", "learned")):
        if os.path.isdir(src):
            for f in os.listdir(src):
                if f.endswith(".txt"):
                    shutil.copy2(os.path.join(src, f), os.path.join(learned, f))
                    n[f] = src
    print("read-ahead lists: %d scenes" % len(n))
    note = os.path.join(build, "data", "THIRD_PARTY_NOTICES.txt")
    notices(note)
    sep = os.pathsep
    cmd = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onefile", "--console", "--name", "360convert",
           "--distpath", a.out, "--workpath", os.path.join(build, "work"), "--specpath", build,
           "--paths", os.path.join(ROOT, "tools"),
           "--collect-all", "UnityPy", "--collect-all", "fmod_toolkit", "--collect-all", "pyfmodex", "--collect-all", "texture2ddecoder",
           "--collect-all", "soundfile", "--collect-all", "etcpak", "--collect-all", "tpk_ar",
           # etcpak and astc_encoder ask archspec for the CPU as UnityPy imports them (first in the sound step):
           # archspec reads its json/cpu files then
           "--collect-all", "astc_encoder", "--collect-all", "archspec",
           "--add-data", os.path.join(ROOT, "console", "artscale.txt") + sep + "console",
           "--add-data", learned + sep + os.path.join("console", "learned"),
           "--add-data", note + sep + "."]
    for t in TOOLS:
        cmd += ["--hidden-import", t]
    cmd.append(os.path.join(ROOT, "tools", "convert360.py"))
    subprocess.run(cmd, check=True)
    exe = os.path.join(a.out, "360convert.exe")
    shutil.copy2(note, os.path.join(a.out, "360convert-THIRD_PARTY_NOTICES.txt"))
    print("built %s (%.0f MB)" % (exe, os.path.getsize(exe) / 2**20))
    r = subprocess.run([exe, "--selftest", "--no-pause"], capture_output=True, text=True, timeout=600,
                       creationflags=0x08000000 if os.name == "nt" else 0)
    print(r.stdout + r.stderr)
    return r.returncode


if __name__ == "__main__":
    sys.exit(main())
