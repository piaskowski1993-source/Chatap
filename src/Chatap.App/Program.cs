using Avalonia;
using Chatap.Core;
using System;

namespace Chatap.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Composition root: prepare the Core parts before the window opens.
        string dataFolder = DataFolder.FromArgs(args);
        Guid identity = IdentityStore.LoadOrCreate(dataFolder);

        var log = new LogWriter(dataFolder);
        // The window shows log and store failures too; the terminal keeps a copy.
        log.LogFailed += error => Console.Error.WriteLine($"Chatap continues without a log: {error.Message}");
        log.Log($"started, identity {identity}, data folder {dataFolder}");

        var store = new ConversationStore(dataFolder);
        store.StoreFailed += error => Console.Error.WriteLine($"Chatap continues without saving messages: {error.Message}");

        // The window subscribes to the node and then starts it (App.axaml.cs).
        var node = new Node(identity, log, store);
        App.Services = new AppServices(identity, node, store, log);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            node.Dispose();
            // Graceful shutdown: write the remaining messages and log lines before the process ends.
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            log.Log("stopped");
            log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
