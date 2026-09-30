namespace MiniPdm.Application;

public enum ImportSeverity { Success, Warning, Error }

public sealed record ImportRow(string FileName, ImportSeverity Severity, string Reason);

public sealed record ValidatedBatch(IReadOnlyList<CadDocument> Accepted, IReadOnlyList<ImportRow> Rows);

public sealed record ApplyStatistics(int Created, int Updated, int Unchanged);

public sealed record ImportReport(IReadOnlyList<ImportRow> Rows, ApplyStatistics Changes)
{
    public int Accepted => Rows.Count(row => row.Severity != ImportSeverity.Error);
    public int Rejected => Rows.Count(row => row.Severity == ImportSeverity.Error);
    public int Warnings => Rows.Count(row => row.Severity == ImportSeverity.Warning);
}

public interface IImportStore
{
    Task<IReadOnlyDictionary<string, string>> FindIdentityConflictsAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default);
    Task<ApplyStatistics> ApplyAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default);
}

public sealed class ImportService(ICadDocumentReader reader, IImportStore store)
{
    public async Task<ImportReport> ImportAsync(string source, CancellationToken ct = default)
    {
        var reads = await reader.ReadSourceAsync(source, ct);
        var batch = ImportValidator.Validate(reads);
        if (batch.Accepted.Count > 0)
        {
            var conflicts = await store.FindIdentityConflictsAsync(batch.Accepted, ct);
            if (conflicts.Count > 0)
            {
                reads = reads.Select(result => conflicts.TryGetValue(result.FileName, out var reason)
                    ? new CadReadResult(result.FileName, null, reason)
                    : result).ToArray();
                batch = ImportValidator.Validate(reads);
            }
        }
        var changes = batch.Accepted.Count == 0
            ? new ApplyStatistics(0, 0, 0)
            : await store.ApplyAsync(batch.Accepted, ct);
        return new ImportReport(batch.Rows, changes);
    }
}
