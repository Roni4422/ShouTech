-- Material-to-group membership is many-to-many. Group headers remain in material_groups.
CREATE TABLE IF NOT EXISTS material_group_items (
    company_id TEXT NOT NULL,
    group_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    PRIMARY KEY (company_id, group_id, material_id),
    FOREIGN KEY (group_id, company_id)
        REFERENCES material_groups(group_id, company_id)
        ON DELETE CASCADE,
    FOREIGN KEY (material_id, company_id)
        REFERENCES materials(material_id, company_id)
        ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_id_company
    ON material_groups(group_id, company_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_materials_id_company
    ON materials(material_id, company_id);
CREATE INDEX IF NOT EXISTS ix_material_group_items_material
    ON material_group_items(company_id, material_id);
