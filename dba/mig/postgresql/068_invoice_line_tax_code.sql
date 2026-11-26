-- 068 - Persist the selected tax code on each commercial document line.
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS tax_code varchar(50) NOT NULL DEFAULT '';
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS tax_code varchar(50) NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_tax_code ON sales_document_lines(tax_code);
CREATE INDEX IF NOT EXISTS ix_purchase_document_lines_tax_code ON purchase_document_lines(tax_code);
