using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
            activityLifetime.MainViewFactory = () => new MainView();
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewLifetime)
            singleViewLifetime.MainView = new MainView(repository);

        base.OnFrameworkInitializationCompleted();
    }
}
