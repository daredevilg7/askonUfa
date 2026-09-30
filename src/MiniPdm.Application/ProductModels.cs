using MiniPdm.Domain;

namespace MiniPdm.Application;

public sealed record ProductCard(
    long ObjectId, CadKind Kind, string? Designation, string Name,
    int? VersionNo, VersionState? State, string? Material, decimal? MassKg);

public sealed record VersionInfo(int VersionNo, VersionState State);

public sealed record BomNode(
    long ObjectId, long? ParentObjectId, long VersionId,
    string Path, string? ParentPath, int Depth, int DirectQuantity, long? PathQuantity,
    CadKind Kind, string? Designation, string Name, string? Material, decimal? MassKg,
    bool IsCycle = false);

public interface IProductCatalog
{
    Task<IReadOnlyList<ProductCard>> SearchAsync(string search, CancellationToken ct = default);
    Task<ProductCard?> GetCardAsync(long objectId, CancellationToken ct = default);
    Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(long objectId, CancellationToken ct = default);
    Task<IReadOnlyList<BomNode>> GetTreeAsync(long objectId, CancellationToken ct = default);
    Task ChangeStateAsync(long objectId, VersionState target, CancellationToken ct = default);
}
