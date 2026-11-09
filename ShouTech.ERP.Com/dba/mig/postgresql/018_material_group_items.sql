-- Material-to-group membership is many-to-many. Group headers remain in material_groups.
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_id_company
    ON material_groups(group_id, company_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_materials_id_company
    ON materials(material_id, company_id);
CREATE TABLE IF NOT EXISTS material_group_items (
    company_id TEXT NOT NULL,
    group_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    PRIMARY KEY (company_id, group_id, material_id),
    FOREIGN KEY (group_id, company_id)
        REFERENCES material_groups(group_id, company_id)
        ON DELETE CASCADE,
    FOREIGN KEY (material_id, company_id)
        REFERENCES materials(material_id, company_id)
        ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_material_group_items_material
    ON material_group_items(company_id, material_id);

INSERT INTO material_group_items
    (company_id, group_id, material_id, created_at, created_by)
SELECT m.company_id, g.group_id, m.material_id, CURRENT_TIMESTAMP, m.created_by
FROM materials m
INNER JOIN material_groups g
    ON g.company_id = m.company_id
   AND g.group_code = m.group_code
WHERE COALESCE(m.group_code, '') <> ''
  AND COALESCE(m.is_deleted, FALSE) = FALSE
  AND COALESCE(g.is_deleted, 0) = 0
ON CONFLICT (company_id, group_id, material_id) DO NOTHING;
