using MiniPdm.Application;
using MiniPdm.Desktop.Services;
using MiniPdm.Desktop.ViewModels;
using MiniPdm.Domain;

namespace MiniPdm.Tests;

public class ViewModelTests
{
    [Fact]
    public async Task Выбор_другой_сборки_очищает_прежнюю_спецификацию()
    {
        var catalog = new FakeCatalog();
        var viewModel = new MainViewModel(catalog, new ImportService(new EmptyReader(), new EmptyStore()), new EmptyPicker());
        viewModel.SelectedSearchResult = catalog.Cards[0];
        await viewModel.SpecificationCommand.ExecuteAsync(null);
        Assert.Single(viewModel.SpecificationRows);
        viewModel.SelectedSearchResult = catalog.Cards[1];
        Assert.Equal("Сборка Б", viewModel.SelectedName);
        Assert.Empty(viewModel.SpecificationRows);
        Assert.DoesNotContain("Позиций: 1", viewModel.SpecificationSummary);
    }

    [Fact]
    public async Task Импорт_очищает_спецификацию_даже_если_карточка_корня_не_изменилась()
    {
        var catalog = new FakeCatalog();
        var viewModel = new MainViewModel(catalog, new ImportService(new EmptyReader(), new EmptyStore()), new EmptyPicker());
        viewModel.SelectedSearchResult = catalog.Cards[0];
        await viewModel.SpecificationCommand.ExecuteAsync(null);
        Assert.Single(viewModel.SpecificationRows);
        await viewModel.ImportCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.SpecificationRows);
    }

    private sealed class FakeCatalog : IProductCatalog
    {
        public ProductCard[] Cards { get; } =
        [
            new(1, CadKind.Assembly, "РДЦЛ.304112.001", "Сборка А", 1, VersionState.InWork, null, null),
            new(2, CadKind.Assembly, "РДЦЛ.304112.002", "Сборка Б", 1, VersionState.InWork, null, null)
        ];

        public Task<ProductCard?> GetCardAsync(long objectId, CancellationToken ct = default) =>
            Task.FromResult<ProductCard?>(Cards.Single(card => card.ObjectId == objectId));
        public Task<IReadOnlyList<ProductCard>> SearchAsync(string search, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProductCard>>(Cards);
        public Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(long objectId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>([new(1, VersionState.InWork)]);
        public Task<IReadOnlyList<BomNode>> GetTreeAsync(long objectId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BomNode>>([
                new(objectId, null, objectId, $"/{objectId}/", null, 0, 1, 1, CadKind.Assembly, null, "Сборка", null, null),
                new(3, objectId, 3, $"/{objectId}/3/", $"/{objectId}/", 1, 1, 1, CadKind.Part, "РДЦЛ.304112.003", "Деталь", "Сталь", 1m)
            ]);
        public Task ChangeStateAsync(long objectId, VersionState target, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyReader : ICadDocumentReader
    {
        public Task<IReadOnlyList<CadReadResult>> ReadSourceAsync(string source, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CadReadResult>>([]);
    }

    private sealed class EmptyStore : IImportStore
    {
        public Task<IReadOnlyDictionary<string, string>> FindIdentityConflictsAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<ApplyStatistics> ApplyAsync(IReadOnlyList<CadDocument> documents, CancellationToken ct = default) =>
            Task.FromResult(new ApplyStatistics(0, 0, 0));
    }

    private sealed class EmptyPicker : IFolderPicker
    {
        public Task<string?> PickAsync() => Task.FromResult<string?>("подменный источник");
    }
}
