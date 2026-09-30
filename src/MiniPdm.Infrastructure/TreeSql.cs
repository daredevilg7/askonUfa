namespace MiniPdm.Infrastructure;

public static class TreeSql
{
    public const string Query = """
        WITH RECURSIVE tree AS (
            SELECT o.id AS ObjectId, CAST(NULL AS INTEGER) AS ParentObjectId,
                   '/' || o.id || '/' AS Path, CAST(NULL AS TEXT) AS ParentPath,
                   0 AS Depth, 1 AS DirectQuantity, CAST(1 AS INTEGER) AS PathQuantity,
                   v.id AS VersionId, o.object_type AS Kind, o.designation AS Designation,
                   o.name AS Name, v.material AS Material, v.mass_kg AS MassKg
            FROM pdm_object o
            JOIN object_version v ON v.id = o.current_version_id
            WHERE o.id = @ObjectId AND o.object_type = 1
            UNION ALL
            SELECT child.id, tree.ObjectId,
                   tree.Path || child.id || '/', tree.Path,
                   tree.Depth + 1, link.quantity,
                   CASE WHEN tree.PathQuantity IS NULL OR tree.PathQuantity > 9223372036854775807 / link.quantity
                        THEN NULL ELSE tree.PathQuantity * link.quantity END,
                   version.id, child.object_type, child.designation, child.name,
                   version.material, version.mass_kg
            FROM tree
            JOIN bom_link link ON link.parent_version_id = tree.VersionId
            JOIN pdm_object child ON child.id = link.child_object_id
            JOIN object_version version ON version.id = child.current_version_id
            WHERE instr(tree.Path, '/' || child.id || '/') = 0
        )
        SELECT * FROM tree ORDER BY Path
        """;
}
