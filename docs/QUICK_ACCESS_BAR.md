# Quick Access Bar (MWin)

An always-visible icon strip in the main window for the most-used destinations.
Owner layout directive (2026-11-17): the bar sits **below the menu bar**, and when
a menu is opened the **ribbon appears above the quick bar**.

## Layout (MWin root grid, top → bottom)

| Row | Content |
|---|---|
| 0 | Window chrome bar (min / max / close) |
| 1 | `MainMenu` (menu bar) |
| 2 | `RibbonControl` — visible only while a menu's tab is open |
| 3 | **Quick Access Bar** (`QuickAccessBar`) — always visible |
| 4 | Content (`CurrentContent`) + `RecycleBinLayer` overlay (unchanged) |
| 5 | `InternalWindowDock` |
| 6 | `MainStatusBar` |

The previous emoji `ToolBar` was removed: its Save/Undo/Redo/Print/Search buttons
only set status text (no real commands behind them), and a raw `ToolBar` control
is the pattern that crashed this shell at startup once (ToolBar template /
IsItemsHost). The bar is built from the app's flat command-bar pattern instead:
a `Border` + horizontal `StackPanel` of styled `Button`s. The one real feature
the old toolbar carried — the accent-color picker — moved into the quick bar
(last button, after the separator) with its handler unchanged.

## Buttons → navigation key → window

Each navigation button carries the **same action key the ribbon uses** in its
`Tag`, and `MWin.OnQuickAccessClick` routes it through the exact chain a ribbon
click uses (`RibbonReportRouter` → `RibbonNav` → `RptNav` →
`MainShVM.HandleRibbonAction`), so a quick-bar button always opens the same
window as its ribbon counterpart. Tooltips are the window's localized title,
applied by the central localization engine via `loc:LocProps.ToolTipKey`
(re-localized automatically on language change). The bar has no FlowDirection
pin: it inherits the window direction from the central mechanism and mirrors
under Arabic (`MWin.ApplyUiDirection` sets the bar's direction explicitly,
like the menu, ribbon and status bar).

| # | Button | Nav key (Tag) | Router | Window opened | Tooltip key |
|---|---|---|---|---|---|
| 1 | Dashboard | — (`ShowDashboardCommand`) | MainShVM | Dashboard (`DashVw`) in the content area | `Dashboard.Title` |
| 2 | Sales Invoice | `Invoices.SalesInvoice` | RptNav | `InvoiceWin` (SalesInvoice) | `Invoices.SalesInvoice` |
| 3 | Point of Sale | `POS.OpenCashier` | RptNav | `PosWin` | `POS.OpenCashier` |
| 4 | Journal Voucher | `Bonds.JournalVoucher` | RptNav | `JrnWin` | `Bonds.JournalVoucher` |
| 5 | Receipt Voucher | `Bonds.ReceiptVoucher` | RptNav | `CshWin` (Receipt) | `Bonds.ReceiptVoucher` |
| 6 | Materials Directory | `View.MaterialTree` | RibbonNav | `MatDirWin` | `Inventory.MaterialTree` |
| 7 | Customers | `Customers.CustomerCard` | RptNav | `CstCard` | `Customers.CustomerCard` |
| 8 | Orders | `Orders.SalesOrder` | RptNav | `OrdWin` (Sales) | `Orders.SalesOrder` |
| 9 | Cheques | `Bonds.ReceiptCheque` | RptNav | `ChqWin` | `Bonds.ReceiptCheque` |
| 10 | Banks | `Banks.DefineBanks` | RptNav | `BnkCard` | `Banks.DefineBanks` |
| 11 | Reports | — (`OnQuickReportsClick`) | `ShowRibbonTab("TabReports")` | Opens the ribbon on the Reports tab (same as the Reports menu) | `Menu.Reports` |
| 12 | Recycle Bin | — (`OnRecycleBinClick`) | MWin | `BinWin` (modal); tooltip shows the live item count like the other bin buttons | `RecycleBin.Title` |
| 13 | Accent picker | — (`OnAccentPickerClick`) | MWin | Accent color context menu (unchanged behavior) | (literal "Accent color") |

Navigation failures are reported through the error catalog
(`AppHost.Errors.Report`, `NavigationFailed` / `ShellOperationFailed`) — no
silent catches.

## Icons — current state and how to swap in real PNGs later

The owner will supply real icon images later. Today:

- **Recycle Bin** already uses a real image: `Resources/Ico/ArchiveBin.png`
  (the only per-destination image that exists in the repo).
- All other buttons show a **Segoe MDL2 Assets** glyph, rendered by the central
  `QuickAccessButtonStyle` template in `Themes/ShRibn.xaml`.

Each button's icon is **one settable value** on the button's own line in
`Views/MWin.xaml`, via the attached properties in
`cod/app/src/Interaction/QuickBarProps.cs`:

- `qb:QuickBarProps.Glyph="&#xE8A5;"` — the glyph (current state), or
- `qb:QuickBarProps.Icon="/Resources/Ico/QbInvoice.png"` — an image.

**To swap a button to a real icon (one-line change per button):**

1. Drop the PNG into `cod/app/src/Resources/Ico/` — `MainApp.csproj` already
   includes `Resources\Ico\**\*.png` as a compiled resource, so no project
   edit is needed.
2. On that button's line in `Views/MWin.xaml`, replace the
   `qb:QuickBarProps.Glyph="&#x....;"` attribute with
   `qb:QuickBarProps.Icon="/Resources/Ico/<file>.png"`.

Nothing else changes: the central template detects the image (via the
`HasIcon` attached property, maintained automatically) and renders it at
20×20 instead of the glyph.

### Swap table (suggested future filenames)

| Button | Glyph today (MDL2) | Suggested icon file in `Resources/Ico` |
|---|---|---|
| Dashboard | `E80F` Home | `QbDashboard.png` |
| Sales Invoice | `E8A5` Document | `QbInvoice.png` |
| Point of Sale | `E7BF` ShoppingCart | `QbPos.png` |
| Journal Voucher | `E8F1` Library | `QbJournal.png` |
| Receipt Voucher | `E8C7` PaymentCard | `QbCash.png` |
| Materials Directory | `E7B8` Package | `QbMaterials.png` |
| Customers | `E716` People | `QbCustomers.png` |
| Orders | `F0E3` ClipboardList | `QbOrders.png` |
| Cheques | `E77C` Memo | `QbCheques.png` |
| Banks | `E825` Bank | `QbBanks.png` |
| Reports | `E9D2` AreaChart | `QbReports.png` |
| Recycle Bin | — (image already) | `ArchiveBin.png` (in use) |

Glyph code points verified against Microsoft's Segoe MDL2 Assets icon list.
Recommended icon size: square PNG, 32×32 or larger (rendered at 20×20).

## Styling

All styling lives in the central files — nothing is hand-rolled in MWin.xaml:

- `Themes/ShRibn.xaml`: `QuickAccessBarStyle` (strip: shell light surface,
  top divider, 38px) and `QuickAccessButtonStyle` (38×30 button, glyph/image
  template, shell hover/pressed/disabled states).
- `cod/app/src/Interaction/QuickBarProps.cs`: `Glyph` / `Icon` / `HasIcon`
  attached properties.

## Behavior preserved

- Menu clicks still select ribbon tabs (including the toggle-collapse on a
  second click); every ribbon button works as before; ribbon clicks still
  collapse the ribbon, and quick-bar navigation clicks collapse it too.
- During company switch (`FreezeForCompanySwitch`) the quick bar is disabled
  together with the menu and toolbar-era controls, and re-enabled on unfreeze.
- Status bar and the floating Recycle Bin corner button/layer are unchanged.
