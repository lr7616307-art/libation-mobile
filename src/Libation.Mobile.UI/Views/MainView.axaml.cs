using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
