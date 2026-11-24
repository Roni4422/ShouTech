#!/usr/bin/env python3
"""DevMode fence verification (licensing build, design §7 step 2).

Fails (exit 1) when ANY of these hold:
  1. A shipped configuration file enables Licensing.DevMode
     (cfg/appset.json in the repo, and any appset*.json under cfg/).
  2. The DevMode grant in Services/Entitlements.cs is not fenced
     inside an `#if DEBUG` block (the grant must be impossible in
     Release builds even if the config flag is edited in the field).
  3. AppConfig's DevMode default is not false-equivalent (the
     property must carry no `= true` initializer).

Run from the repository root (ShouTech.ERP.Com/) or pass the root
as argv[1]. Exit 0 = fence intact.
"""

import json
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
failures: list[str] = []

# 1. Shipped configs must not enable DevMode.
cfg_dir = root / "cfg"
candidates = sorted(cfg_dir.glob("appset*.json")) if cfg_dir.is_dir() else []
if not candidates:
    failures.append("no shipped cfg/appset*.json found to verify")
for path in candidates:
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception as ex:  # noqa: BLE001 - report and fail closed
        failures.append(f"{path}: unparseable config ({ex})")
        continue
    licensing = data.get("Licensing") or {}
    if licensing.get("DevMode") is True:
        failures.append(f"{path}: Licensing.DevMode is true in a shipped config")

# 2. The grant must sit inside `#if DEBUG`.
entitlements = root / "cod" / "app" / "src" / "Services" / "Entitlements.cs"
if not entitlements.is_file():
    failures.append(f"{entitlements}: not found")
else:
    text = entitlements.read_text(encoding="utf-8-sig")
    grant = text.find("DevMode license grant ACTIVE")
    if grant < 0:
        failures.append("Entitlements.cs: DevMode grant marker not found (provider changed?)")
    else:
        before = text[:grant]
        last_if = before.rfind("#if DEBUG")
        last_endif = before.rfind("#endif")
        if last_if < 0 or last_endif > last_if:
            failures.append("Entitlements.cs: DevMode grant is NOT inside an #if DEBUG block")

# 3. Config default must not enable it.
appconfig = root / "cod" / "app" / "src" / "Configuration" / "AppConfig.cs"
if appconfig.is_file():
    text = appconfig.read_text(encoding="utf-8-sig")
    if re.search(r"bool\s+DevMode\s*{\s*get;\s*set;\s*}\s*=\s*true", text):
        failures.append("AppConfig.cs: LicensingSettings.DevMode defaults to true")

if failures:
    print("DEVMODE FENCE: FAIL")
    for failure in failures:
        print(f"  - {failure}")
    sys.exit(1)

print("DEVMODE FENCE: OK (shipped configs clean, grant fenced to Debug builds, default off)")
