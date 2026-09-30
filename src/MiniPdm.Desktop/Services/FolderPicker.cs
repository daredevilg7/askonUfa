using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MiniPdm.Desktop.Services;

public interface IFolderPicker
{
    Task<string?> PickAsync();
}

public sealed class FolderPicker(Window window) : IFolderPicker
{
    public async Task<string?> PickAsync()
    {
        var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку CAD-документов",
            AllowMultiple = false
        });
        return result.Count == 0 ? null : result[0].Path.LocalPath;
    }
}
