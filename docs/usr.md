<!-- source: docs/usr.md -->

# Tech Stack Details

## Final Decisions
- **No EF Core** → Using Dapper for performance
- **FastReport** → Superior to RDLC
- **FastAPI** → Python AI runs on localhost:5050
- **DPAPI** → Windows encryption for keys
- **Windows Service** → SYNC runs as background service

## Performance Targets
- Invoice with 200 items: < 0.5 sec
- Annual report: < 2 sec
- Stress test: 500k invoices

---

<!-- source: docs/arc.md -->

// Path: doc\architecture.md
// Language: Markdown
// Description: Complete architectural description of the system.
======================================================================

SHOUTECH ERP V9 - Enterprise System
Tech Stack:
  - C# .NET 9 (UI + Business Logic)
  - C++ 20 (Security Engine)
  - Python 3.12 FastAPI (AI)
  - SQLite / PostgreSQL
  - Dapper ORM
  - FastReport .NET

Architecture Principles:
1. Security: Dongle mandatory via PROT.dll
2. Backup: 10 local (FIFO) + 3 cloud encrypted
3. Data: Isolated DB per Year via MST.sys
4. AI: Local FastAPI (localhost:5050)
5. Sync: Windows Service background worker
6. Migration: SQL versioning in DBA/Migrations/

---

<!-- source: docs/api.md -->

// Path: doc\api.md
// Language: Markdown
// Description: Internal API documentation between the layers.
======================================================================

SHOUTECH ERP V9 - Enterprise System
Tech Stack:
  - C# .NET 9 (UI + Business Logic)
  - C++ 20 (Security Engine)
  - Python 3.12 FastAPI (AI)
  - SQLite / PostgreSQL
  - Dapper ORM
  - FastReport .NET

Architecture Principles:
1. Security: Dongle mandatory via PROT.dll
2. Backup: 10 local (FIFO) + 3 cloud encrypted
3. Data: Isolated DB per Year via MST.sys
4. AI: Local FastAPI (localhost:5050)
5. Sync: Windows Service background worker
6. Migration: SQL versioning in DBA/Migrations/

---

<!-- source: docs/sec.md -->

// Path: doc\migration_guide.md
// Language: Markdown
// Description: Database upgrade guide.
======================================================================

SHOUTECH ERP V9 - Enterprise System
Tech Stack:
  - C# .NET 9 (UI + Business Logic)
  - C++ 20 (Security Engine)
  - Python 3.12 FastAPI (AI)
  - SQLite / PostgreSQL
  - Dapper ORM
  - FastReport .NET

Architecture Principles:
1. Security: Dongle mandatory via PROT.dll
2. Backup: 10 local (FIFO) + 3 cloud encrypted
3. Data: Isolated DB per Year via MST.sys
4. AI: Local FastAPI (localhost:5050)
5. Sync: Windows Service background worker
6. Migration: SQL versioning in DBA/Migrations/
