# ShouTech ERP — User Guide

**Purpose:** Help users perform routine tasks safely in ShouTech ERP. This guide is written for operators, accountants, warehouse staff, managers, and company administrators.

> **Important:** Available commands depend on the installed build, enabled modules, company configuration, user permissions, and database setup. A menu item described here may not be available in every installation. Do not assume a transaction is saved or posted until the application confirms it.

## 1. Getting help

- Press **F1** in the main window or a supported ShouTech window to open this guide.
- Use the section list on the left to jump to a topic.
- If a command is unavailable, ask your ShouTech administrator to check the license, module availability, company settings, and your permissions.
- Before entering live transactions, ask your administrator which company, fiscal year, warehouse, currency, and numbering rules are active.

## 2. First sign-in

1. Start ShouTech ERP.
2. Select the interface language when prompted. You can change it later if your account has access to language settings.
3. Select the company you are authorized to work in.
4. Sign in using the username and password supplied by your administrator.
5. Confirm the company name and user shown in the main window before entering or viewing business data.

Never share your password. Do not use development/demo accounts for real company operations.

## 3. Understanding the main window

The main window provides the menu bar, Ribbon commands, work area, and status information.

- **Menu bar:** Opens the major business areas.
- **Ribbon:** Shows commands grouped by task. Select a tab, then choose the command you need.
- **Work area:** Displays the dashboard, module workspace, or opened windows.
- **Status area:** Helps identify the current working context.

The exact tabs and commands depend on the installed build and your permissions. If you are unsure which command to use, press F1 and consult the relevant topic.

## 4. Company and working context

Before creating records, verify:
- The selected company is correct.
- The active fiscal year and accounting period are open.
- The required currency and exchange-rate policy are configured.
- The correct warehouse or branch is selected where relevant.
- Your user account has the permissions required for the task.

Company setup and database configuration should be performed by an authorized administrator. Do not create a second company or change database settings to work around an access problem.

## 5. Master data: customers, suppliers, items, and accounts

Master data is reused by transactions and reports. Keep it accurate and avoid creating duplicate records.

### Customer and supplier records

1. Open the relevant customer or supplier area from the menu or Ribbon.
2. Choose the command for a new record.
3. Enter the identifying information required by your company.
4. Check contact details, tax/registration details (only when applicable), currency, payment terms, and credit settings.
5. Save the record and verify that it can be found using the list/search screen.

### Item and material records

1. Open Inventory and select the item/material card command.
2. Enter the item code and description.
3. Select the correct unit, item group, and default warehouse where required.
4. Enter barcode, costing, pricing, and tax information only when applicable and authorized.
5. Save, then search for the item to confirm the saved values.

Use a consistent item code and unit policy. Do not create a new item merely because an existing item is difficult to find; search by code, barcode, and description first.

### Chart of accounts

The chart of accounts is foundational accounting data. Only authorized accounting administrators should create or modify accounts. Confirm account type, parent account, currency rules, and posting restrictions before using a new account in a transaction.

## 6. Entering business documents

The application may provide entry windows for invoices, vouchers, orders, stock documents, and other records.

1. Open the required entry window.
2. Start a new document using its New command.
3. Confirm the document date, company, fiscal period, currency, and document number.
4. Select the customer, supplier, account, item, warehouse, or other required references.
5. Enter quantities, prices, discounts, charges, and notes.
6. Review totals, tax treatment, and account/warehouse effects.
7. Save the document.
8. Reopen or search for the saved document and verify its status.

### Document numbering

Where automatic numbering is enabled, a new entry should receive the next available number according to the configured sequence. Do not manually reuse a number or change a saved document number unless your administrator's approved procedure allows it.

### Draft, saved, approved, and posted states

These states are not interchangeable. Saving a draft does not necessarily post it to accounting or inventory. Before posting or approving, review the date, accounts, quantities, totals, tax treatment, and supporting references. If the application displays a confirmation or validation message, read it before continuing.

## 7. Accounting documents and corrections

Before posting a journal, receipt, payment, or other accounting document:
- Confirm debit and credit totals balance where applicable.
- Check account codes, dates, descriptions, currency, and references.
- Confirm the accounting period is open.
- Confirm the document belongs to the correct company.

**Do not delete a posted financial transaction to correct an error.** Follow your organization's approved cancellation or reversal procedure so the original transaction and its audit history remain traceable. If you cannot find the reversal command, stop and contact the accounting administrator.

## 8. Inventory and warehouse operations

Before recording a stock receipt, issue, transfer, or adjustment:
- Verify the item and unit of measure.
- Verify source and destination warehouses.
- Check the document date and quantity.
- Review available stock and any lot/serial requirements used by your company.
- Confirm the reason and supporting reference for adjustments.

A warehouse transfer is not the same as a purchase or sale. Use the correct transaction type so stock quantities and accounting effects are recorded correctly. Never use a stock adjustment to conceal an unexplained difference; investigate and document the reason.

## 9. Point of Sale

Use the POS module only after the company administrator has configured the required terminal, cash drawer/payment methods, price rules, and user permissions.

At the start of a shift, verify the active company, terminal, cashier, and opening cash procedure. For each sale, scan or select the correct items, verify quantities and prices, select the payment method, and confirm the receipt. At shift end, follow the approved cash-up and reconciliation procedure.

If a payment is declined or the terminal loses connectivity, verify the transaction status before attempting the payment again. This helps prevent duplicate charges or duplicate sales.

## 10. Reports and search

1. Open the relevant report or list.
2. Set the company, date range, fiscal period, warehouse, customer, supplier, or other available filters.
3. Run or refresh the report.
4. Check the filter criteria and report date before interpreting the result.
5. Export or print only when the command is available and your role permits it.

A report with no rows may mean that the filters are too restrictive; it does not necessarily mean that no transactions exist. Check the company, period, status, and date range.

## 11. Tax settings

Selecting a country does **not** by itself mean the company's tax system is enabled. Tax functionality should only be activated by an authorized user after the company has decided to use it and completed the required country-specific registration and configuration.

If tax is disabled for the company, do not manually add tax codes or tax postings as a workaround. If tax is enabled, verify the registration details, applicable tax rules, effective dates, accounts, and reporting requirements with the company's tax adviser or responsible authority.

Tax rules change. The software configuration does not replace professional advice or the official requirements of the relevant tax authority.

## 12. Backup and restore

Backups protect company data against hardware failure, accidental changes, and other incidents.

- Follow your organization's backup schedule.
- Confirm that a backup completed successfully and that the backup file is stored in an approved secure location.
- Keep backup copies separate from the computer hosting the live database.
- Restrict access to backup files because they may contain confidential company and personal information.
- Test restoration periodically using an approved test environment.

Restore operations can replace or overwrite data. Only an authorized administrator should restore a backup. Before restoring, confirm the target company/database and the backup date, and preserve a copy of the current database where possible.

## 13. Users, roles, and security

- Use an individual account; do not share accounts.
- Give each user only the permissions needed for their job.
- Lock your computer when away from your desk.
- Report unexpected access, missing records, or suspicious activity to the administrator.
- Do not send database files, backups, passwords, or exported customer data through unapproved channels.

Administrators should review user access when employees change roles or leave the organization.

## 14. Language and display

ShouTech supports language and right-to-left/left-to-right interface behavior where the relevant language resources are installed. Changing the interface language changes labels and layout direction; it does not change the underlying business data or accounting values.

If a label is missing or appears as a resource key, report the window name, selected language, and a screenshot to support.

## 15. Troubleshooting

### I cannot see a menu or command
Your role, license, company configuration, or enabled modules may not include it. Ask the administrator to verify access; do not use another user's credentials.

### I cannot save a document
Read the validation message. Check required fields, document date, open accounting period, referenced master records, number sequence, and permissions. Do not repeatedly submit the same document without checking whether it was already saved.

### A report is empty or incorrect
Check the company, date range, fiscal period, document status, warehouse, currency, and other filters. Confirm that the underlying documents were saved or posted as required.

### A document appears more than once
Stop entering further copies. Check the document list and status, then ask an administrator to investigate before deleting or reversing anything.

### The application closes or displays an error
Record the time, window, action being performed, exact error text/code, and whether the issue can be repeated. Send this information to your administrator or ShouTech support. Do not include passwords or confidential customer data in the report.

## 16. Safe daily checklist

- Confirm the company and fiscal period before work.
- Search before creating a new customer, supplier, or item.
- Review documents before saving, approving, or posting.
- Use the correct transaction type for accounting and stock movements.
- Never delete posted financial history to correct an error; use the approved reversal process.
- Verify payment and POS status before retrying.
- Review report filters before relying on totals.
- Protect passwords, exports, and backups.
- Press F1 whenever you need help.

---

**This guide describes safe operating practices.** Screen labels and available workflows can vary by installed version, enabled modules, permissions, and company configuration. The in-app help should be updated whenever a user-facing workflow changes.
