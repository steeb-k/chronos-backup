using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using WinRT.Interop;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Core.Services;
using Chronos.Core.Disk;
using Chronos.Core.VirtualDisk;
using Chronos.Core.Compression;
using Chronos.Core.Imaging;
using Chronos.Core.VSS;

namespace Chronos.App;

public partial class App : Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;
    public static Window MainWindow { get; private set; } = null!;
    public static IntPtr MainWindowHandle => WindowNative.GetWindowHandle(MainWindow);

    public static Task<T?> RunOnUIThreadAsync<T>(Func<Task<T?>> action)
    {
        var tcs = new TaskCompletionSource<T?>();
        var queue = MainWindow.DispatcherQueue;

        if (!queue.TryEnqueue(async () =>
        {
            try
            {
                var result = await action();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue UI action."));
        }

        return tcs.Task;
    }

    public App()
    {
        ConfigureLogging();
        this.InitializeComponent();
        Services = ConfigureServices();
    }

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chronos", "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "chronos-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        Log.Information("Chronos started. Log file: {LogPath}", logPath);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<IDiskEnumerator, DiskEnumerator>();
        services.AddSingleton<IAllocatedRangesProvider, AllocatedRangesProvider>();
        services.AddSingleton<IVssService, VssService>();
        services.AddSingleton<IDiskReader, DiskReader>();
        services.AddSingleton<IDiskWriter, DiskWriter>();
        services.AddSingleton<IVirtualDiskService, VirtualDiskService>();
        services.AddSingleton<ICompressionProvider, ZstdCompressionProvider>();
        services.AddSingleton<IBackupEngine, BackupEngine>();
        services.AddSingleton<IVerificationEngine, VerificationEngine>();
        services.AddSingleton<IBackupOperationsService, BackupOperationsService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddTransient<RestoreViewModel>();
        services.AddTransient<VerifyViewModel>();
        services.AddTransient<BrowseViewModel>();
        services.AddTransient<OptionsViewModel>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        return services.BuildServiceProvider();
    }
}
