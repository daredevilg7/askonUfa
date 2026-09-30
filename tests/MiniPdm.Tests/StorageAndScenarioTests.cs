using Microsoft.Data.Sqlite;
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
    public async Task Неверный_UTF8_не_прерывает_чтение_папки()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mini-pdm-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "А.m3d"), [0xC3, 0x28]);
            await File.WriteAllTextAsync(Path.Combine(folder, "Б.m3d"), """
                {"formatVersion":1,"fileName":"Б.m3d","type":"Part","designation":"РДЦЛ.304112.001",
                 "name":"Деталь","properties":{"material":"Сталь","mass":1},"components":[]}
                """);
            var rows = await new JsonCadDocumentReader().ReadSourceAsync(folder);
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0].Document);
            Assert.NotNull(rows[0].Error);
            Assert.NotNull(rows[1].Document);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Конфликт_типа_в_базе_отклоняет_файл_и_предков_но_сохраняет_корректные()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        await store.ApplyAsync([Part()]);
        var conflict = new CadDocument("Конфликт.a3d", CadKind.Assembly, "РДЦЛ.304112.001", "Конфликт", null, null, []);
        var ancestor = new CadDocument("Верх.a3d", CadKind.Assembly, "РДЦЛ.304112.004", "Верх", null, null, [new("Конфликт.a3d", 1)]);
        var valid = Part() with { FileName = "Правильная.m3d", Name = "Правильная", Designation = "РДЦЛ.304112.003" };
        var report = await new ImportService(new InlineReader([conflict, ancestor, valid]), store).ImportAsync("подменный CAD");
        Assert.Equal(1, report.Accepted);
        Assert.Equal(2, report.Rejected);
        Assert.Contains(report.Rows, row => row.FileName == conflict.FileName && row.Reason.Contains("Тип"));
        Assert.Contains(report.Rows, row => row.FileName == ancestor.FileName && row.Reason.Contains("отклонён"));
        Assert.Equal(2, (await store.SearchAsync("")).Count);
    }

    private sealed class InlineReader(IReadOnlyList<CadDocument> documents) : ICadDocumentReader
    {
        public Task<IReadOnlyList<CadReadResult>> ReadSourceAsync(string source, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CadReadResult>>(documents.Select(document => new CadReadResult(document.FileName, document, null)).ToArray());
    }

    [Fact]
    public async Task Цикл_после_восстановления_старой_версии_не_даёт_неполную_массу()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        var first = new CadDocument("А.a3d", CadKind.Assembly, "РДЦЛ.304112.001", "Сборка А", null, null, [new("Б.a3d", 1)]);
        var second = new CadDocument("Б.a3d", CadKind.Assembly, "РДЦЛ.304112.002", "Сборка Б", null, null, []);
        await store.ApplyAsync([first, second]);
        var firstId = (await store.SearchAsync("Сборка А")).Single().ObjectId;
        var secondId = (await store.SearchAsync("Сборка Б")).Single().ObjectId;
        await store.ChangeStateAsync(firstId, VersionState.Approved);
        await store.ChangeStateAsync(secondId, VersionState.Approved);
        await store.ApplyAsync([first with { Components = [] }, second with { Components = [new("А.a3d", 1)] }]);
        await store.ChangeStateAsync(firstId, VersionState.Cancelled);
        var tree = await store.GetTreeAsync(firstId);
        Assert.Throws<InvalidOperationException>(() => ProductCalculator.CalculateMass(tree));
        Assert.Throws<InvalidOperationException>(() => ProductCalculator.BuildSpecification(tree));
    }

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
        var database = TempDatabase();
        var store = new SqliteProductStore(database);
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
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT mass_kg FROM object_version WHERE object_id = $id AND version_no = 1";
            command.Parameters.AddWithValue("$id", changed.ObjectId);
            Assert.Equal("5.86", (string?)await command.ExecuteScalarAsync());
        }
        var v2Tree = await store.GetTreeAsync(rootId);
        Assert.Equal(98.8800m, ProductCalculator.CalculateMass(v2Tree).TotalKg);
        var v2Spec = ProductCalculator.BuildSpecification(v2Tree);
        Assert.Equal(122, v2Spec.Sum(row => row.Quantity));
        Assert.Equal(16, v2Spec.Single(row => row.Name == "Прокладка регулировочная").Quantity);
    }
    [Fact]
    public async Task После_отмены_всех_версий_повторный_импорт_создаёт_следующий_номер()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        await store.ApplyAsync([Part()]);
        var id = (await store.SearchAsync("Деталь")).Single().ObjectId;
        await store.ChangeStateAsync(id, VersionState.Cancelled);
        Assert.Null((await store.GetCardAsync(id))!.VersionNo);
        Assert.Empty(await store.GetTreeAsync(id));
        await store.ApplyAsync([Part(2m)]);
        Assert.Equal(2, (await store.GetCardAsync(id))!.VersionNo);
        Assert.Equal(VersionState.Cancelled, (await store.GetVersionsAsync(id))[0].State);
    }

    [Fact]
    public async Task Порядок_строк_BOM_не_создаёт_новую_версию()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        var standard = new CadDocument("Болт.m3d", CadKind.StandardPart, null, "Болт", null, 0.1m, []);
        await store.ApplyAsync([Part(), standard, Assembly(new CadComponent("Деталь.m3d", 2), new CadComponent("Болт.m3d", 3))]);
        var result = await store.ApplyAsync([standard, Part(), Assembly(new CadComponent("Болт.m3d", 3), new CadComponent("Деталь.m3d", 2))]);
        Assert.Equal(3, result.Unchanged);
    }

    [Fact]
    public async Task Изменённая_версия_в_работе_обновляется_без_нового_номера()
    {
        var store = new SqliteProductStore(TempDatabase());
        await store.InitializeAsync();
        await store.ApplyAsync([Part()]);
        var id = (await store.SearchAsync("Деталь")).Single().ObjectId;
        var result = await store.ApplyAsync([Part(2m)]);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, (await store.GetCardAsync(id))!.VersionNo);
        Assert.Equal(2m, (await store.GetCardAsync(id))!.MassKg);
    }

}
