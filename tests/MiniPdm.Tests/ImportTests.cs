using MiniPdm.Application;
using MiniPdm.Domain;

namespace MiniPdm.Tests;

public class ImportTests
{
    private static CadDocument Part(string file, string designation, decimal? mass = 1m) =>
        new(file, CadKind.Part, designation, file, "Сталь", mass, []);

    private static CadDocument Assembly(string file, string designation, params CadComponent[] parts) =>
        new(file, CadKind.Assembly, designation, file, null, null, parts);

    private static CadReadResult Read(CadDocument document) => new(document.FileName, document, null);

    [Fact]
    public void Ошибка_чтения_не_останавливает_остальные_документы()
    {
        var batch = ImportValidator.Validate([new("Ошибка.m3d", null, "Неверный JSON"), Read(Part("Деталь.m3d", "РДЦЛ.304112.001"))]);
        Assert.Single(batch.Accepted);
        Assert.Equal(ImportSeverity.Error, batch.Rows[0].Severity);
        Assert.Equal(ImportSeverity.Success, batch.Rows[1].Severity);
    }

    [Fact]
    public void Дубли_обозначения_отклоняются_все()
    {
        var batch = ImportValidator.Validate([Read(Part("1.m3d", "РДЦЛ.304112.001")), Read(Part("2.m3d", "РДЦЛ.304112.001"))]);
        Assert.Empty(batch.Accepted);
        Assert.All(batch.Rows, row => Assert.Equal(ImportSeverity.Error, row.Severity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Неположительное_количество_отклоняет_сборку(int count)
    {
        var batch = ImportValidator.Validate([Read(Part("Деталь.m3d", "РДЦЛ.304112.001")), Read(Assembly("Узел.a3d", "РДЦЛ.304112.002", new CadComponent("Деталь.m3d", count)))]);
        Assert.DoesNotContain(batch.Accepted, d => d.FileName == "Узел.a3d");
    }

    [Fact]
    public void Отсутствующий_компонент_и_предки_отклоняются_независимо_от_порядка()
    {
        var batch = ImportValidator.Validate([
            Read(Assembly("Верх.a3d", "РДЦЛ.304112.003", new CadComponent("Середина.a3d", 1))),
            Read(Assembly("Середина.a3d", "РДЦЛ.304112.002", new CadComponent("Нет.m3d", 1)))
        ]);
        Assert.Empty(batch.Accepted);
        Assert.All(batch.Rows, row => Assert.Equal(ImportSeverity.Error, row.Severity));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("two")]
    [InlineData("three")]
    public void Циклы_любого_размера_отклоняются(string size)
    {
        var docs = size switch
        {
            "self" => new[] { Assembly("А.a3d", "РДЦЛ.304112.001", new CadComponent("А.a3d", 1)) },
            "two" => new[] { Assembly("А.a3d", "РДЦЛ.304112.001", new CadComponent("Б.a3d", 1)), Assembly("Б.a3d", "РДЦЛ.304112.002", new CadComponent("А.a3d", 1)) },
            _ => new[] { Assembly("А.a3d", "РДЦЛ.304112.001", new CadComponent("Б.a3d", 1)), Assembly("Б.a3d", "РДЦЛ.304112.002", new CadComponent("В.a3d", 1)), Assembly("В.a3d", "РДЦЛ.304112.003", new CadComponent("А.a3d", 1)) }
        };
        var batch = ImportValidator.Validate(docs.Select(Read).ToArray());
        Assert.Empty(batch.Accepted);
        Assert.All(batch.Rows, row => Assert.Contains("Цикл", row.Reason));
    }

    [Fact]
    public void Предок_цикла_тоже_отклоняется()
    {
        var batch = ImportValidator.Validate([
            Read(Assembly("Верх.a3d", "РДЦЛ.304112.003", new CadComponent("А.a3d", 1))),
            Read(Assembly("А.a3d", "РДЦЛ.304112.001", new CadComponent("Б.a3d", 1))),
            Read(Assembly("Б.a3d", "РДЦЛ.304112.002", new CadComponent("А.a3d", 1)))
        ]);
        Assert.Empty(batch.Accepted);
        Assert.Contains(batch.Rows, row => row.FileName == "Верх.a3d" && row.Reason.Contains("отклонён"));
    }

    [Fact]
    public void Деталь_без_массы_сохраняется_с_предупреждением()
    {
        var batch = ImportValidator.Validate([Read(Part("Деталь.m3d", "РДЦЛ.304112.001", null))]);
        Assert.Single(batch.Accepted);
        Assert.Equal(ImportSeverity.Warning, batch.Rows[0].Severity);
    }

    [Fact]
    public async Task Подменный_CAD_reader_не_требует_файловой_системы()
    {
        var reader = new FakeReader([Read(Part("Деталь.m3d", "РДЦЛ.304112.001"))]);
        var store = new FakeStore();
        var report = await new ImportService(reader, store).ImportAsync("фиктивный источник");
        Assert.Equal(1, report.Accepted);
        Assert.Single(store.Documents);
    }


    [Fact]
    public void Переполнение_суммы_повторяющихся_компонентов_отклоняет_сборку()
    {
        var batch = ImportValidator.Validate([
            Read(Part("Деталь.m3d", "РДЦЛ.304112.001")),
            Read(Assembly("Узел.a3d", "РДЦЛ.304112.002",
                new CadComponent("Деталь.m3d", int.MaxValue), new CadComponent("Деталь.m3d", 1)))
        ]);
        Assert.DoesNotContain(batch.Accepted, document => document.FileName == "Узел.a3d");
        Assert.Contains(batch.Rows, row => row.FileName == "Узел.a3d" && row.Reason.Contains("превышает"));
    }

    [Fact]
    public void Стандартные_изделия_с_одинаковым_именем_отклоняются_оба()
    {
        var first = new CadDocument("Первый.m3d", CadKind.StandardPart, null, "Болт", null, 0.1m, []);
        var second = first with { FileName = "Второй.m3d" };
        var batch = ImportValidator.Validate([Read(first), Read(second)]);
        Assert.Empty(batch.Accepted);
        Assert.All(batch.Rows, row => Assert.Equal(ImportSeverity.Error, row.Severity));
    }
    private sealed class FakeReader(IReadOnlyList<CadReadResult> documents) : ICadDocumentReader
    {
        public Task<IReadOnlyList<CadReadResult>> ReadSourceAsync(string source, CancellationToken ct = default) => Task.FromResult(documents);
    }

    private sealed class FakeStore : IImportStore
    {
        public IReadOnlyList<CadDocument> Documents { get; private set; } = [];
        public Task<IReadOnlyDictionary<string, string>> FindIdentityConflictsAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<ApplyStatistics> ApplyAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default)
        {
            Documents = documents;
            return Task.FromResult(new ApplyStatistics(documents.Count, 0, 0));
        }
    }
}
