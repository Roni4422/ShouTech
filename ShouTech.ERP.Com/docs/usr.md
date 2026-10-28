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
