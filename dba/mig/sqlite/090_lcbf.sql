-- 090 - Backfill exact source receipt lines for legacy stock movements (SQLite).
-- Only unambiguous receipt-line matches are populated; duplicate material/warehouse lines remain untouched.
WITH candidates AS (
    SELECT sm.stock_movement_id, MIN(l.line_id) AS line_id, COUNT(*) AS match_count
    FROM material_stock_movements sm
    JOIN companies co ON co.company_code = sm.company_id
    JOIN purchase_documents d
      ON d.company_id = CAST(co.company_id AS TEXT)
     AND d.doc_kind = 'RCV' AND d.doc_no = sm.document_no
    JOIN purchase_document_lines l
      ON l.doc_id = d.doc_id
     AND l.material_id = sm.material_id
     AND COALESCE(l.warehouse_id, d.warehouse_id) = sm.warehouse_id
    WHERE sm.movement_type = 'RCV'
      AND sm.source_document_line_id IS NULL
    GROUP BY sm.stock_movement_id
)
UPDATE material_stock_movements
SET source_document_line_id = (
    SELECT line_id FROM candidates
    WHERE candidates.stock_movement_id = material_stock_movements.stock_movement_id
)
WHERE stock_movement_id IN (SELECT stock_movement_id FROM candidates WHERE match_count = 1);
