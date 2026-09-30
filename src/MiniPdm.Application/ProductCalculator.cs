namespace MiniPdm.Application;

public sealed record MassResult(decimal? TotalKg, IReadOnlyList<string> MissingComponents);

public sealed record SpecificationLine(
    long ObjectId, string? Designation, string Name, long Quantity,
    decimal? UnitMassKg, decimal? TotalMassKg);

public static class ProductCalculator
{
    public static MassResult CalculateMass(IReadOnlyList<BomNode> tree)
    {
        EnsureValidTree(tree);
        var leaves = tree.Where(node => node.Kind != CadKind.Assembly).ToArray();
        if (leaves.Any(node => node.PathQuantity is null))
            throw new OverflowException("Количество по пути превышает допустимый предел");
        var missing = leaves.Where(node => node.MassKg is null)
            .Select(node => $"{node.Name} ({node.Designation ?? "без обозначения"}; путь {node.Path})")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) return new MassResult(null, missing);
        var total = leaves.Aggregate(0m, (sum, node) => checked(sum + node.MassKg!.Value * node.PathQuantity!.Value));
        return new MassResult(total, []);
    }

    public static IReadOnlyList<SpecificationLine> BuildSpecification(IReadOnlyList<BomNode> tree)
    {
        EnsureValidTree(tree);
        var lines = new List<SpecificationLine>();
        foreach (var group in tree.Where(node => node.Kind != CadKind.Assembly).GroupBy(node => node.ObjectId))
        {
            var first = group.First();
            long quantity = 0;
            foreach (var node in group)
                quantity = checked(quantity + (node.PathQuantity ?? throw new OverflowException("Количество по пути превышает допустимый предел")));
            var totalMass = first.MassKg is null ? null : (decimal?)checked(first.MassKg.Value * quantity);
            lines.Add(new SpecificationLine(first.ObjectId, first.Designation, first.Name, quantity, first.MassKg, totalMass));
        }
        return lines.OrderBy(line => line.Designation ?? line.Name, StringComparer.CurrentCulture).ToArray();
    }

    private static void EnsureValidTree(IReadOnlyList<BomNode> tree)
    {
        if (tree.Count == 0 || tree[0].Kind != CadKind.Assembly)
            throw new ArgumentException("Выберите сборку с текущей версией", nameof(tree));
        var cyclicNode = tree.FirstOrDefault(node => node.IsCycle);
        if (cyclicNode is not null)
            throw new InvalidOperationException($"В текущем составе обнаружен цикл: {cyclicNode.Name} ({cyclicNode.Path})");
    }
}
