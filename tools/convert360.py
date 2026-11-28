#!/usr/bin/env python3
"""360convert: turns your own copy of Cuphead (Steam) into the game data Cuphead360's Xbox 360 build reads.

    360convert.exe                      (or: python tools/convert360.py)
    360convert.exe --game "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Cuphead" [--workers N] [--work <folder>] [--keep-work]

It uses (or makes) the folder "cuphead360" in the current folder, asks for your Cuphead.exe in a file dialog (or takes
--game), converts the game's objects, textures and sounds and stages them in cuphead360\\GameData: the texture and
sound packs, the converted objects, the read-ahead lists and the fonts. Your own build of default.xex goes beside them
(docs/BUILDING.md: tools/stage_console.py --build-only copies default.xex, GameData/PLAYER.DAT and licenses/).

The conversion needs about 36 GB free for its working files (deleted at the end) and about 5 GB where cuphead360 is;
it takes an hour or two, mostly the textures. The working files go in "cuphead360_work" beside cuphead360, or on the
fixed drive with the most room when that drive is short (--work sets it). An interrupted run picks up from its last
finished step when started again in the same folder. Everything it writes comes from your own game files: keep it to
your own console.

The steps are the tools docs/BUILDING.md runs one by one (convert_objects, convert_textures, convert_console,
cluster_console, simplify_meshes, scale_pages, convert_audio, stage_console); run from source, this needs the Python
packages that guide installs.
"""
import argparse
import ctypes
import multiprocessing
import os
import shutil
import sys
import time

WORK_GB = 36        # the working files at their largest: decoded textures 14, halved pages 4, full-size pages 13
OUT_GB = 5          # the staged data (the packs 3.4, objects 0.8, sounds' info, lists, fonts)
TESTED = "Cuphead 1.3.4 (build 9318602) on Steam, with or without The Delicious Last Course"


def here():
    """Where the tools and their files are: the exe's unpacked bundle, or the repository."""
    if getattr(sys, "frozen", False):
        return sys._MEIPASS  # noqa: SLF001 - PyInstaller's unpacked bundle
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class Tee:
    """Everything printed also goes to the log file beside cuphead360."""
    def __init__(self, stream, log):
        self.stream, self.log = stream, log

    def write(self, s):
        self.stream.write(s)
        self.log.write(s)
        self.log.flush()

    def flush(self):
        self.stream.flush()


def memory_gb():
    if os.name != "nt":
        return 16

    class MS(ctypes.Structure):
        _fields_ = [("len", ctypes.c_ulong), ("load", ctypes.c_ulong), ("total", ctypes.c_ulonglong),
                    ("avail", ctypes.c_ulonglong), ("pt", ctypes.c_ulonglong), ("pa", ctypes.c_ulonglong),
                    ("vt", ctypes.c_ulonglong), ("va", ctypes.c_ulonglong), ("ve", ctypes.c_ulonglong)]
    m = MS()
    m.len = ctypes.sizeof(MS)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.total / 2**30


def default_workers():
    """Texture workers: a core spare for the PC, and about 3 GB of memory each (a full-size page's pixels and its
    copies), at most 6."""
    return max(1, min((os.cpu_count() or 2) - 1, int(memory_gb() // 3), 6))


def free_gb(path):
    while path and not os.path.exists(path):
        path = os.path.dirname(path)
    return shutil.disk_usage(path or ".").free / 2**30


def drive_of(path):
    return os.path.splitdrive(os.path.abspath(path))[0].upper() or "/"


def fixed_drives():
    if os.name != "nt":
        return []
    out = []
    mask = ctypes.windll.kernel32.GetLogicalDrives()
    for i in range(26):
        if mask & (1 << i):
            root = "%s:\\" % chr(65 + i)
            if ctypes.windll.kernel32.GetDriveTypeW(root) == 3:   # DRIVE_FIXED
                out.append(root)
    return out


def choose_work(out_dir, wanted):
    """The working folder: beside cuphead360 when that drive has the room, else the fixed drive with the most."""
    if wanted:
        return os.path.abspath(wanted)
    beside = os.path.join(os.path.dirname(out_dir), "cuphead360_work")
    if os.path.isdir(beside):
        return beside   # an interrupted run's: go on with it
    if free_gb(beside) >= WORK_GB + OUT_GB:   # beside cuphead360: the same drive holds both
        return beside
    best = max(fixed_drives(), key=free_gb, default=None)
    if best and drive_of(best) != drive_of(beside) and free_gb(best) >= WORK_GB:
        return os.path.join(best, "cuphead360_work")
    return beside


def ask_game():
    """Cuphead.exe, chosen in a file dialog; its folder."""
    import tkinter
    from tkinter import filedialog
    root = tkinter.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    path = filedialog.askopenfilename(
        title="Choose Cuphead.exe in your Cuphead folder (Steam: steamapps\\common\\Cuphead)",
        filetypes=[("Cuphead", "Cuphead.exe"), ("Programs", "*.exe"), ("All files", "*.*")])
    root.destroy()
    if not path:
        return None
    return path if os.path.isdir(path) else os.path.dirname(path)


def check_game(game):
    data = os.path.join(game, "Cuphead_Data")
    if not os.path.exists(os.path.join(data, "Managed", "Assembly-CSharp.dll")):
        return None, "%s doesn't look like a Cuphead folder (no Cuphead_Data\\Managed\\Assembly-CSharp.dll)" % game
    bundles = os.path.join(data, "StreamingAssets", "AssetBundles")
    dlc = os.path.isdir(bundles) and any("dlc" in f.lower() for f in os.listdir(bundles))
    return dlc, None


def run_tool(module, args):
    """A tool's main() with these arguments, as if run from the command line; its exit code."""
    import importlib
    mod = importlib.import_module(module)
    saved = sys.argv
    sys.argv = [module + ".py"] + list(args)
    try:
        code = mod.main()
    except SystemExit as e:
        code = e.code
    finally:
        sys.argv = saved
    if code is None or code == 0:
        return 0
    if isinstance(code, str):
        print(code)
        return 1
    return int(code)


def remove(path):
    if os.path.isdir(path):
        shutil.rmtree(path, ignore_errors=True)


def steps(game, work, out, workers):
    """(name, minutes, action): each a tool run or a removal of working files no later step reads."""
    w = str(workers)
    return [
        ("objects", 2, lambda: run_tool("convert_objects", ["--game", game, "--out", work])),
        ("objects in the asset bundles", 2, lambda: run_tool("convert_objects", ["--game", game, "--out", work, "--files", "none", "--bundles"])),
        ("textures", 5, lambda: run_tool("convert_textures", ["--game", game, "--out", work, "--bundles"])),
        ("texture pages", 30, lambda: run_tool("convert_console", ["--data", work, "--workers", w])),
        ("full-size texture pages", 45, lambda: run_tool("convert_console", ["--data", work, "--max-size", "8192", "--out",
                                                                             os.path.join(work, "ctex4k"), "--halved-only", "--workers", w])),
        ("decoded textures removed", 1, lambda: remove(os.path.join(work, "textures")) or 0),
        ("sprite pages", 5, lambda: run_tool("cluster_console", ["--data", work])),
        ("lighter sprite meshes", 15, lambda: run_tool("simplify_meshes", ["--data", work])),
        ("full-size pages removed", 1, lambda: remove(os.path.join(work, "ctex4k")) or 0),
        ("smaller pages for the heaviest levels", 4, lambda: run_tool("scale_pages", ["--data", work, "--workers", w])),
        ("sounds", 10, lambda: run_tool("convert_audio", ["--game", game, "--out", work, "--bundles"])),
        ("packs, staged in cuphead360", 15, lambda: run_tool("stage_console", ["--to", out, "--data", work, "--no-xex",
                                                                              "--packs", os.path.join(work, "packs"),
                                                                              "--workers", w])),
    ]


def selftest(game=None):
    """Loads what the conversion uses, without the game: the tools, every UnityPy module (some import texture codecs
    that read data files as they load: etcpak and astc_encoder ask archspec about the CPU), UnityPy's FMOD, the texture
    decoder, numpy, Pillow, LZ4, libsndfile's Vorbis encoder (a short tone to a temporary file) and the file dialog's
    tkinter. With --game, also one of the game's sounds through the sound step's own path (UnityPy and FMOD to WAV,
    then Ogg Vorbis to a temporary file)."""
    import importlib
    import pkgutil
    import tempfile
    ok = True

    def check(name, fn):
        nonlocal ok
        try:
            print("  %-28s %s" % (name, fn() or "ok"))
        except Exception as e:  # noqa: BLE001 - every failure is printed
            ok = False
            print("  %-28s FAILED: %s: %s" % (name, type(e).__name__, e))

    for m in ("convert_objects", "convert_textures", "convert_console", "cluster_console", "simplify_meshes",
              "scale_pages", "convert_audio", "stage_console", "chob"):
        check(m, lambda m=m: importlib.import_module(m) and None)

    def fmod():
        import fmod_toolkit
        import pyfmodex
        fmod_toolkit.get_pyfmodex_system_instance(1, pyfmodex.flags.INIT_FLAGS.NORMAL)
        return "FMOD loaded"

    def vorbis():
        import numpy as np
        import convert_audio
        import io
        import soundfile
        tone = (np.sin(np.arange(48000) * 2 * np.pi * 440 / 48000) * 0.3).astype(np.float32)
        wav = io.BytesIO()
        soundfile.write(wav, tone, 48000, format="WAV", subtype="PCM_16")
        path = os.path.join(tempfile.gettempdir(), "360convert_selftest.ogg")
        convert_audio.encode(wav.getvalue(), path, 5)
        frames = soundfile.info(path).frames
        os.remove(path)
        return "1 s tone encoded and read back: %d frames" % frames

    def dxt():
        import texture2ddecoder
        return "a DXT1 block decoded to %d bytes" % len(texture2ddecoder.decode_bc1(bytes(8), 4, 4))

    def lz4():
        import lz4.block
        return "%d bytes -> %d" % (65536, len(lz4.block.compress(bytes(65536), mode="high_compression", compression=9)))

    def unitypy_modules():
        import UnityPy
        n = 0
        for m in pkgutil.walk_packages(UnityPy.__path__, "UnityPy."):
            importlib.import_module(m.name)
            n += 1
        return "UnityPy %s: %d modules imported" % (UnityPy.__version__, n)

    def codecs():
        import archspec.cpu
        importlib.import_module("etcpak")
        importlib.import_module("astc_encoder")
        return "etcpak, astc_encoder (CPU: %s)" % archspec.cpu.host().name

    def game_sound():
        import UnityPy
        import convert_audio
        import soundfile
        env = UnityPy.load(os.path.join(game, "Cuphead_Data", "resources.assets"))
        clip = next(o for o in env.objects if o.type.name == "AudioClip").read()
        wav = next(iter(clip.samples.values()))
        path = os.path.join(tempfile.gettempdir(), "360convert_selftest_game.ogg")
        convert_audio.encode(wav, path, 5)
        frames = soundfile.info(path).frames
        os.remove(path)
        return "%s: %d frames decoded and encoded" % (clip.m_Name, frames)

    check("UnityPy", unitypy_modules)
    check("texture codecs", codecs)
    check("FMOD", fmod)
    if game:
        check("a game sound", game_sound)
    check("texture2ddecoder", dxt)
    check("numpy / Pillow", lambda: "numpy %s, Pillow %s" % (importlib.import_module("numpy").__version__,
                                                            importlib.import_module("PIL").__version__))
    check("LZ4", lz4)
    check("Vorbis (libsndfile)", vorbis)
    check("tkinter (the file dialog)", lambda: importlib.import_module("tkinter.filedialog") and None)
    print("selftest: %s" % ("ok" if ok else "FAILED"))
    return 0 if ok else 1


def finish(code, pause):
    if pause:
        try:
            input("\nPress Enter to close.")
        except EOFError:
            pass
    return code


def main():
    ap = argparse.ArgumentParser(prog="360convert", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", help="your Cuphead folder (the one holding Cuphead.exe); without it a dialog asks")
    ap.add_argument("--workers", type=int, default=0, help="texture worker processes (default: from the PC's cores and memory)")
    ap.add_argument("--work", help="the folder for the working files (default: cuphead360_work beside cuphead360)")
    ap.add_argument("--keep-work", action="store_true", help="keep the working files at the end")
    ap.add_argument("--no-pause", action="store_true", help="don't wait for Enter at the end")
    ap.add_argument("--licenses", action="store_true", help="print the licenses of the libraries inside 360convert.exe")
    ap.add_argument("--selftest", action="store_true",
                    help="check the libraries inside 360convert.exe load (no game needed; with --game, one of its sounds too)")
    a = ap.parse_args()
    pause = getattr(sys, "frozen", False) and not a.no_pause
    root = here()
    if a.licenses:
        p = os.path.join(root, "THIRD_PARTY_NOTICES.txt")
        print(open(p, encoding="utf-8").read() if os.path.exists(p) else "(no notices file next to the tools)")
        return 0
    # The tools find their own files (console/artscale.txt, console/learned) from here.
    os.environ["CUPHEAD360_ROOT"] = root
    sys.path.insert(0, os.path.join(root, "tools"))
    if a.selftest:
        return selftest(a.game)

    out = os.path.abspath("cuphead360")
    print("360convert: Cuphead360's game data, from your own copy of Cuphead (tested with %s)." % TESTED)
    if os.path.isdir(out):
        print("Using the folder %s" % out)
    else:
        os.makedirs(out)
        print("Made the folder %s" % out)
    log = open(os.path.join(os.path.dirname(out), "cuphead360_convert.log"), "a", encoding="utf-8")
    sys.stdout = Tee(sys.stdout, log)
    sys.stderr = Tee(sys.stderr, log)
    print("\n%s 360convert started" % time.strftime("%Y-%m-%d %H:%M:%S"))

    game = a.game
    if not game:
        print("Choose Cuphead.exe in your Cuphead folder (Steam: steamapps\\common\\Cuphead; the dialog may be behind this "
              "window)...")
        game = ask_game()
        if not game:
            print("No game folder chosen; nothing was converted.")
            return finish(1, pause)
    game = os.path.abspath(game)
    dlc, problem = check_game(game)
    if problem:
        print(problem)
        return finish(1, pause)
    print("Game: %s (%s)" % (game, "with The Delicious Last Course" if dlc else "without The Delicious Last Course"))
    if drive_of(out) == drive_of(game) and os.path.abspath(out).startswith(os.path.abspath(game) + os.sep):
        print("Run 360convert from a folder outside the game's folder: it never writes there.")
        return finish(1, pause)

    work = choose_work(out, a.work)
    if os.path.abspath(work).startswith(os.path.abspath(game) + os.sep):
        print("The working folder can't be inside the game's folder.")
        return finish(1, pause)
    os.makedirs(work, exist_ok=True)
    workers = a.workers or default_workers()
    same = drive_of(work) == drive_of(out)
    need_work = WORK_GB + (OUT_GB if same else 0)
    print("Working files: %s (%.0f GB free; up to %d GB needed, deleted at the end)" % (work, free_gb(work), WORK_GB))
    print("Output: %s (%.0f GB free; about %d GB)" % (out, free_gb(out), OUT_GB))
    print("Texture workers: %d (of %d cores, %.0f GB of memory)" % (workers, os.cpu_count() or 1, memory_gb()))
    done_dir = os.path.join(work, "done")
    os.makedirs(done_dir, exist_ok=True)
    todo = steps(game, work, out, workers)
    finished = [n for n, _, _ in todo if os.path.exists(os.path.join(done_dir, n))]
    if finished:
        print("Going on from an earlier run: %d of %d steps done." % (len(finished), len(todo)))
    elif free_gb(work) < need_work:
        print("Not enough room: the working files need about %d GB free on %s (start 360convert with --work <folder> "
              "for another drive)." % (need_work, drive_of(work)))
        return finish(1, pause)
    if not same and free_gb(out) < OUT_GB:
        print("Not enough room for the output: about %d GB free is needed on %s." % (OUT_GB, drive_of(out)))
        return finish(1, pause)

    t_all = time.time()
    for i, (name, minutes, action) in enumerate(todo):
        mark = os.path.join(done_dir, name)
        if os.path.exists(mark):
            continue
        print("\n[%d/%d] %s (about %d min)" % (i + 1, len(todo), name, minutes), flush=True)
        t0 = time.time()
        code = action()
        if code:
            print("\nThe step \"%s\" failed (code %s). The lines above say why; %s has the whole log. Start 360convert "
                  "again in this folder to go on from this step." % (name, code, log.name))
            return finish(1, pause)
        open(mark, "w").close()
        print("[%d/%d] %s: done in %.0f min" % (i + 1, len(todo), name, (time.time() - t0) / 60), flush=True)
    if not a.keep_work:
        print("\nRemoving the working files...")
        shutil.rmtree(work, ignore_errors=True)
    print("\nDone in %.0f min. %s holds the game data." % ((time.time() - t_all) / 60, out))
    print("Add your own build: default.xex in cuphead360, PLAYER.DAT in cuphead360\\GameData and the licenses folder\n"
          "(docs/BUILDING.md, \"Put it on the USB drive\"), then copy cuphead360 to the drive's Apps folder.")
    return finish(0, pause)


if __name__ == "__main__":
    multiprocessing.freeze_support()
    sys.exit(main())
