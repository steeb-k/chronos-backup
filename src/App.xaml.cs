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
        
        // Hook up window closing event to dismount VHDXs
        _window.Closed += OnMainWindowClosed;
        
        _window.Activate();

        // Apply saved theme to the root element
        ApplyStartupTheme();

        // Check for updates in the background after a short delay
        _ = CheckForUpdatesOnStartupAsync();
    }

    private static async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            // Wait a bit so the UI loads first
            await Task.Delay(3000);

            var updateService = Services.GetService<IUpdateService>();
            if (updateService is not null)
            {
                var updateAvailable = await updateService.CheckForUpdatesAsync();
                if (updateAvailable)
                {
                    Log.Information("Update available: {Version}", updateService.LatestVersion);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup update check failed");
        }
    }

    private static void ApplyStartupTheme()
    {
        try
        {
            var settings = Services.GetService<ISettingsService>();
            if (settings is null) return;

            var theme = settings.ThemeMode switch
            {
                1 => Microsoft.UI.Xaml.ElementTheme.Light,
                2 => Microsoft.UI.Xaml.ElementTheme.Dark,
                _ => Microsoft.UI.Xaml.ElementTheme.Default
            };

            if (MainWindow?.Content is FrameworkElement root)
                root.RequestedTheme = theme;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply startup theme");
        }
    }

    private void OnMainWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        Log.Information("Application closing - dismounting all VHDXs");
        
        // Get the VirtualDiskService and dismount all VHDXs
        var virtualDiskService = Services.GetService<IVirtualDiskService>();
        if (virtualDiskService is VirtualDiskService vds)
        {
            vds.DismountAll();
        }

        Log.Information("Application closed");
        Log.CloseAndFlush();
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
        services.AddSingleton<IDiskPreparationService, DiskPreparationService>();
        services.AddSingleton<IVirtualDiskService, VirtualDiskService>();
        services.AddSingleton<ICompressionProvider, ZstdCompressionProvider>();
        services.AddSingleton<IBackupEngine, BackupEngine>();
        services.AddSingleton<IRestoreEngine, RestoreEngine>();
        services.AddSingleton<IVerificationEngine, VerificationEngine>();
        services.AddSingleton<IBackupOperationsService, BackupOperationsService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IOperationHistoryService, OperationHistoryService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddTransient<CloneViewModel>();
        services.AddTransient<RestoreViewModel>();
        services.AddTransient<VerifyViewModel>();
        services.AddTransient<BrowseViewModel>();
        services.AddTransient<OptionsViewModel>();
        services.AddTransient<HistoryViewModel>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        return services.BuildServiceProvider();
    }
}
