-- Enforce one external supplier invoice reference per supplier and company.
-- Blank references remain optional; matching is case-insensitive and whitespace-trimmed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_supplier_invoice_reference
    ON purchase_documents(company_id, party_id, lower(trim(reference_no)))
    WHERE doc_kind = 'PUR'
      AND party_id IS NOT NULL
      AND reference_no IS NOT NULL
      AND trim(reference_no) <> ''
      AND is_deleted = 0;
