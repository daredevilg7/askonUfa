PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS pdm_object (
    id INTEGER PRIMARY KEY,
    object_type INTEGER NOT NULL CHECK (object_type IN (1, 2, 3)),
    designation TEXT,
    name TEXT NOT NULL,
    current_version_id INTEGER REFERENCES object_version(id),
    CHECK ((object_type = 3 AND designation IS NULL) OR (object_type IN (1, 2) AND designation IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_object_designation ON pdm_object(designation) WHERE designation IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_standard_name ON pdm_object(name) WHERE object_type = 3;
CREATE INDEX IF NOT EXISTS ix_object_name ON pdm_object(name);

CREATE TABLE IF NOT EXISTS object_version (
    id INTEGER PRIMARY KEY,
    object_id INTEGER NOT NULL REFERENCES pdm_object(id),
    version_no INTEGER NOT NULL CHECK (version_no > 0),
    state INTEGER NOT NULL CHECK (state IN (1, 2, 3)),
    material TEXT,
    mass_kg TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (object_id, version_no)
);
CREATE INDEX IF NOT EXISTS ix_version_object_state ON object_version(object_id, state, version_no);

CREATE TABLE IF NOT EXISTS bom_link (
    id INTEGER PRIMARY KEY,
    parent_version_id INTEGER NOT NULL REFERENCES object_version(id),
    child_object_id INTEGER NOT NULL REFERENCES pdm_object(id),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    UNIQUE (parent_version_id, child_object_id)
);
CREATE INDEX IF NOT EXISTS ix_link_parent ON bom_link(parent_version_id);
