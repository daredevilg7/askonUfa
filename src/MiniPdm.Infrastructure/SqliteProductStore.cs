using System.Globalization;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using MiniPdm.Application;
using MiniPdm.Domain;

namespace MiniPdm.Infrastructure;

public sealed class SqliteProductStore : IImportStore, IProductCatalog
{
    private readonly string _connectionString;

    public SqliteProductStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MiniPdm.Infrastructure.Database.001_initial.sql")
            ?? throw new InvalidOperationException("Не найден скрипт схемы базы данных");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<ApplyStatistics> ApplyAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        try
        {
            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                var keySql = document.Kind == CadKind.StandardPart
                    ? "SELECT id AS Id, object_type AS Kind FROM pdm_object WHERE object_type = 3 AND name = @Key"
                    : "SELECT id AS Id, object_type AS Kind FROM pdm_object WHERE designation = @Key";
                var key = document.Kind == CadKind.StandardPart ? document.Name : document.Designation;
                var existing = await connection.QuerySingleOrDefaultAsync<ObjectRow>(
                    new CommandDefinition(keySql, new { Key = key }, transaction, cancellationToken: ct));
                if (existing is not null && existing.Kind != (int)document.Kind)
                    throw new InvalidOperationException($"Тип объекта «{document.FileName}» не совпадает с сохранённым");
                long id;
                if (existing is null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO pdm_object(object_type, designation, name) VALUES (@Kind, @Designation, @Name)",
                        new { Kind = (int)document.Kind, document.Designation, document.Name }, transaction, cancellationToken: ct));
                    id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: transaction, cancellationToken: ct));
                }
                else
                {
                    id = existing.Id;
                    await connection.ExecuteAsync(new CommandDefinition("UPDATE pdm_object SET name = @Name WHERE id = @Id",
                        new { document.Name, Id = id }, transaction, cancellationToken: ct));
                }
                ids.Add(document.FileName, id);
            }

            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                var objectId = ids[document.FileName];
                var current = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition("""
                    SELECT v.id AS Id, v.version_no AS VersionNo, v.state AS State,
                           v.material AS Material, v.mass_kg AS MassKg
                    FROM pdm_object o LEFT JOIN object_version v ON v.id = o.current_version_id
                    WHERE o.id = @ObjectId
                    """, new { ObjectId = objectId }, transaction, cancellationToken: ct));
                var newLinks = new Dictionary<long, int>();
                foreach (var component in document.Components)
                {
                    if (!ids.TryGetValue(component.FileName, out var childId))
                        throw new InvalidOperationException($"Компонент «{component.FileName}» не входит в принятый пакет");
                    newLinks[childId] = checked(newLinks.GetValueOrDefault(childId) + component.Quantity);
                }
                var oldLinks = current is null || current.Id == 0
                    ? new Dictionary<long, int>()
                    : (await connection.QueryAsync<LinkRow>(new CommandDefinition(
                        "SELECT child_object_id AS ChildId, quantity AS Quantity FROM bom_link WHERE parent_version_id = @VersionId",
                        new { VersionId = current.Id }, transaction, cancellationToken: ct)))
                        .ToDictionary(link => link.ChildId, link => link.Quantity);
                var same = current is not null && current.Id != 0
                    && current.Material == document.Material
                    && ParseMass(current.MassKg) == document.MassKg
                    && oldLinks.Count == newLinks.Count
                    && oldLinks.All(pair => newLinks.TryGetValue(pair.Key, out var quantity) && quantity == pair.Value);
                if (same)
                {
                    unchanged++;
                    continue;
                }

                var mass = document.MassKg?.ToString(CultureInfo.InvariantCulture);
                long versionId;
                if (current is not null && current.Id != 0 && current.State == (int)VersionState.InWork)
                {
                    versionId = current.Id;
                    await connection.ExecuteAsync(new CommandDefinition("""
                        UPDATE object_version SET material = @Material, mass_kg = @Mass
                        WHERE id = @VersionId AND state = 1
                        """, new { document.Material, Mass = mass, VersionId = versionId }, transaction, cancellationToken: ct));
                    await connection.ExecuteAsync(new CommandDefinition("DELETE FROM bom_link WHERE parent_version_id = @VersionId",
                        new { VersionId = versionId }, transaction, cancellationToken: ct));
                    updated++;
                }
                else
                {
                    var next = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COALESCE(MAX(version_no), 0) + 1 FROM object_version WHERE object_id = @ObjectId",
                        new { ObjectId = objectId }, transaction, cancellationToken: ct));
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO object_version(object_id, version_no, state, material, mass_kg)
                        VALUES (@ObjectId, @VersionNo, @State, @Material, @Mass)
                        """, new { ObjectId = objectId, VersionNo = next, State = (int)VersionState.InWork, document.Material, Mass = mass }, transaction, cancellationToken: ct));
                    versionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: transaction, cancellationToken: ct));
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE pdm_object SET current_version_id = @VersionId WHERE id = @ObjectId",
                        new { VersionId = versionId, ObjectId = objectId }, transaction, cancellationToken: ct));
                    created++;
                }
                foreach (var link in newLinks)
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO bom_link(parent_version_id, child_object_id, quantity)
                        VALUES (@VersionId, @ChildId, @Quantity)
                        """, new { VersionId = versionId, ChildId = link.Key, Quantity = link.Value }, transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
            return new ApplyStatistics(created, updated, unchanged);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProductCard>> SearchAsync(string search, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<CardRow>(new CommandDefinition("""
            SELECT o.id AS ObjectId, o.object_type AS Kind, o.designation AS Designation,
                   o.name AS Name, v.version_no AS VersionNo, v.state AS State,
                   v.material AS Material, v.mass_kg AS MassKg
            FROM pdm_object o LEFT JOIN object_version v ON v.id = o.current_version_id
            ORDER BY o.name, o.id
            """, cancellationToken: ct));
        return rows.Where(row => string.IsNullOrWhiteSpace(search)
            || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Designation?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .Take(100).Select(ToCard).ToArray();
    }

    public async Task<ProductCard?> GetCardAsync(long objectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition("""
            SELECT o.id AS ObjectId, o.object_type AS Kind, o.designation AS Designation,
                   o.name AS Name, v.version_no AS VersionNo, v.state AS State,
                   v.material AS Material, v.mass_kg AS MassKg
            FROM pdm_object o LEFT JOIN object_version v ON v.id = o.current_version_id
            WHERE o.id = @ObjectId
            """, new { ObjectId = objectId }, cancellationToken: ct));
        return row is null ? null : ToCard(row);
    }

    public async Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(long objectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<VersionInfoRow>(new CommandDefinition(
            "SELECT version_no AS VersionNo, state AS State FROM object_version WHERE object_id = @ObjectId ORDER BY version_no",
            new { ObjectId = objectId }, cancellationToken: ct));
        return rows.Select(row => new VersionInfo(row.VersionNo, (VersionState)row.State)).ToArray();
    }

    public async Task<IReadOnlyList<BomNode>> GetTreeAsync(long objectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<BomRow>(new CommandDefinition(TreeSql.Query,
            new { ObjectId = objectId }, cancellationToken: ct));
        return rows.Select(row => new BomNode(row.ObjectId, row.ParentObjectId, row.VersionId,
            row.Path, row.ParentPath, row.Depth, row.DirectQuantity, row.PathQuantity,
            (CadKind)row.Kind, row.Designation, row.Name, row.Material, ParseMass(row.MassKg))).ToArray();
    }

    public async Task ChangeStateAsync(long objectId, VersionState target, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition("""
            SELECT v.id AS Id, v.state AS State FROM pdm_object o
            JOIN object_version v ON v.id = o.current_version_id WHERE o.id = @ObjectId
            """, new { ObjectId = objectId }, transaction, cancellationToken: ct));
        if (current is null || !VersionRules.CanTransition((VersionState)current.State, target))
            throw new InvalidOperationException("Переход состояния недопустим для текущей версии");
        await connection.ExecuteAsync(new CommandDefinition("UPDATE object_version SET state = @State WHERE id = @Id",
            new { State = (int)target, current.Id }, transaction, cancellationToken: ct));
        if (target == VersionState.Cancelled)
        {
            var previousId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT id FROM object_version WHERE object_id = @ObjectId AND state != 3
                ORDER BY version_no DESC LIMIT 1
                """, new { ObjectId = objectId }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE pdm_object SET current_version_id = @PreviousId WHERE id = @ObjectId",
                new { PreviousId = previousId, ObjectId = objectId }, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
    }

    private static decimal? ParseMass(string? value) => value is null ? null : decimal.Parse(value, CultureInfo.InvariantCulture);
    private static ProductCard ToCard(CardRow row) => new(row.ObjectId, (CadKind)row.Kind,
        row.Designation, row.Name, row.VersionNo, row.State is null ? null : (VersionState)row.State,
        row.Material, ParseMass(row.MassKg));

    private sealed class ObjectRow { public long Id { get; set; } public int Kind { get; set; } }
    private sealed class VersionRow { public long Id { get; set; } public int VersionNo { get; set; } public int State { get; set; } public string? Material { get; set; } public string? MassKg { get; set; } }
    private sealed class LinkRow { public long ChildId { get; set; } public int Quantity { get; set; } }
    private sealed class CardRow { public long ObjectId { get; set; } public int Kind { get; set; } public string? Designation { get; set; } public string Name { get; set; } = ""; public int? VersionNo { get; set; } public int? State { get; set; } public string? Material { get; set; } public string? MassKg { get; set; } }
    private sealed class VersionInfoRow { public int VersionNo { get; set; } public int State { get; set; } }
    private sealed class BomRow { public long ObjectId { get; set; } public long? ParentObjectId { get; set; } public long VersionId { get; set; } public string Path { get; set; } = ""; public string? ParentPath { get; set; } public int Depth { get; set; } public int DirectQuantity { get; set; } public long? PathQuantity { get; set; } public int Kind { get; set; } public string? Designation { get; set; } public string Name { get; set; } = ""; public string? Material { get; set; } public string? MassKg { get; set; } }
}
