#!/usr/bin/env python3
"""Audit source files for Arabic text outside approved localization resources.

The Arabic translation catalogs under cod/app/src/Resources/Lang and their
nested localization catalogs are explicitly allowed. All other text files are
expected to be English-only once the cleanup is complete.
"""
from __future__ import annotations
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARABIC = re.compile(r"[\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff]")
ALLOWED_PARTS = {
    Path("cod/app/src/Resources/Lang"),
    Path("cod/app/src/Resources/COA/Lang"),
}
SKIP_DIRS = {".git", "bin", "obj", "node_modules", "packages", "TestResults"}
TEXT_EXTS = {
    ".cs", ".xaml", ".xml", ".json", ".sql", ".md", ".txt", ".ps1",
    ".sh", ".py", ".yml", ".yaml", ".toml", ".config", ".csproj", ".props",
    ".targets", ".html", ".css", ".js", ".ts", ".csv", ".ini", ".resx",
}

def allowed_localization(path: Path) -> bool:
    rel = path.relative_to(ROOT)
    return any(rel == base or base in rel.parents for base in ALLOWED_PARTS)

def main() -> int:
    hits: list[tuple[str, int, str]] = []
    scanned = 0
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in TEXT_EXTS:
            continue
        if any(part in SKIP_DIRS for part in path.parts):
            continue
        if allowed_localization(path):
            continue
        try:
            content = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        scanned += 1
        for line_no, line in enumerate(content.splitlines(), 1):
            if ARABIC.search(line):
                sample = line.strip().replace("\t", " ")
                if len(sample) > 180:
                    sample = sample[:177] + "..."
                hits.append((str(path.relative_to(ROOT)), line_no, sample))
    print(f"Scanned text files: {scanned}")
    print(f"Arabic-containing lines outside approved localization catalogs: {len(hits)}")
    for file, line, sample in hits:
        print(f"{file}:{line}: {sample}")

    # Validate the shape of all localization catalogs and report missing keys.
    lang_root = ROOT / "cod/app/src/Resources/Lang"
    catalogs: dict[str, dict] = {}
    for path in sorted(lang_root.glob("*.json")):
        try:
            catalogs[path.stem] = json.loads(path.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"INVALID LANGUAGE JSON: {path.relative_to(ROOT)}: {exc}")
            return 2
    def flatten(obj, prefix=""):
        out = {}
        for key, value in obj.items():
            full = f"{prefix}.{key}" if prefix else key
            if isinstance(value, dict): out.update(flatten(value, full))
            else: out[full] = value
        return out
    if "En" not in catalogs:
        print("Missing required English catalog: En.json")
        return 2
    en_keys = set(flatten(catalogs["En"]))
    for lang, catalog in catalogs.items():
        keys = set(flatten(catalog))
        missing = sorted(en_keys - keys)
        extra = sorted(keys - en_keys)
        print(f"Catalog {lang}: {len(keys)} keys; missing={len(missing)}; extra={len(extra)}")
        if missing:
            print("  Missing examples: " + ", ".join(missing[:12]))
        if extra:
            print("  Extra examples: " + ", ".join(extra[:12]))
    return 1 if hits else 0

if __name__ == "__main__":
    raise SystemExit(main())
