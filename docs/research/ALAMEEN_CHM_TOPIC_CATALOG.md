# Al-Ameen CHM Topic Catalog and ShouTech Implementation Map

## Source and evidence boundary

Source file: user-provided `Al-Ameen.chm`.

The CHM's internal directory/index was inspected and yielded 1,003 distinct HTML topic paths. This catalog records the topic names and directory organization that are directly visible in that index. The HTML topic bodies, screenshots, and procedural instructions have **not** yet been decoded, so this document does not claim to describe details that are only present inside those pages.

The CHM is organized as an older Al-Ameen help set and includes general ERP topics as well as restaurant, hospital, laboratory, and distribution-specific material. ShouTech should adopt relevant business capabilities and workflow patterns, not copy obsolete technology or assume every industry module belongs in the default product.

## 1. Order management topics visible in the index

Directory: `/Ameen/Orders/`

- `Orders_Intro.htm`
- `Purchase_Order.htm`
- `Purchase_Order_-_More.htm`
- `Post_Purchase_Orders.htm`
- `Post_Sales_Order.htm`
- `Sales_Orders_Rout.htm`
- `Selling_order_card.htm`
- `New_Mats_Order.htm`
- `Old_Mats_Order.htm`
- `Ordered_mat.htm`
- `Mat_of_order.htm`
- `Mats_Details.htm`
- `Input_Bill.htm`
- `Input_Details.htm`
- `Preparing.htm`
- `Details.htm`
- `Filter_Orders_Window.htm`
- `Order_Reports_Intro.htm`
- `Purchase_Orders_Report.htm`
- `Settings/Order_Status_Card.htm`
- `Settings/Purshase_orders_types.htm`
- `Settings/Selling_Orders_Types.htm`
- `Settings/Sort.htm`
- `Settings/Sort-.htm`

**ShouTech implication:** orders must be treated as a managed document lifecycle, not merely a header form. The roadmap must cover draft entry, detailed lines, review/preparation, explicit posting, order type/status configuration, filtering, progress/fulfilment, and dedicated reports. Purchase and sales order behavior must remain separate where their stock and accounting consequences differ.

## 2. Bills, warehouse processing, and stock documents

Directory: `/Ameen/Processings/Bills_and_stores_processings/`

Index topics include:

- `Bills_Stores_Prossesses_Intro.htm`
- `bill_1.htm`, `bill_2.htm`, `Bill_header.htm`, `Input_Bill.htm`, `Input_Details.htm`
- `Bills_Options.htm`, `Bill_check.htm`, `Bill_Design.htm`
- `beginning_inventory.htm`
- `Stock_transfer.htm`, `Stock_transfer-2.htm`
- `Assembly&diassembly_bill.htm`
- `Cash_bill.htm`
- `payment_conditions.htm`, `Dividing_Payments.htm`
- `Extra_Details.htm`, `Compare.htm`, `Data_max.htm`
- `Packing_list.htm`, `Packing_Lists.htm`, `Packing_list_preview.htm`
- `Generate_Bill_From_Packing_List.htm`
- `Import_Serial_Number.htm`, `Insert_Serial_Numbers.htm`, `Insert_Serial_Numbers_Button.htm`, `Serial_numbers_bill.htm`
- `Bill_import.htm`, `Bill_export.htm`
- `Bill_Im-export/Standard_import_file_driver.htm`, `Standard_Export_File.htm`, `Imp_Bill_From_TXT_File.htm`, and device/import-driver topics.

**ShouTech implication:** each transaction editor should have a consistent header/lines/options/details/validation/print-export design. Warehouse operations need distinct document types and controlled posting. Serial/batch capture, packing lists, payment terms, split payments, and document comparison belong in the acceptance matrix where applicable.

## 3. Accounting entries and settlement documents

Directory: `/Ameen/Processings/Enries_processings/`

- `Entreies_Processings.htm`
- `Journal_entry.htm`, `Journal_entry-2.htm`
- `Receipt_voucher.htm`, `Payment_voucher.htm`
- `Vouchers.htm`
- `Cash_activity.htm`
- `Entry_import.htm`, `entry_export.htm`
- `Text_file.htm`, `XML_file.htm`
- `Printing_journal_entry.htm`, `Print_reciept_payment_voucher.htm`

**ShouTech implication:** voucher and journal entry screens need separate input and posting states, balanced-entry validation, auditable numbering, print layouts, import/export where supported, and links to the party/document they settle. A payment voucher is not the same event as allocating that payment to an invoice.

## 4. Master cards and setup

Directory: `/Ameen/Cards/`

Index topics include:

- `Chart_of_accounts.htm`, `Account_card.htm`, `Numbering_Accounts.htm`, `normal_account.htm`, `collective_account.htm`, `distributive_account.htm`, `closing_account.htm`
- `Customer_card.htm`, `Vendor_card.htm`
- `Product_Card.htm`, `Product_Card_General.htm`, `Product_Card_Prices.htm`, `Product_Card_Units.htm`
- `Group_card.htm`, `Store_card.htm`, `Chart_of_stores.htm`, `chart_of_materials.htm`
- `Currencies.htm`, `cost_center_card.htm`, `chart_of_cost_centers.htm`
- `pricing_policy.htm`, `unlinked_units.htm`, `vat.htm`, `options.htm`, `search.htm`, `Branches_card.htm`

Directory: `/Ameen/Setting/File_creation/`

- `File_creation.htm`, `First_window.htm`, `Create_file.htm`
- `Beginning_date.htm`, `Set_basic_currency.htm`
- `Ready_chart_of_account.htm`, `Other_chart_of_account.htm`
- `Set_user_name.htm`, `Files_location.htm`, `Enter_server_name_and_database_name.htm`

**ShouTech implication:** cards should be complete business masters with searchable identifiers, units, prices, group/parent relationships, active state, company isolation, and audit fields. Company creation must include fiscal-period start, base currency, chart-of-accounts choice, database location/provider, and user setup. Tax must remain an explicit opt-in for each company; a country choice alone must not activate it.

## 5. Reporting topics

Directory: `/Ameen/Report/`

The index separates report families for:

- Accounts: account movement, account reconciliation, balances, general journal/ledger, profit and loss, budgets, accounting periods, currencies and exchange-rate variations, debts aging.
- Customers: statements, balances, movement by bills/entries/periods, due bills, settlements, inactive and active customer analysis.
- Materials: daily movement, material movement, group/product activity, material aging/expiry, annual trial balance, monthly activity, inactive/most-active products, negative output, inventory reconciliation, stock comparison, price lists, serial-number movement, product profitability and VAT.
- Report operations: conditions, preview, print, export, and report customization.

**ShouTech implication:** reports need meaningful date ranges, company/branch/warehouse/party/item filters, status and document-type filters where relevant, drill-through to source documents, and consistent preview/print/export. Reports must be backed by real query services and not placeholder windows.

## 6. Advanced operations and data exchange

Directory: `/Ameen/Advenced_operations/`

The index includes:

- Backup, restore, backup-copy and restore operations.
- Import/export data, advanced options, date-range export, customized export layouts.
- Importing customer cards and material cards, field mapping, separators, file fields, duplicate/update behavior, import results and error topics.
- Encrypted-file import topics and their file-information / update-field / final-step pages.
- Import/export design and label-design exchange.
- Job scheduling and job-time/schedule-type configuration.
- Database location and authentication topics.

**ShouTech implication:** import/export must be a controlled workflow: file selection, format detection, mapping, validation preview, duplicate policy, dry-run/error report, transactional commit, and import audit. Encrypted ShouTech exchange files should expose authenticated metadata and integrity checks; they must not be treated as ordinary plaintext exports. Backup/restore and scheduled jobs require verification, audit, and recovery tests.

## 7. ShouTech implementation priorities derived from this catalog

1. **Purchase-to-pay:** requisition/request, RFQ and supplier responses, comparison/award, purchase order, partial goods receipts, supplier invoice matching, GRNI, returns, payment vouchers, invoice allocation, supplier statement reconciliation, and reports.
2. **Order controls:** configurable order types and statuses, posting/review gates, filtering, progress tracking, numbering, cancellation/reversal policy, and source-document traceability.
3. **Sales cycle:** quotation, sales order, fulfilment/delivery, invoicing, returns/credit notes, customer receipts, allocation, aging and sales analysis.
4. **Document workbench:** consistent header, lines, discounts/charges, currency, tax (only when company tax is enabled), warehouse, units, serial/batch, payment terms, attachments, validation, posting, print and export.
5. **Inventory operations:** opening balances, transfers, adjustments, stock counts, serial/batch/expiry, packing lists, movement analysis and reconciliation.
6. **Master data:** complete customer/supplier/material/warehouse/account/currency/unit/group/branch/cost-center cards and their setup options.
7. **Reporting:** family-specific filters, drill-through, preview, print, export and saved conditions.
8. **Data lifecycle:** validated import/export, encrypted exchange, backup/restore, scheduling, and auditable error reporting.

## 8. Current implementation boundary

The existing ShouTech purchase documentation describes the implemented PO → receipt → supplier invoice/GRNI → supplier payment/allocation path and explicitly lists outstanding procurement capabilities. RFQ → supplier quotation → comparison → purchase-order conversion has recently been added at the service/repository layer, but it still requires end-to-end tests, UI integration, localization, help documentation, and concurrency/award controls. A physical return from a posted receipt before supplier invoicing, supplier statement matching, bank reconciliation, approval routing, landed costs, and GRNI aging/reporting remain separate work.

This catalog is a navigation/index-based analysis. Before copying any detailed Al-Ameen screen rule, field behavior, calculation, or posting rule into ShouTech, the corresponding HTML topic body and screenshot must be extracted and reviewed.
