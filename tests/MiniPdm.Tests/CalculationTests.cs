using MiniPdm.Application;

namespace MiniPdm.Tests;

public class CalculationTests
{
    private static BomNode Node(long id, string path, string? parent, CadKind kind, long quantity, decimal? mass, string name) =>
        new(id, null, id, path, parent, path.Count(ch => ch == '/') - 2, 1, quantity, kind,
            kind == CadKind.StandardPart ? null : $"РДЦЛ.304112.{id:000}", name, kind == CadKind.Part ? "Сталь" : null, mass);

    [Fact]
    public void Масса_и_спецификация_агрегируют_лист_по_разным_ветвям()
    {
        var tree = new[]
        {
            Node(1, "/1/", null, CadKind.Assembly, 1, null, "Изделие"),
            Node(2, "/1/2/", "/1/", CadKind.Assembly, 2, null, "Узел А"),
            Node(4, "/1/2/4/", "/1/2/", CadKind.Part, 8, 0.125m, "Общая деталь"),
            Node(3, "/1/3/", "/1/", CadKind.Assembly, 3, null, "Узел Б"),
            Node(4, "/1/3/4/", "/1/3/", CadKind.Part, 9, 0.125m, "Общая деталь"),
            Node(5, "/1/5/", "/1/", CadKind.StandardPart, 2, 0.05m, "Болт")
        };
        Assert.Equal(2.225m, ProductCalculator.CalculateMass(tree).TotalKg);
        var spec = ProductCalculator.BuildSpecification(tree);
        Assert.Equal(2, spec.Count);
        Assert.Equal(17, spec.Single(row => row.ObjectId == 4).Quantity);
        Assert.Null(spec.Single(row => row.ObjectId == 5).Designation);
    }

    [Fact]
    public void Без_массы_нет_частичного_итога_но_строка_спецификации_остаётся()
    {
        var tree = new[]
        {
            Node(1, "/1/", null, CadKind.Assembly, 1, null, "Изделие"),
            Node(2, "/1/2/", "/1/", CadKind.Part, 2, 0.4m, "Деталь"),
            Node(3, "/1/3/", "/1/", CadKind.Part, 1, null, "Прокладка")
        };
        var mass = ProductCalculator.CalculateMass(tree);
        Assert.Null(mass.TotalKg);
        Assert.Contains(mass.MissingComponents, item => item.Contains("Прокладка"));
        var spec = ProductCalculator.BuildSpecification(tree);
        Assert.Null(spec.Single(row => row.ObjectId == 3).UnitMassKg);
        Assert.Null(spec.Single(row => row.ObjectId == 3).TotalMassKg);
    }

    [Fact]
    public void Переполнение_количества_не_даёт_неверный_результат()
    {
        var tree = new[]
        {
            Node(1, "/1/", null, CadKind.Assembly, 1, null, "Изделие"),
            Node(2, "/1/2/", "/1/", CadKind.Part, long.MaxValue, 1m, "Деталь"),
            Node(2, "/1/3/2/", "/1/3/", CadKind.Part, 1, 1m, "Деталь")
        };
        Assert.Throws<OverflowException>(() => ProductCalculator.BuildSpecification(tree));
    }
}
