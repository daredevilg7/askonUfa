using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniPdm.Application;
using MiniPdm.Desktop.Services;
using MiniPdm.Domain;

namespace MiniPdm.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IProductCatalog _store;
    private readonly ImportService _importer;
    private readonly IFolderPicker _folderPicker;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ProductCard? _selectedSearchResult;
    [ObservableProperty] private TreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private ProductCard? _selectedCard;
    [ObservableProperty] private string _statusText = "Подготовка базы данных…";
    [ObservableProperty] private string _massText = "Выберите сборку для расчёта массы";
    [ObservableProperty] private string _reportSummary = "Импорт ещё не запускался";
    [ObservableProperty] private string _specificationSummary = "Выберите сборку и нажмите «Сводная спецификация»";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<ProductCard> SearchResults { get; } = [];
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];
    public ObservableCollection<ReportRowViewModel> ReportRows { get; } = [];
    public ObservableCollection<SpecificationRowViewModel> SpecificationRows { get; } = [];

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand MassCommand { get; }
    public IAsyncRelayCommand SpecificationCommand { get; }
    public IAsyncRelayCommand ApproveCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }

    public MainViewModel(IProductCatalog store, ImportService importer, IFolderPicker folderPicker)
    {
        _store = store;
        _importer = importer;
        _folderPicker = folderPicker;
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        MassCommand = new AsyncRelayCommand(ShowMassAsync);
        SpecificationCommand = new AsyncRelayCommand(ShowSpecificationAsync);
        ApproveCommand = new AsyncRelayCommand(() => ChangeStateAsync(VersionState.Approved));
        CancelCommand = new AsyncRelayCommand(() => ChangeStateAsync(VersionState.Cancelled));
    }

    public string SelectedName => SelectedCard?.Name ?? "Выберите объект";
    public string SelectedDesignation => SelectedCard?.Designation ?? "—";
    public string SelectedKind => SelectedCard?.Kind switch
    {
        CadKind.Assembly => "Сборка", CadKind.Part => "Деталь", CadKind.StandardPart => "Стандартное изделие", _ => "—"
    };
    public string SelectedVersion => SelectedCard?.VersionNo?.ToString() ?? "—";
    public string SelectedState => SelectedCard?.State switch
    {
        VersionState.InWork => "В работе", VersionState.Approved => "Утверждено", VersionState.Cancelled => "Аннулировано", _ => "Нет текущей версии"
    };
    public string SelectedMaterial => SelectedCard?.Material ?? "—";
    public string SelectedUnitMass => SelectedCard?.MassKg?.ToString("0.#### кг") ?? "—";
    public bool IsAssembly => SelectedCard?.Kind == CadKind.Assembly && SelectedCard.State is not null;
    public bool CanApprove => SelectedCard?.State == VersionState.InWork;
    public bool CanCancel => SelectedCard?.State is VersionState.InWork or VersionState.Approved;

    partial void OnSelectedCardChanged(ProductCard? value)
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedDesignation));
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(SelectedMaterial));
        OnPropertyChanged(nameof(SelectedUnitMass));
        OnPropertyChanged(nameof(IsAssembly));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnSelectedSearchResultChanged(ProductCard? value)
    {
        if (value is not null) _ = SelectAsync(value.ObjectId, true);
    }

    partial void OnSelectedTreeNodeChanged(TreeNodeViewModel? value)
    {
        if (value is not null) _ = SelectAsync(value.Data.ObjectId, false);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await SearchAsync();
            StatusText = "База готова. Выберите папку CAD-документов для импорта.";
        }
        catch (Exception error) { StatusText = $"Ошибка запуска: {error.Message}"; }
    }

    private async Task SearchAsync()
    {
        try
        {
            var items = await _store.SearchAsync(SearchText);
            SearchResults.Clear();
            foreach (var item in items) SearchResults.Add(item);
            StatusText = $"Найдено объектов: {items.Count}";
        }
        catch (Exception error) { StatusText = $"Ошибка поиска: {error.Message}"; }
    }

    private async Task SelectAsync(long objectId, bool replaceTree)
    {
        try
        {
            SelectedCard = await _store.GetCardAsync(objectId);
            MassText = "Нажмите «Рассчитать массу»";
            if (replaceTree)
            {
                Roots.Clear();
                if (IsAssembly)
                {
                    var tree = await _store.GetTreeAsync(objectId);
                    var nodes = new Dictionary<string, TreeNodeViewModel>(StringComparer.Ordinal);
                    foreach (var item in tree)
                    {
                        var node = new TreeNodeViewModel(item);
                        nodes.Add(item.Path, node);
                        if (item.ParentPath is null) Roots.Add(node);
                        else if (nodes.TryGetValue(item.ParentPath, out var parent)) parent.Children.Add(node);
                    }
                }
            }
            SelectedTabIndex = 0;
        }
        catch (Exception error) { StatusText = $"Ошибка загрузки объекта: {error.Message}"; }
    }

    private async Task ImportAsync()
    {
        if (IsBusy) return;
        var folder = await _folderPicker.PickAsync();
        if (folder is null) return;
        IsBusy = true;
        StatusText = "Импорт выполняется…";
        try
        {
            var report = await _importer.ImportAsync(folder);
            ReportRows.Clear();
            foreach (var row in report.Rows) ReportRows.Add(ReportRowViewModel.From(row));
            ReportSummary = $"Принято: {report.Accepted} (с предупреждением: {report.Warnings}) · Отклонено: {report.Rejected} · Новых версий: {report.Changes.Created} · Обновлено: {report.Changes.Updated} · Без изменений: {report.Changes.Unchanged}";
            await SearchAsync();
            if (SelectedCard is not null) await SelectAsync(SelectedCard.ObjectId, true);
            SelectedTabIndex = 1;
            StatusText = $"Импорт завершён: {report.Accepted} принято, {report.Rejected} отклонено";
        }
        catch (Exception error) { StatusText = $"Импорт не завершён: {error.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task ShowMassAsync()
    {
        if (!IsAssembly || SelectedCard is null) return;
        try
        {
            var result = ProductCalculator.CalculateMass(await _store.GetTreeAsync(SelectedCard.ObjectId));
            MassText = result.TotalKg is null
                ? "Масса недоступна. Без массы: " + string.Join(", ", result.MissingComponents)
                : $"Масса сборки: {result.TotalKg:0.####} кг";
            SelectedTabIndex = 0;
        }
        catch (Exception error) { StatusText = $"Ошибка расчёта массы: {error.Message}"; }
    }

    private async Task ShowSpecificationAsync()
    {
        if (!IsAssembly || SelectedCard is null) return;
        try
        {
            var rows = ProductCalculator.BuildSpecification(await _store.GetTreeAsync(SelectedCard.ObjectId));
            SpecificationRows.Clear();
            foreach (var row in rows) SpecificationRows.Add(SpecificationRowViewModel.From(row));
            SpecificationSummary = $"Позиций: {rows.Count} · Всего изделий: {rows.Sum(row => row.Quantity)}";
            SelectedTabIndex = 2;
        }
        catch (Exception error) { StatusText = $"Ошибка спецификации: {error.Message}"; }
    }

    private async Task ChangeStateAsync(VersionState state)
    {
        if (SelectedCard is null) return;
        try
        {
            var id = SelectedCard.ObjectId;
            await _store.ChangeStateAsync(id, state);
            await SelectAsync(id, true);
            await SearchAsync();
            StatusText = "Состояние версии изменено";
        }
        catch (Exception error) { StatusText = $"Ошибка смены состояния: {error.Message}"; }
    }
}
