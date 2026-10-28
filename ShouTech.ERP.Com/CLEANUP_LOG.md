# ShouTech ERP v0.4 — Cleanup Log
**Date:** 2026-10-01  
**Action:** Safe archive of dead / unused / duplicate files  
**Policy:** Archive first (recoverable), never hard-delete without backup.

## Removed from active tree (moved to `_archived_dead/`)

### 1. Duplicate leftovers after rename scripts
| Archived file | Active replacement | Reason |
|---------------|-------------------|--------|
| `Converters/BoolVis.cs` | `BoolToVisibilityConverter.cs` | XAML uses long name only |
| `Navigation/RibbonModuleMapper.cs` | `RibModMap.cs` | Shell + MainShVM call `RibModMap` |
| `Views/Modules/ModWsVw.xaml` + `.cs` | `ModuleWorkspaceView.xaml` + `.cs` | `Shell.CreateWorkspace` uses ModuleWorkspaceView |
| `Converters/LangFlwCv.cs` | — | Unused (FlowDirection handled in code) |
| `Converters/LanguageToFlowDirectionConverter.cs` | — | Unused |

### 2. Empty placeholders (0 bytes)
- `bld/bld.bat`
- `bld/pkg.ps1`
- `bld/sgn.ps1`
- `LICENSE`
- `doc/dep.md`

### 3. Finished one-time rename scripts
- `tools/rename_short.ps1`
- `tools/rename_short2.ps1`
- `tools/rename_distinct.ps1`

## Additional professional fix
- Renamed `src/ShouTech.Infrastructure/Class1.cs` → `SqlJournalRepository.cs`  
  (file already contained the real `SqlJournalRepository` class — name was misleading)

## Safety verification performed
- No external references found for any removed type/class.
- MainApp still compiles all remaining `**/*.cs` and `**/*.xaml`.
- Domain / Application / Infrastructure project references unchanged.

## Recovery
All archived files are under `_archived_dead/` with original relative paths preserved by category.
To restore any file: copy it back to its original location.

## Next recommended steps
1. Implement real content for `bld/bld.bat`, `pkg.ps1`, `sgn.ps1` (enterprise build pipeline).
2. Expand unit tests under `tests/Kern.Tests/`.
3. Populate `int/fore.onnx` when AI forecasting model is ready.
