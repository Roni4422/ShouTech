// Path: tst\unittests\kern_tests.cs
// Language: C# xUnit
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
