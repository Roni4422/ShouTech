#!/usr/bin/env python3
"""
ShouTech ERP - Step 12 screen-fit verification harness.

The owner's rule: every child window opens at <= 50% of the work area.
At 1366x768 that cap is 684x384, so no window may declare a minimum
larger than that (a minimum above the cap defeats the central cap:
BaseWin's clamp floors at the minimum). This harness statically
verifies, from the XAML/code in app/Views:

  1. Every window's declared MinWidth/MinHeight is <= 684x384.
     (Windows that declare no minimum cannot defeat the cap and are
     listed as compliant-by-construction; MWin, the maximized main
     window, is exempt by the owner's rule and listed separately.)
  2. BinWin derives from BaseWin (XAML root + code-behind base class)
     and keeps its SYS.ADMIN purge gate.
  3. The three sweep clip regions are structurally fixed:
     - InvoiceWin: header and bottom bands live in ScrollViewers and
       the old fixed 30%/40%/30% root bands are gone;
     - ProjWin: the task editor lives in a ScrollViewer and the old
       fixed 170-DIP task row is gone;
     - AccCard: the bottom bar is a WrapPanel and the card is 700 wide
       (the 3-pair header strip no longer squeezes fields to ~10 DIP);
     - PosWin: minimum is 660x360 and the summary rail is scrollable.

Usage:
    python3 tools/verify_screenfit.py [--base <git-rev>]

With --base, a before/after inventory is printed (before = the tree
at that revision, read via `git show`). Exit code is non-zero when
any assertion fails. WPF rendering at small sizes still owes a
Windows runtime pass; this harness proves the static contract only.
"""

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VIEWS = ROOT / "app" / "Views"
CAP_W, CAP_H = 684, 384

SEQ_RE = re.compile(
    r"Width\s*=\s*(\d+)\s*[;,]\s*Height\s*=\s*(\d+)\s*[;,]\s*"
    r"MinWidth\s*=\s*(\d+)\s*[;,]\s*MinHeight\s*=\s*(\d+)",
    re.S)
CLASS_RE = re.compile(r"class\s+(\w+)\s*:\s*([\w\.]+)")


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="ignore")


_TOPLEVEL = None


def git_show(rev: str, path: Path) -> str | None:
    global _TOPLEVEL
    if _TOPLEVEL is None:
        top = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--show-toplevel"],
            capture_output=True, text=True)
        _TOPLEVEL = Path(top.stdout.strip())
    rel = path.relative_to(_TOPLEVEL).as_posix()
    out = subprocess.run(
        ["git", "-C", str(ROOT), "show", f"{rev}:{rel}"],
        capture_output=True, text=True)
    return out.stdout if out.returncode == 0 else None


def xaml_minimums(text: str):
    """(name, minw, minh) for a XAML window root, or None."""
    m = re.search(r"<(\w+:)?(\w+)\s[^>]*?x:Class=\"[\w\.]*?(\w+)\"", text, re.S)
    if not m:
        return None
    root_tag = text[: text.index(">", m.start()) + 1]
    if "UserControl" in root_tag:
        return None
    mw = re.search(r'MinWidth="(\d+)"', root_tag)
    mh = re.search(r'MinHeight="(\d+)"', root_tag)
    if not mw and not mh:
        return (m.group(3), None, None)
    return (m.group(3),
            int(mw.group(1)) if mw else None,
            int(mh.group(1)) if mh else None)


def code_minimums(text: str):
    """All (minw, minh) ctor/initializer size sequences in a .cs file."""
    return [(int(m.group(3)), int(m.group(4))) for m in SEQ_RE.finditer(text)]


def inventory(source):
    """source: callable(Path) -> str|None. Returns rows + nomins."""
    rows, nomins = {}, []
    for f in sorted(VIEWS.glob("*.xaml")):
        text = source(f)
        if text is None:
            continue
        got = xaml_minimums(text)
        if got:
            name, mw, mh = got
            if mw is None and mh is None:
                nomins.append(name)
            else:
                rows[name] = (mw, mh)
    for f in sorted(VIEWS.glob("*.cs")):
        text = source(f)
        if text is None:
            continue
        seqs = code_minimums(text)
        if not seqs:
            continue
        classes = [m.group(1) for m in CLASS_RE.finditer(text)
                   if any(k in m.group(2) for k in ("BaseWin", "Window", "RptBase"))]
        primary = classes[0] if classes else f.stem
        # A .xaml.cs file's own window minimums live in its XAML; any
        # full size sequence in the code-behind belongs to a nested
        # dialog the window constructs (WhsWin's StockCountDialog /
        # MatMergeWin pattern).
        is_code_behind = f.name.endswith(".xaml.cs")
        for i, (mw, mh) in enumerate(seqs):
            if is_code_behind:
                name = f"{primary}/dialog{i + 1}"
            else:
                name = primary if i == 0 else f"{primary}/dialog{i}"
            rows[name] = (mw, mh)
    return rows, nomins


def main():
    base = None
    if "--base" in sys.argv:
        base = sys.argv[sys.argv.index("--base") + 1]

    failures = []
    current, nomins = inventory(read)

    before = {}
    if base:
        before, _ = inventory(lambda p: git_show(base, p))

    print(f"{'Window':28s} {'Before':>11s} {'After':>11s}  Verdict")
    for name in sorted(set(current) | set(before)):
        mw, mh = current.get(name, (None, None))
        b = before.get(name)
        bs = f"{b[0]}x{b[1]}" if b else "-"
        as_ = f"{mw}x{mh}" if mw is not None else "none"
        ok = (mw is None) or (mw <= CAP_W and mh <= CAP_H)
        if name == "MWin":
            verdict = "EXEMPT (main window, maximized by rule)"
        elif ok:
            verdict = "PASS"
        else:
            verdict = "FAIL"
            failures.append(f"{name}: minimum {mw}x{mh} exceeds cap {CAP_W}x{CAP_H}")
        print(f"{name:28s} {bs:>11s} {as_:>11s}  {verdict}")
    print(f"\nNo declared minimum (cannot defeat the cap): "
          f"{', '.join(sorted(nomins)) or '-'}")

    # --- BinWin under BaseWin -------------------------------------------
    bx = read(VIEWS / "BinWin.xaml")
    bc = read(VIEWS / "BinWin.xaml.cs")
    if not bx.lstrip("\ufeff").startswith("<local:BaseWin"):
        failures.append("BinWin.xaml root is not <local:BaseWin>")
    if "class BinWin : BaseWin" not in bc:
        failures.append("BinWin.xaml.cs does not derive from BaseWin")
    if "PermCat.System.Admin" not in bc:
        failures.append("BinWin lost its SYS.ADMIN purge gate")
    print("\nBinWin: root=<local:BaseWin>, base=BaseWin, SYS.ADMIN purge gate present"
          if not any("BinWin" in f for f in failures) else "\nBinWin: FAIL")

    # --- Clip-region structure ------------------------------------------
    inv = read(VIEWS / "InvoiceWin.cs")
    if "ScrollViewer { Content = BuildHeaderPanel()" not in inv:
        failures.append("InvoiceWin header band is not in a ScrollViewer")
    if "ScrollViewer { Content = bottom" not in inv:
        failures.append("InvoiceWin bottom band is not in a ScrollViewer")
    if "top 30%" in inv or "GridLength(4, GridUnitType.Star)" in inv:
        failures.append("InvoiceWin still has the fixed 30/40/30 root bands")

    proj = read(VIEWS / "ProjWin.cs")
    if "taskEditorScroll" not in proj or "ScrollViewer { Content = taskEditor" not in proj:
        failures.append("ProjWin task editor is not in a ScrollViewer")
    if "GridLength(170)" in proj:
        failures.append("ProjWin still has the fixed 170-DIP task row")

    acc = read(VIEWS / "AccCard.xaml")
    if '<WrapPanel Grid.Row="3"' not in acc:
        failures.append("AccCard bottom bar is not a WrapPanel")
    if 'Width="700"' not in acc:
        failures.append("AccCard was not widened to 700")

    pos = read(VIEWS / "PosWin.cs")
    if "MinWidth = 660; MinHeight = 360" not in pos:
        failures.append("PosWin minimum is not 660x360")
    if "ScrollViewer { Content = summaryLayout" not in pos:
        failures.append("PosWin summary rail is not scrollable")

    print("Clip regions: InvoiceWin bands scrollable, ProjWin task editor "
          "scrollable, AccCard bar wraps, PosWin rail scrollable"
          if not any(k in f for f in failures
                     for k in ("InvoiceWin", "ProjWin", "AccCard", "PosWin"))
          else "Clip regions: FAIL")

    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print(f"\nALL PASS: every declared minimum <= {CAP_W}x{CAP_H} "
          f"(or exempt/no-minimum), BinWin under BaseWin, clip regions fixed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
