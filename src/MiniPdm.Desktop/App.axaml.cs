using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MiniPdm.Application;
using MiniPdm.Desktop.Services;
using MiniPdm.Desktop.ViewModels;
using MiniPdm.Desktop.Views;
using MiniPdm.Infrastructure;

namespace MiniPdm.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var database = Environment.GetEnvironmentVariable("MINIPDM_DB");
            if (string.IsNullOrWhiteSpace(database))
                database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniPdm", "mini-pdm.db");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
            var store = new SqliteProductStore(database);
            var importer = new ImportService(new JsonCadDocumentReader(), store);
            var window = new MainWindow();
            var viewModel = new MainViewModel(store, importer, new FolderPicker(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            _ = InitializeDataAsync(store, viewModel);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeDataAsync(SqliteProductStore store, MainViewModel viewModel)
    {
        try
        {
            await store.InitializeAsync();
            await viewModel.InitializeAsync();
        }
        catch (Exception error) { viewModel.StatusText = $"Ошибка запуска: {error.Message}"; }
    }
}
