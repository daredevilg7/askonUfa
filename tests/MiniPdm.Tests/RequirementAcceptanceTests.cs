using MiniPdm.Application;
using MiniPdm.Domain;
using MiniPdm.Infrastructure;

namespace MiniPdm.Tests;

public class RequirementAcceptanceTests
{
    private static CadDocument Part(decimal mass = 0.125m) =>
        new("Деталь.m3d", CadKind.Part, "РДЦЛ.304112.001", "Деталь", "Сталь", mass, []);

    [Theory]
    [InlineData("рдцл.304112.001", "Деталь")]
    [InlineData("дЕТАл", "Деталь")]
    [InlineData("болт м8", "Болт М8")]
    [InlineData("Нет такого объекта", null)]
    public async Task Поиск_работает_по_обозначению_и_имени_без_учёта_регистра(string query, string? expectedName)
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.ApplyAsync([
            Part(), new("Болт.m3d", CadKind.StandardPart, null, "Болт М8", null, 0.05m, [])
        ]);
        var results = await database.Store.SearchAsync(query);
        if (expectedName is null) Assert.Empty(results);
        else Assert.Equal(expectedName, Assert.Single(results).Name);
    }

    [Theory]
    [InlineData(VersionState.InWork, VersionState.InWork)]
    [InlineData(VersionState.Approved, VersionState.InWork)]
    [InlineData(VersionState.Approved, VersionState.Approved)]
    [InlineData(VersionState.Cancelled, VersionState.InWork)]
    [InlineData(VersionState.Cancelled, VersionState.Approved)]
    [InlineData(VersionState.Cancelled, VersionState.Cancelled)]
    public async Task База_запрещает_обратный_или_повторный_переход_и_сохраняет_данные(VersionState from, VersionState to)
    {
        using var database = new TestDatabase();
        var store = database.Store;
        await store.InitializeAsync();
        await store.ApplyAsync([Part()]);
        var id = Assert.Single(await store.SearchAsync("")).ObjectId;
        if (from != VersionState.InWork) await store.ChangeStateAsync(id, from);
        var cardBefore = await store.GetCardAsync(id);
        var versionsBefore = await store.GetVersionsAsync(id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangeStateAsync(id, to));

        Assert.Equal(cardBefore, await store.GetCardAsync(id));
        Assert.Equal(versionsBefore, await store.GetVersionsAsync(id));
    }

    [Fact]
    public async Task Рекурсивный_запрос_перемножает_количества_и_агрегирует_деталь_на_разных_уровнях()
    {
        using var database = new TestDatabase();
        var store = database.Store;
        await store.InitializeAsync();
        var first = new CadDocument("Узел А.a3d", CadKind.Assembly, "РДЦЛ.304112.003", "Узел А", null, null, [new("Деталь.m3d", 4)]);
        var second = new CadDocument("Узел Б.a3d", CadKind.Assembly, "РДЦЛ.304112.004", "Узел Б", null, null, [new("Деталь.m3d", 5)]);
        var root = new CadDocument("Изделие.a3d", CadKind.Assembly, "РДЦЛ.304112.005", "Изделие", null, null,
            [new("Узел А.a3d", 2), new("Узел Б.a3d", 3), new("Деталь.m3d", 1), new("Болт.m3d", 2)]);
        var bolt = new CadDocument("Болт.m3d", CadKind.StandardPart, null, "Болт", null, 0.05m, []);
        await store.ApplyAsync([root, second, bolt, Part(), first]);
        var rootId = Assert.Single(await store.SearchAsync("Изделие")).ObjectId;
        var tree = await store.GetTreeAsync(rootId);

        Assert.Equal(new long?[] { 1, 8, 15 }, tree.Where(node => node.Kind == CadKind.Part).Select(node => node.PathQuantity).Order().ToArray());
        Assert.Equal(new[] { 1, 2, 2 }, tree.Where(node => node.Kind == CadKind.Part).Select(node => node.Depth).Order().ToArray());
        Assert.Equal(3.1m, ProductCalculator.CalculateMass(tree).TotalKg);
        var specification = ProductCalculator.BuildSpecification(tree);
        Assert.Equal(2, specification.Count);
        var partLine = Assert.Single(specification.Where(line => line.Name == "Деталь"));
        Assert.Equal(24, partLine.Quantity);
        Assert.Equal(3m, partLine.TotalMassKg);
        var boltLine = Assert.Single(specification.Where(line => line.Name == "Болт"));
        Assert.Null(boltLine.Designation);
        Assert.Equal(2, boltLine.Quantity);
        Assert.Equal(0.1m, boltLine.TotalMassKg);
    }

    [Fact]
    public async Task Аннулирование_дочерней_версии_восстанавливает_предыдущую_или_исключает_объект_из_расчёта()
    {
        using var database = new TestDatabase();
        var store = database.Store;
        await store.InitializeAsync();
        var root = new CadDocument("Изделие.a3d", CadKind.Assembly, "РДЦЛ.304112.002", "Изделие", null, null, [new("Деталь.m3d", 4)]);
        await store.ApplyAsync([Part(), root]);
        var partId = Assert.Single(await store.SearchAsync("Деталь")).ObjectId;
        var rootId = Assert.Single(await store.SearchAsync("Изделие")).ObjectId;
        await store.ChangeStateAsync(partId, VersionState.Approved);
        await store.ApplyAsync([Part(0.25m), root]);
        Assert.Equal(1m, ProductCalculator.CalculateMass(await store.GetTreeAsync(rootId)).TotalKg);

        await store.ChangeStateAsync(partId, VersionState.Cancelled);
        Assert.Equal(1, (await store.GetCardAsync(partId))!.VersionNo);
        Assert.Equal(0.5m, ProductCalculator.CalculateMass(await store.GetTreeAsync(rootId)).TotalKg);

        await store.ChangeStateAsync(partId, VersionState.Cancelled);
        Assert.Null((await store.GetCardAsync(partId))!.VersionNo);
        var tree = await store.GetTreeAsync(rootId);
        Assert.Single(tree);
        Assert.Equal(0m, ProductCalculator.CalculateMass(tree).TotalKg);
        Assert.Empty(ProductCalculator.BuildSpecification(tree));
    }

    [Fact]
    public async Task Повторное_применение_схемы_не_удаляет_данные()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.ApplyAsync([Part()]);
        await database.Store.InitializeAsync();
        var card = Assert.Single(await database.Store.SearchAsync(""));
        Assert.Equal(1, card.VersionNo);
        Assert.Equal(0.125m, card.MassKg);
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mini-pdm-acceptance-{Guid.NewGuid():N}");
        public SqliteProductStore Store { get; }

        public TestDatabase()
        {
            Directory.CreateDirectory(_folder);
            Store = new SqliteProductStore(Path.Combine(_folder, "test.db"));
        }

        public void Dispose() => Directory.Delete(_folder, true);
    }
}
