#!/usr/bin/env python3
"""Build, verify and append ShouTech's compact, checksum-preserving SQL migration bundles."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
import tempfile
import io
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROVIDERS = ("sqlite", "postgresql")
# Bundle membership is functional; the migration reader sorts original IDs globally.
BUNDLES = ("sys", "card", "inv", "acc", "vch", "pur", "sal_crm", "mfg", "hr", "pos_tax")
MARKER = re.compile(
    rb"^-- @shoutech-migration (?P<id>[A-Za-z0-9_.-]+) "
    rb"(?P<windows>[0-9A-Fa-f]{64}) (?P<linux>[0-9A-Fa-f]{64}) "
    rb"(?P<raw>[0-9A-Fa-f]{64}) (?P<content>[0-9A-Fa-f]{64})[ \t]*\r?$",
    re.MULTILINE,
)

EXACT = {
    "000_history.sql": "sys",
    "001_core.sql": "sys",
    "001_sqlite_core.sql": "sys",
    "002_security.sql": "sys",
    "002_rbac.sql": "sys",
    "003_modules.sql": "sys",
    "003_rbac.sql": "sys",
    "003_roles.sql": "sys",
    "004_modules.sql": "sys",
    "004_permissions.sql": "sys",
    "005_roles.sql": "sys",
    "006_permissions.sql": "sys",
    "006_outbound_messages.sql": "sys",
    "008_outbound_messages.sql": "sys",
    "014_core_runtime_schema.sql": "card",
    "017_catalogs.sql": "card",
    "018_material_group_items.sql": "card",
    "018_perm_ext.sql": "sys",
    "030_english_only_seed_cleanup.sql": "acc",
    "036_definitions.sql": "sys",
    "037_operations.sql": "pos_tax",
    "050_card_manufacturing_persistence.sql": "card",
    "051_companies_profile_columns.sql": "sys",
    "061_party_unification.sql": "acc",
    "095_party_structure.sql": "card",
    "103_sre_receipts_audit_recon.sql": "vch",
    "110_supplier_advances.sql": "vch",
    "115_report_designer.sql": "sys",
    "116_doc_designs.sql": "vch",
    "117_label_designs.sql": "card",
    "118_account_card_fields.sql": "acc",
    "122_recycle_bin_completion.sql": "sys",
    "125_print_settings_logo.sql": "vch",
    "130_maintenance_reports.sql": "sys",
}


def classify(filename: str) -> str:
    name = filename.lower()
    if name in EXACT:
        return EXACT[name]
    if any(token in name for token in ("tax", "withholding", "pos_")):
        return "pos_tax"
    if "hr_" in name:
        return "hr"
    if "card_manufacturing" in name:
        return "card"
    if any(token in name for token in ("manufacturing", "mfg", "quality")):
        return "mfg"
    if any(token in name for token in (
        "purchase", "supplier_invoice_reference", "rfq", "p2p", "prq",
        "landed_cost", "lcsa", "lcbf", "prt_match",
    )):
        return "pur"
    if any(token in name for token in (
        "sales", "crm", "warranty", "rma", "commission", "pricing",
        "contract", "recurring", "srt_", "delivery_fulfilment",
    )):
        return "sal_crm"
    if any(token in name for token in (
        "inventory", "stock", "warehouse", "batch_serial", "valuation",
        "smdl", "sml", "osml",
    )):
        return "inv"
    if any(token in name for token in (
        "journal", "accounting", "account", "coa", "budget", "fiscal_period",
        "base_currency", "posting_completeness", "document_gl_posting",
        "projects", "fixed_assets",
    )):
        return "acc"
    if any(token in name for token in (
        "cheque", "bank_reconciliation", "bankrec", "payment_allocation",
        "supplier_advances", "sre_receipts", "document_links",
        "document_number_sequences", "number_series", "commercial_documents",
        "invoice_header_fields", "invoice_line_notes", "document_order_fields",
        "document_target_warehouse",
    )):
        return "vch"
    if any(token in name for token in (
        "material", "master_cards", "catalogs", "product_category",
        "party_structure", "uom_names", "barcode_template", "label_designs",
    )):
        return "card"
    if any(token in name for token in (
        "permission", "role", "security", "core", "modules", "outbound",
        "deletion_audit", "company_profile", "recycle_bin", "maintenance",
        "ribbon",
    )):
        return "sys"
    raise ValueError(f"Unclassified migration: {filename}")


def source_body(raw: bytes) -> bytes:
    body = raw.decode("utf-8-sig").encode("utf-8")
    return body if body.endswith(b"\n") else body + b"\n"


def normalize_lf(value: bytes) -> bytes:
    return value.replace(b"\r\n", b"\n").replace(b"\r", b"\n")


def legacy_hashes(raw: bytes) -> tuple[str, str, str]:
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    lf = normalize_lf(text.encode("utf-8"))
    prefix = b"\xef\xbb\xbf" if has_bom else b""
    linux_hash = hashlib.sha256(prefix + lf).hexdigest().upper()
    windows_hash = hashlib.sha256(prefix + lf.replace(b"\n", b"\r\n")).hexdigest().upper()
    raw_hash = hashlib.sha256(raw).hexdigest().upper()
    return windows_hash, linux_hash, raw_hash


def segment_bytes(raw: bytes, migration_id: str) -> bytes:
    body = source_body(raw)
    windows_hash, linux_hash, raw_hash = legacy_hashes(raw)
    content_hash = hashlib.sha256(normalize_lf(body)).hexdigest().upper()
    marker = (
        f"-- @shoutech-migration {migration_id} {windows_hash} {linux_hash} {raw_hash} {content_hash}\n"
    ).encode("ascii")
    return marker + body


def parse_bundle(path: Path, content: bytes) -> list[tuple[str, str]]:
    matches = list(MARKER.finditer(content))
    if not matches:
        raise ValueError(f"Bundle has no migration markers: {path}")
    if content[:matches[0].start()].strip():
        raise ValueError(f"Unexpected content before first marker: {path}")

    migrations: list[tuple[str, str]] = []
    for index, match in enumerate(matches):
        start = match.end()
        if content.startswith(b"\r\n", start):
            start += 2
        elif content.startswith(b"\n", start):
            start += 1
        end = matches[index + 1].start() if index + 1 < len(matches) else len(content)
        segment = content[start:end]
        expected = match.group("content").decode("ascii").upper()
        actual = hashlib.sha256(normalize_lf(segment)).hexdigest().upper()
        if actual != expected:
            migration_id = match.group("id").decode("ascii")
            raise ValueError(f"Segment checksum mismatch: {migration_id} in {path}")
        migrations.append((
            match.group("id").decode("ascii"),
            match.group("windows").decode("ascii").upper(),
            match.group("linux").decode("ascii").upper(),
            match.group("raw").decode("ascii").upper(),
        ))
    return migrations


def migration_directory(provider: str) -> Path:
    return ROOT / "dba" / "mig" / provider


def verify_provider(provider: str) -> tuple[int, dict[str, int]]:
    directory = migration_directory(provider)
    files = sorted(directory.glob("*.sql"), key=lambda path: path.name.casefold())
    expected_names = {f"{name}.sql" for name in BUNDLES}

    if {path.name for path in files} == expected_names:
        migrations: list[tuple[str, str]] = []
        counts: dict[str, int] = {}
        for path in files:
            rows = parse_bundle(path, path.read_bytes())
            counts[path.stem] = len(rows)
            migrations.extend(rows)
        ids = [migration[0].casefold() for migration in migrations]
        if len(ids) != len(set(ids)):
            raise ValueError(f"Duplicate migration IDs in {provider} bundles")
        return len(migrations), counts

    # Before consolidation, verify that every legacy migration has a known domain
    # and that the IDs are unique. This makes the same check useful before and after.
    migrations = []
    counts = {name: 0 for name in BUNDLES}
    for path in files:
        if MARKER.search(path.read_bytes()):
            raise ValueError(f"Mixed legacy and bundled migration formats in {directory}")
        bundle = classify(path.name)
        counts[bundle] += 1
        migrations.append(path.name.casefold())
    if len(migrations) != len(set(migrations)):
        raise ValueError(f"Duplicate migration IDs in legacy {provider} migrations")
    return len(migrations), counts


def apply_bundles(source_ref: str | None = None) -> None:
    generated: dict[str, dict[str, bytes]] = {}
    with tempfile.TemporaryDirectory() as temporary:
        source_root = ROOT / "dba" / "mig"
        if source_ref:
            archive = subprocess.run(
                ["git", "archive", "--format=tar", source_ref, "dba/mig/sqlite", "dba/mig/postgresql"],
                check=True,
                stdout=subprocess.PIPE,
            ).stdout
            with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as tar:
                for member in tar.getmembers():
                    relative = Path(member.name)
                    if not member.isfile() or relative.is_absolute() or ".." in relative.parts:
                        continue
                    destination = Path(temporary) / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    extracted = tar.extractfile(member)
                    if extracted is None:
                        raise ValueError(f"Could not read archived migration: {member.name}")
                    destination.write_bytes(extracted.read())
            source_root = Path(temporary) / "dba" / "mig"

        for provider in PROVIDERS:
            directory = source_root / provider
            sources = sorted(directory.glob("*.sql"), key=lambda path: path.name.casefold())
            if not sources:
                raise ValueError(f"No source migrations found for {provider}")
            if any(MARKER.search(path.read_bytes()) for path in sources):
                raise ValueError(f"{provider} already contains bundled migrations; refusing to rebuild")

            grouped: dict[str, list[Path]] = {name: [] for name in BUNDLES}
            for path in sources:
                grouped[classify(path.name)].append(path)

            generated[provider] = {}
            for bundle in BUNDLES:
                content = b"".join(segment_bytes(path.read_bytes(), path.name) for path in grouped[bundle])
                if not content:
                    raise ValueError(f"Empty migration bundle: {provider}/{bundle}.sql")
                generated[provider][f"{bundle}.sql"] = content

            all_ids = []
            for name, content in generated[provider].items():
                all_ids.extend(parse_bundle(directory / name, content))
            ids = [migration[0].casefold() for migration in all_ids]
            if len(ids) != len(set(ids)):
                raise ValueError(f"Duplicate IDs generated for {provider}")

        # Both providers are fully generated and verified before replacing any active files.
        for provider in PROVIDERS:
            directory = migration_directory(provider)
            for path in directory.glob("*.sql"):
                path.unlink()
            for filename, content in generated[provider].items():
                (directory / filename).write_bytes(content)


def append_migration(provider: str, bundle: str, migration_id: str, source: Path) -> None:
    directory = migration_directory(provider)
    total, _ = verify_provider(provider)
    if not (directory / f"{bundle}.sql").is_file():
        raise ValueError(f"Unknown bundle: {bundle}")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+\.sql", migration_id):
        raise ValueError("Migration ID must be a safe SQL filename")
    if not source.is_file():
        raise FileNotFoundError(source)
    try:
        source.resolve().relative_to(directory.resolve())
        raise ValueError("Append source must be outside the active migration directory")
    except ValueError as error:
        if str(error).startswith("Append source"):
            raise

    existing = []
    for path in directory.glob("*.sql"):
        existing.extend(parse_bundle(path, path.read_bytes()))
    if any(item[0].casefold() == migration_id.casefold() for item in existing):
        raise ValueError(f"Migration ID already exists: {migration_id}")

    target = directory / f"{bundle}.sql"
    with target.open("ab") as stream:
        stream.write(segment_bytes(source.read_bytes(), migration_id))
    verify_provider(provider)
    print(f"Appended {migration_id} to {provider}/{bundle}.sql ({total + 1} migrations)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("verify", "apply", "append"))
    parser.add_argument("--provider", choices=PROVIDERS)
    parser.add_argument("--bundle", choices=BUNDLES)
    parser.add_argument("--id", dest="migration_id")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--source-ref", help="Read original migrations from a Git ref before bundling")
    args = parser.parse_args()

    try:
        if args.command == "apply":
            apply_bundles(args.source_ref)
            for provider in PROVIDERS:
                total, counts = verify_provider(provider)
                print(f"{provider}: {total} migrations in {len(counts)} bundles: {counts}")
        elif args.command == "append":
            if not all((args.provider, args.bundle, args.migration_id, args.source)):
                parser.error("append requires --provider, --bundle, --id and --source")
            append_migration(args.provider, args.bundle, args.migration_id, args.source)
        else:
            for provider in PROVIDERS:
                total, counts = verify_provider(provider)
                print(f"{provider}: {total} migrations across {len(counts)} groups: {counts}")
        return 0
    except Exception as error:  # noqa: BLE001 - developer tool reports validation failures cleanly
        print(f"Migration bundle error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
