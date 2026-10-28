#!/usr/bin/env python3
"""Audit schema ownership, duplicate table definitions, and runtime DDL."""
from __future__ import annotations

import argparse
import re
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DDL_PATTERNS = {
    "CREATE TABLE": re.compile(r"\bCREATE\s+(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\[?([\w.]+)\]?)", re.I),
    "ALTER TABLE": re.compile(r"\bALTER\s+TABLE\s+(?:ONLY\s+)?(?:IF\s+EXISTS\s+)?(?:\[?([\w.]+)\]?)", re.I),
    "CREATE INDEX": re.compile(r"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b", re.I),
    "CREATE TRIGGER": re.compile(r"\bCREATE\s+(?:OR\s+REPLACE\s+)?TRIGGER\b", re.I),
    "CREATE FUNCTION": re.compile(r"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\b", re.I),
    "CREATE VIEW": re.compile(r"\bCREATE\s+(?:OR\s+REPLACE\s+)?VIEW\b", re.I),
    "DROP TABLE": re.compile(r"\bDROP\s+TABLE\b", re.I),
}
ARABIC = re.compile(r"[\u0600-\u06ff]")
SOURCE_SUFFIXES = {".sql", ".cs", ".xaml", ".json", ".xml", ".ps1"}
ARCHIVE_PREFIXES = ("archive/",)
TEST_PREFIXES = ("tests/",)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, help="Optional Markdown report path")
    parser.add_argument("--strict", action="store_true",
                        help="Exit non-zero when application service code contains schema DDL")
    args = parser.parse_args()
    root = args.root.resolve()
    definitions: dict[str, list[str]] = defaultdict(list)
    ddl_hits: list[tuple[str, str, int, str]] = []
    arabic_hits: list[str] = []

    for path in root.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in SOURCE_SUFFIXES:
            continue
        try:
            content = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        relative = path.relative_to(root).as_posix()
        archived = relative.startswith(ARCHIVE_PREFIXES)
        test_file = relative.startswith(TEST_PREFIXES)
        for kind, pattern in DDL_PATTERNS.items():
            for match in pattern.finditer(content):
                line = content.count("\n", 0, match.start()) + 1
                subject = match.group(1) if match.lastindex else ""
                ddl_hits.append((relative, kind, line, subject or ""))
                if kind == "CREATE TABLE" and path.suffix.lower() == ".sql":
                    definitions[(subject or "(unparsed)").lower()].append(f"{relative}:{line}")
        if ARABIC.search(content) and "Resources/Lang/" not in relative:
            for number, line in enumerate(content.splitlines(), 1):
                if ARABIC.search(line):
                    arabic_hits.append(f"{relative}:{number}")

    duplicates = {name: places for name, places in sorted(definitions.items()) if len(places) > 1}
    active_service_ddl = [
        hit for hit in ddl_hits
        if hit[0].startswith("cod/app/src/Services/")
    ]
    non_dba_sql = [
        hit for hit in ddl_hits
        if hit[0].endswith(".sql")
        and not hit[0].startswith("dba/")
        and not hit[0].startswith(ARCHIVE_PREFIXES)
        and not hit[0].startswith(TEST_PREFIXES)
    ]
    lines = [
        "# Schema Audit", "",
        f"- SQL CREATE TABLE definitions found: {sum(map(len, definitions.values()))}",
        f"- Table names defined more than once: {len(duplicates)}",
        f"- Schema DDL matches in application services: {len(active_service_ddl)}",
        f"- Schema DDL matches in non-archive SQL outside dba/: {len(non_dba_sql)}",
        f"- Files/lines containing Arabic outside language resources: {len(arabic_hits)}",
        "", "## Duplicate SQL table definitions", ""
    ]
    if duplicates:
        for table, places in duplicates.items():
            lines.append(f"- **{table}**: " + ", ".join(places))
    else:
        lines.append("- None found.")

    lines += ["", "## DDL in application services", ""]
    if active_service_ddl:
        for path, kind, line, subject in active_service_ddl:
            suffix = f" ({subject})" if subject else ""
            lines.append(f"- `{path}:{line}` — {kind}{suffix}")
    else:
        lines.append("- None found.")

    lines += ["", "## DDL in non-archive SQL outside dba/", ""]
    if non_dba_sql:
        for path, kind, line, subject in non_dba_sql:
            suffix = f" ({subject})" if subject else ""
            lines.append(f"- `{path}:{line}` — {kind}{suffix}")
    else:
        lines.append("- None found.")

    lines += ["", "## DDL inventory (including archives and tests)", ""]
    for path, kind, line, subject in sorted(ddl_hits):
        zone = "archive" if path.startswith(ARCHIVE_PREFIXES) else "test" if path.startswith(TEST_PREFIXES) else "active"
        suffix = f" ({subject})" if subject else ""
        lines.append(f"- [{zone}] `{path}:{line}` — {kind}{suffix}")

    lines += ["", "## Arabic content outside language resources", ""]
    lines.extend([f"- {x}" for x in arabic_hits] if arabic_hits else ["- None found."])
    report = "\n".join(lines) + "\n"
    if args.output:
        out = args.output if args.output.is_absolute() else root / args.output
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(report, encoding="utf-8")
        print(f"Wrote {out}")
    else:
        print(report)
    if args.strict and active_service_ddl:
        print("ERROR: schema DDL is present in application services; move it to dba migrations.")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
