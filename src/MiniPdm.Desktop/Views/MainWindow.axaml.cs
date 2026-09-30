using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MiniPdm.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
