# ShouTech ERP — Complete Program Documentation

**ShouTech Enterprise Management System**

| Item | Value |
|------|-------|
| **Version** | 10.6.0 (Build 20260801) |
| **Assembly** | `ShouTech.ERP` |
| **Platform** | Windows · .NET 8 · WPF |
| **Database** | SQLite / PostgreSQL |
| **Website** | [https://www.shoutech.com](https://www.shoutech.com) |
| **Support** | support@shoutech.com |

> Arabic version: [`SHOUTECH_ERP.md`](SHOUTECH_ERP.md)

---

## 1. Overview

**ShouTech ERP** is a commercial, integrated Enterprise Resource Planning (ERP) platform designed for small and medium businesses and organizations that require:

- General Ledger and financial accounting
- Warehouses and inventory management
- Sales, purchases, and invoicing
- Point of Sale (POS)
- Customer relationship management (CRM)
- Manufacturing
- Human resources
- Reports and analytics
- Security, permissions, and audit trails

The application runs **Offline-First**: core business operations work without a network connection. Background sync and backup run when connectivity is available. Server and sync details are **not exposed** in the end-user interface.

---

## 2. Copyright and Intellectual Property

### 2.1 Copyright Notice

```
© 2026 ShouTech Software — ShouTech. All rights reserved.
© 2026 شو تيك للبرمجيات — جميع الحقوق محفوظة.
```

All intellectual property rights in the software, its user interfaces, database schemas, documentation, logos, and the trade names **"ShouTech"** and **"شو تيك"** are owned exclusively by **ShouTech Software / شو تيك للبرمجيات**.

### 2.2 Protected Materials

| Category | Examples |
|----------|----------|
| **Source code** | C#, XAML, SQL, C++, Python |
| **Databases** | `dba/` schemas, stored procedures, seed data |
| **User interfaces** | Ribbon, menus, themes, icons |
| **Documentation** | This file, `FILE_ABBR.md`, `NAV_README.md` |
| **Trademarks** | ShouTech ERP, شو تيك ERP |
| **Configuration** | `appset.json`, `UsrPref`, license files |

### 2.3 Prohibited Actions (Without Written License)

- Copying, distributing, selling, or renting the software or any part of it
- Reverse engineering, decompiling, or tampering with protection mechanisms
- Removing or disabling licensing, dongle, or integrity-check features
- Using the name or logo on competing or derivative products
- Publishing source code or database schemas to public repositories

### 2.4 Licensing

| License aspect | Description |
|----------------|-------------|
| **Type** | Per User |
| **Max Users** | 10 (configurable in `appset.json`) |
| **Features** | Core, Inventory, Sales, POS, Accounting, CRM, HR, Manufacturing, Reports, … |
| **Dongle** | Optional hardware protection (`RequireDongle`) |
| **Activation** | Online or offline (`OfflineActivationAllowed`) |

The license is a **usage agreement**, not a transfer of ownership. License expiry terminates the right to operate the software under the contract terms.

### 2.5 Disclaimer

The software is provided **"AS IS"**. ShouTech Software does not warrant that it is error-free or uninterrupted. The user is responsible for:

- Backing up their data
- Compliance with local laws (tax, accounting, privacy)
- Protecting passwords and user permissions

---

## 3. Developer / Vendor

| | |
|---|---|
| **Name (En)** | ShouTech Software |
| **Name (Ar)** | شو تيك للبرمجيات |
| **Product** | ShouTech ERP / شو تيك ERP |
| **Email** | info@shoutech.com |
| **Technical support** | support@shoutech.com |
| **Website** | https://www.shoutech.com |

---

## 4. How the Program Works — Startup Flow

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│  Launch EXE │───▶│  Language   │───▶│  SplWin     │───▶│  ComWin     │───▶│  LogWin     │
│  App.xaml   │    │  Lang       │    │  (Splash)   │    │  (Company)  │    │  (Login)    │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
                                                                                    │
                                                                                    ▼
                                                                            ┌─────────────┐
                                                                            │  MWin       │
                                                                            │  (Main)     │
                                                                            └─────────────┘
```

### 4.1 Startup Stages

1. **Mutex — single instance**  
   Prevents more than one instance on the same machine (`AllowMultipleInstances: false`).

2. **AppHost.Initialize()**  
   Loads settings, theme, and services (licensing, connectivity, backup, sync).

3. **Language selection (first run)**  
   `Lang` window — Arabic, English, French, Turkish.

4. **SplWin — splash screen**  
   Initializes offline services, progress bar, minimum display time (`MinSplashSeconds`).

5. **ComWin — company selection**  
   Multi-company support (`MultiCompany`).

6. **LogWin — login**  
   User authentication, remember last username, roles: Administrator / User / Viewer.

7. **MWin — main window**  
   Menu bar + Ribbon + work area + status bar.

### 4.2 Main Window Layout (MWin)

| Area | Purpose |
|------|---------|
| **Menu Bar** | 17 top-level menus (File, Inventory, Accounting, …) |
| **Toolbar** | Save, undo, print, search, theme, exit |
| **Ribbon** | Commands grouped by tab (215+ items in catalog) |
| **Content** | Dashboard or module workspace (`ModWsVw`, `DashVw`) |
| **Status Bar** | Status (English), company, version, user, time |

> **Note:** The status bar uses **fixed English strings** via `StatBarEn` and does not expose server or sync state.

---

## 5. Modules and Features

### 5.1 Main Ribbon Tabs (17 tabs)

| Tab | Module | Example functions |
|-----|--------|-------------------|
| 📁 File | File | Import, export, settings |
| 📦 Inventory | Inventory | Items, stock, movements, barcodes |
| 💰 Accounting | Accounting | Chart of accounts, journals, financial analysis |
| 📋 Vouchers | Vouchers | Receipt/payment vouchers |
| 🧾 Invoices | Invoices | Sales/purchase invoices, barcodes |
| 🛍️ POS | Point of Sale | Retail POS |
| 📋 Orders | Orders | Customer orders |
| 👤 Customers | Customers | CRM, customers, pricing |
| 🚶 Sales Rep | Sales Rep | Sales representatives |
| 🏗️ Manufacturing | Manufacturing | BOM, production orders |
| 🏦 Banks | Banks | Bank accounts |
| 📊 Reports | Reports | Reports and statistics |
| 🔧 Tools | Tools | Backup, options |
| ⚙️ Administration | Administration | System administration |
| 👁️ View | View | Layout and display |
| 🚪 Session | Session | Logout, shutdown |
| ❓ Help | Help | About, support |

### 5.2 Navigation Database Catalog

- **17** menus · **17** Ribbon tabs · **55** groups · **215** commands · **209** permissions  
- Schema: `dba/sys/nav.sql` — seed: `dba/sds/sds_nav.sql`  
- See: [`dba/sys/NAV_README.md`](../dba/sys/NAV_README.md)

### 5.3 Dashboard (DashVw)

- Statistics: customers, items, pending approvals  
- Default landing view after login

---

## 6. Security and Permissions

### 6.1 User Levels

| Level | Code | Permissions |
|-------|------|-------------|
| **Administrator** | `admin` | Full access — settings, security, backup |
| **User** | `user` | Daily operations — save/edit per RBAC |
| **Viewer** | `viewer` | Read-only — no save |

### 6.2 RBAC (Role-Based Access Control)

- Tables: `sec.Users`, `sec.Roles`, `sec.Permissions`  
- Ribbon commands linked to `PERM_NAV_*` in `sds_nav.sql`  
- `PermServ.HasPermission(key)` in the application layer

### 6.3 Additional Security (Configuration-Dependent)

- AES-256 encryption for sensitive data  
- Audit log — 365-day retention  
- Password policy: 8+ chars, upper/lower/numbers  
- Optional 2FA  
- Dongle / Hardware ID for licensing  
- File integrity checks (`CheckFileIntegrity`)

---

## 7. Technical Architecture

### 7.1 Project Layers

```
┌─────────────────────────────────────────────────────────────┐
│  Presentation (WPF)                                         │
│  cod/app/src — MWin, ViewModels, Themes, Lang JSON          │
├─────────────────────────────────────────────────────────────┤
│  Application Services                                       │
│  AppHost, NavServ, AuthServ, BkpServ, OnlCoord, LocSvc      │
├─────────────────────────────────────────────────────────────┤
│  Domain + Application                                       │
│  src/ShouTech.Domain · src/ShouTech.Application             │
├─────────────────────────────────────────────────────────────┤
│  Infrastructure / Core                                      │
│  cod/cor — navrep, invsvc, invrep                           │
├─────────────────────────────────────────────────────────────┤
│  Database (SQLite / PostgreSQL)                                      │
│  dba/ — sec, fin, inv, crm, hr, mfg, ai, aud, sys, mst      │
└─────────────────────────────────────────────────────────────┘
```

### 7.2 Technologies

| Layer | Technology |
|-------|------------|
| **UI** | WPF (.NET 8), Ribbon, MVVM (CommunityToolkit.Mvvm) |
| **Data access** | Microsoft.Data.Sqlite / Npgsql |
| **i18n** | JSON (Ar, En, Fr, Tr) + RTL/LTR |
| **Logging** | Serilog + files under `%LocalAppData%\ShouTech\ERP\logs` |
| **Backup** | Rotating local ZIP + optional server upload |
| **Security engine** | C++20 (PROT.dll / Dongle) — optional |
| **AI** | Local Ollama / FastAPI — optional |
| **Reports** | FastReport .NET (planned) |

### 7.3 Important Paths on the Machine

| Path | Contents |
|------|----------|
| `%LocalAppData%\ShouTech\ERP\cfg\` | `user.dat`, `license.local.json`, `device.json` |
| `%LocalAppData%\ShouTech\ERP\logs\` | `app_YYYYMMDD.log`, `crash_*.log` |
| `%LocalAppData%\ShouTech\ERP\sync\` | Sync snapshots (internal — not shown in UI) |
| `{App}\bkp\` | Rotating backups |
| `{App}\Resources\Lang\` | Ar.json, En.json, Fr.json, Tr.json |

---

## 8. Database

### 8.1 Schema Layout (`dba/`)

| Folder | Domain |
|--------|--------|
| `sec/` | Security, users, roles |
| `fin/` | Accounting, chart of accounts, invoices |
| `inv/` | Inventory, warehouses, movements |
| `crm/` | Customers, leads, pricing |
| `hr/` | Human resources |
| `mfg/` | Manufacturing |
| `ai/` | Predictions, anomalies |
| `aud/` | Audit, approvals |
| `sys/` | Settings, navigation, sequences |
| `mst/` | Master data |
| `mig/` | Version migrations |

### 8.2 Multi-Database Model

- **MST** — Master (companies, fiscal years)  
- **DB per Year** — data isolation by fiscal year  
- Connection: `ConnectionStrings.Default` in `cfg/appset.json`

---

## 9. Backup and Sync

| Feature | Behavior |
|---------|----------|
| **Local backup** | 10 FIFO copies (`bkp/backup_current.zip`) |
| **Cloud backup** | Up to 3 encrypted copies (when online) |
| **Sync** | Background queue every 3600 seconds — **hidden from UI** |
| **Startup backup** | Backup on launch when network is available |
| **Offline-First** | All core operations work without internet |

---

## 10. Languages and UI

| Language | Code | Direction |
|----------|------|-----------|
| Arabic | `ar` | RTL |
| English | `en` | LTR (default on first run) |
| French | `fr` | LTR |
| Turkish | `tr` | LTR |

- **Menus and Ribbon:** follow the selected UI language  
- **Status bar:** fixed English (`StatBarEn`)  
- **Theme:** Light / Dark — stored in `UsrPref`

---

## 11. System Requirements

| Requirement | Minimum |
|-------------|---------|
| **Operating system** | Windows 10/11 (x64) |
| **Runtime** | .NET 8 Desktop Runtime |
| **Memory** | 4 GB RAM (8 GB recommended) |
| **Database** | SQLite / PostgreSQL |
| **Disk** | 2 GB for app + DB and backup space |
| **Display** | 1280×720 (1920×1080 recommended) |

### Building the Project

```powershell
cd cod\app\src
dotnet restore
dotnet build
dotnet run
```

---

## 12. Demo Accounts (Development)

| Username | Password | Role |
|----------|----------|------|
| `admin` | `admin` | Administrator |
| `user` | `user` | User |
| `viewer` | `viewer` | Viewer (read-only) |

> **Warning:** Change passwords in production. These accounts are for development only.

---

## 13. Related Documentation

| File | Contents |
|------|----------|
| [`docs/FILE_ABBR.md`](FILE_ABBR.md) | Short ↔ long filename dictionary |
| [`dba/sys/NAV_README.md`](../dba/sys/NAV_README.md) | Navigation and Ribbon database |
| [`dba/DBChart.md`](../dba/DBChart.md) | Database folder map |
| [`README.dev.md`](../README.dev.md) | Quick developer guide |
| [`cfg/appset.json`](../cfg/appset.json) | Central configuration |

---

## 14. Version History (Summary)

| Version | Build | Notes |
|---------|-------|-------|
| **10.6.0** | 20260801 | WPF .NET 8, Ribbon DB, Offline-First, i18n×4 |
| 9.x | — | Enterprise scaffold (Domain/Application split) |

---

## 15. Final Copyright Statement

```
═══════════════════════════════════════════════════════════════════
  ShouTech ERP v10.6.0
  Copyright © 2026 ShouTech Software. All Rights Reserved.
  حقوق النشر © 2026 شو تيك للبرمجيات. جميع الحقوق محفوظة.

  This software is proprietary and confidential.
  Unauthorized copying, modification, or distribution is strictly
  prohibited and may result in civil and criminal penalties.

  هذا البرنامج ملكية خاصة وسرية.
  النسخ أو التعديل أو التوزيع غير المصرّح به ممنوع منعاً باتاً
  وقد يعرّض المخالف للمساءلة القانونية.
═══════════════════════════════════════════════════════════════════
```

---

*Last updated: August 13, 2026*
