using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Chatap.App.ViewModels;
using Chatap.App.Views;

namespace Chatap.App;

public partial class App : Application
{
    // Set by Program.cs before Avalonia starts.
    public static AppServices? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is { } services)
        {
            // Subscribe first, then start the node: no incoming conversation can be missed.
            var main = new MainWindowViewModel(services);
            services.Node.Start();
            main.ShowOwnAddress();

            desktop.MainWindow = new MainWindow { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
