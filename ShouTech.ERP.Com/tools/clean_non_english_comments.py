#!/usr/bin/env python3
"""Remove Arabic-only comment text from source files without touching string literals.

This is a conservative first-pass utility. It only removes whole-line comments
or standalone XML comments that contain Arabic. It intentionally leaves UI string
literals and database seed data for key-based localization/database migrations.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ARABIC = re.compile(r"[\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff]")
ALLOWED = (Path("cod/app/src/Resources/Lang"), Path("cod/app/src/Resources/COA/Lang"))
EXTS = {".cs", ".sql", ".xaml", ".xml", ".py", ".ps1", ".sh", ".js", ".ts", ".css", ".html"}
SKIP = {"bin", "obj", ".git", "node_modules", "packages"}

def is_allowed(p: Path) -> bool:
    rel = p.relative_to(ROOT)
    return any(rel == a or a in rel.parents for a in ALLOWED)

def main():
    changed = 0
    removed = 0
    for p in ROOT.rglob("*"):
        if not p.is_file() or p.suffix.lower() not in EXTS or is_allowed(p):
            continue
        if any(part in SKIP for part in p.parts):
            continue
        try: old = p.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError): continue
        new = old
        # Remove standalone XML comments containing Arabic, including multiline comments.
        def xml_sub(m):
            nonlocal removed
            if ARABIC.search(m.group(0)):
                removed += m.group(0).count("\n") + 1
                return ""
            return m.group(0)
        new = re.sub(r"<!--[\s\S]*?-->", xml_sub, new)
        out = []
        for line in new.splitlines(keepends=True):
            stripped = line.lstrip()
            comment = (
                stripped.startswith("//") or stripped.startswith("--") or
                stripped.startswith("#") or stripped.startswith("*")
            )
            if comment and ARABIC.search(line):
                removed += 1
                continue
            out.append(line)
        new = "".join(out)
        if new != old:
            p.write_text(new, encoding="utf-8", newline="")
            changed += 1
    print(f"Files changed: {changed}; comment lines/blocks removed: {removed}")

if __name__ == "__main__": main()
