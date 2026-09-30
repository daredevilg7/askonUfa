namespace MiniPdm.Application;

public enum CadKind { Assembly = 1, Part = 2, StandardPart = 3 }

public sealed record CadComponent(string FileName, int Quantity);

public sealed record CadDocument(
    string FileName,
    CadKind Kind,
    string? Designation,
    string Name,
    string? Material,
    decimal? MassKg,
    IReadOnlyList<CadComponent> Components,
    int FormatVersion = 1);

public sealed record CadReadResult(string FileName, CadDocument? Document, string? Error);

public interface ICadDocumentReader
{
    Task<IReadOnlyList<CadReadResult>> ReadSourceAsync(string source, CancellationToken ct = default);
}
