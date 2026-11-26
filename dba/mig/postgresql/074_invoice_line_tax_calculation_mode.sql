-- 074 - Persist per-line inclusive/exclusive tax calculation mode (PostgreSQL).
ALTER TABLE sales_document_lines
    ADD COLUMN IF NOT EXISTS tax_calculation_mode varchar(16) NOT NULL DEFAULT 'Exclusive';
ALTER TABLE purchase_document_lines
    ADD COLUMN IF NOT EXISTS tax_calculation_mode varchar(16) NOT NULL DEFAULT 'Exclusive';

DO $tax_mode$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_sales_line_tax_calculation_mode') THEN
        ALTER TABLE sales_document_lines
            ADD CONSTRAINT ck_sales_line_tax_calculation_mode
            CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_purchase_line_tax_calculation_mode') THEN
        ALTER TABLE purchase_document_lines
            ADD CONSTRAINT ck_purchase_line_tax_calculation_mode
            CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
    END IF;
END;
$tax_mode$;
