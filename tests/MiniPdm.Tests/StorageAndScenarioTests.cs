using MiniPdm.Application;
using MiniPdm.Domain;
using MiniPdm.Infrastructure;

namespace MiniPdm.Tests;

public class StorageAndScenarioTests
{
    private static string TempDatabase() => Path.Combine(Path.GetTempPath(), $"mini-pdm-{Guid.NewGuid():N}.db");
    private static string DataPath(string set) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sample-data", set));
    private static CadDocument Part(decimal? mass = 1m) => new("Деталь.m3d", CadKind.Part, "РДЦЛ.304112.001", "Деталь", "Сталь", mass, []);
    private static CadDocument Assembly(params CadComponent[] children) => new("Сборка.a3d", CadKind.Assembly, "РДЦЛ.304112.002", "Сборка", null, null, children);

    [Fact]
    public async Task Чтение_папки_изолирует_повреждённый_JSON()
    {
        var rows = await new JsonCadDocumentReader().ReadSourceAsync(DataPath("cad-export"));
        Assert.Equal(45, rows.Count);
        Assert.Single(rows.Where(r => r.Document is null));
        Assert.Contains(rows, r => r.FileName == "Отдушина.m3d" && r.Error is not null);
    }

    [Fact]
    public async Task Повторный_импорт_соблюдает_состояние_версии_и_не_реагирует_на_порядок_связей()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        var first = new[] { Part(), Assembly(new CadComponent("Деталь.m3d", 1), new CadComponent("Деталь.m3d", 2)) };
        Assert.Equal(2, (await store.ApplyAsync(first)).Created);
        Assert.Equal(2, (await store.ApplyAsync(first.Reverse().ToArray())).Unchanged);
        var assemblyId = (await store.SearchAsync("Сборка")).Single().ObjectId;
        Assert.Equal(3, (await store.GetTreeAsync(assemblyId)).Last().PathQuantity);
        Assert.Equal(1, (await store.GetCardAsync(assemblyId))!.VersionNo);
        await store.ChangeStateAsync(assemblyId, VersionState.Approved);
        Assert.Equal(2, (await store.ApplyAsync(first)).Unchanged);
        await store.ApplyAsync([Part(), Assembly(new CadComponent("Деталь.m3d", 4))]);
        var card = await store.GetCardAsync(assemblyId);
        Assert.Equal(2, card!.VersionNo);
        Assert.Equal(VersionState.InWork, card.State);
        Assert.Equal(VersionState.Approved, (await store.GetVersionsAsync(assemblyId))[0].State);
        await store.ChangeStateAsync(assemblyId, VersionState.Cancelled);
        Assert.Equal(1, (await store.GetCardAsync(assemblyId))!.VersionNo);
    }

    [Fact]
    public async Task Ошибка_сохранения_откатывает_всю_принятую_часть()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => store.ApplyAsync([Part(), Assembly(new CadComponent("Нет.m3d", 1))]));
        Assert.Empty(await store.SearchAsync(""));
    }

    [Fact]
    public async Task Базовый_набор_и_v2_дают_контрольные_значения()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        var importer = new ImportService(new JsonCadDocumentReader(), store);
        var baseReport = await importer.ImportAsync(DataPath("cad-export"));
        Assert.Equal(35, baseReport.Accepted);
        Assert.Equal(10, baseReport.Rejected);
        Assert.Equal(1, baseReport.Warnings);
        var rootId = (await store.SearchAsync("Редуктор цилиндрический Ц2-160")).Single().ObjectId;
        var tree = await store.GetTreeAsync(rootId);
        Assert.Equal(98.8040m, ProductCalculator.CalculateMass(tree).TotalKg);
        var spec = ProductCalculator.BuildSpecification(tree);
        Assert.Equal(25, spec.Count);
        Assert.Equal(118, spec.Sum(row => row.Quantity));
        Assert.Equal(12, spec.Single(row => row.Name == "Прокладка регулировочная").Quantity);
        var indicatorId = (await store.SearchAsync("Маслоуказатель в сборе")).Single().ObjectId;
        var missing = ProductCalculator.CalculateMass(await store.GetTreeAsync(indicatorId));
        Assert.Null(missing.TotalKg);
        Assert.Contains(missing.MissingComponents, name => name.Contains("Прокладка маслоуказателя"));
        foreach (var card in await store.SearchAsync("")) await store.ChangeStateAsync(card.ObjectId, VersionState.Approved);
        var report2 = await importer.ImportAsync(DataPath("cad-export-v2"));
        Assert.Equal(2, report2.Changes.Created);
        Assert.Equal(33, report2.Changes.Unchanged);
        var changed = (await store.SearchAsync("Колесо зубчатое")).Single();
        Assert.Equal(2, (await store.GetCardAsync(changed.ObjectId))!.VersionNo);
        var v2Tree = await store.GetTreeAsync(rootId);
        Assert.Equal(98.8800m, ProductCalculator.CalculateMass(v2Tree).TotalKg);
        var v2Spec = ProductCalculator.BuildSpecification(v2Tree);
        Assert.Equal(122, v2Spec.Sum(row => row.Quantity));
        Assert.Equal(16, v2Spec.Single(row => row.Name == "Прокладка регулировочная").Quantity);
    }
}
