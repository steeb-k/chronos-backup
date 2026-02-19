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
using Chronos.Common.Helpers;

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
        System.Diagnostics.Debug.WriteLine("[Chronos] App() ctor start");
        try
        {
            ConfigureLogging();
            System.Diagnostics.Debug.WriteLine("[Chronos] ConfigureLogging OK");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "chronos-startup.log"),
                $"\nApp.ConfigureLogging FAILED: {ex}\n");
            // Continue without Serilog
        }

        try
        {
            this.InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[Chronos] InitializeComponent OK");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "chronos-startup.log"),
                $"\nApp.InitializeComponent FAILED: {ex}\n");
            throw;
        }

        try
        {
            Services = ConfigureServices();
            System.Diagnostics.Debug.WriteLine("[Chronos] ConfigureServices OK");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "chronos-startup.log"),
                $"\nApp.ConfigureServices FAILED: {ex}\n");
            throw;
        }
    }

    private static void ConfigureLogging()
    {
        var appDataDir = PeEnvironment.GetAppDataDirectory();
        var logDir = Path.Combine(appDataDir, "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "chronos-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("Chronos started. Log file: {LogPath}", logPath);

        if (PeEnvironment.IsWinPE)
        {
            Log.Information("WinPE environment detected. Capabilities: {Caps}", PeEnvironment.Capabilities);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Program.Log("");
        Program.Log("=== OnLaunched entered ===");
        Program.FlushLog();

        try
        {
            Program.Log("  Creating MainWindow...");
            Program.FlushLog();
            _window = new MainWindow();
            MainWindow = _window;
            Program.Log("  MainWindow created OK");
            Program.FlushLog();

            // Hook up window closing event to dismount VHDXs
            _window.Closed += OnMainWindowClosed;

            Program.Log("  Calling _window.Activate()...");
            Program.FlushLog();
            _window.Activate();
            Program.Log("  Window activated OK");
            Program.FlushLog();
        }
        catch (Exception ex)
        {
            Program.Log("");
            Program.Log("=== OnLaunched FAILED ===");
            Program.Log(ex.ToString());
            Program.FlushLog();
            throw;
        }

        // Apply saved theme to the root element
        try
        {
            ApplyStartupTheme();
            Program.Log("  ApplyStartupTheme OK");
        }
        catch (Exception ex)
        {
            Program.Log("  ApplyStartupTheme FAILED: " + ex.Message);
        }
        Program.FlushLog();

        // Skip update checks in PE environments (no network, no persistent install)
        if (!PeEnvironment.IsWinPE)
        {
            _ = CheckForUpdatesOnStartupAsync();
        }

        Program.Log("  OnLaunched complete");
        Program.FlushLog();
    }

    private static async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            // Wait a bit so the UI loads first
            await Task.Delay(3000);

            var updateService = Services.GetService<IUpdateService>();
            if (updateService is null)
                return;

            var updateAvailable = await updateService.CheckForUpdatesAsync();
            if (!updateAvailable)
                return;

            Log.Information("Update available: {Version}", updateService.LatestVersion);

            // Show dialog on UI thread
            MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Update Available",
                        Content = $"Chronos {updateService.LatestVersion} is available (you have {updateService.CurrentVersion}).\n\nWould you like to download and install it now?",
                        PrimaryButtonText = "Download & Install",
                        CloseButtonText = "Later",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = MainWindow.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        await DownloadAndInstallUpdateAsync(updateService);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to show startup update dialog");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup update check failed");
        }
    }

    private static async Task DownloadAndInstallUpdateAsync(IUpdateService updateService)
    {
        ContentDialog? progressDialog = null;
        try
        {
            var progressBar = new Microsoft.UI.Xaml.Controls.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 300
            };
            var statusText = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Downloading installer...",
                Margin = new Thickness(0, 8, 0, 0)
            };
            var panel = new Microsoft.UI.Xaml.Controls.StackPanel();
            panel.Children.Add(progressBar);
            panel.Children.Add(statusText);

            progressDialog = new ContentDialog
            {
                Title = "Downloading Update",
                Content = panel,
                CloseButtonText = "Cancel",
                XamlRoot = MainWindow.Content.XamlRoot
            };

            var cts = new CancellationTokenSource();
            progressDialog.CloseButtonClick += (_, _) => cts.Cancel();

            // Show dialog and start download concurrently
            var dialogTask = progressDialog.ShowAsync().AsTask();

            var progress = new Progress<int>(percent =>
            {
                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    progressBar.Value = percent;
                    statusText.Text = $"Downloading installer... {percent}%";
                });
            });

            var installerPath = await updateService.DownloadInstallerAsync(progress);

            if (cts.Token.IsCancellationRequested)
                return;

            if (string.IsNullOrEmpty(installerPath))
            {
                progressDialog.Hide();
                var errorDialog = new ContentDialog
                {
                    Title = "Download Failed",
                    Content = "The installer could not be downloaded. Please try again from Options.",
                    CloseButtonText = "OK",
                    XamlRoot = MainWindow.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            progressDialog.Hide();
            updateService.LaunchInstallerAndExit(installerPath);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Update download cancelled by user");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download/install update from startup dialog");
            progressDialog?.Hide();
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
