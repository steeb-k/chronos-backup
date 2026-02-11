using System.Runtime.InteropServices;
using Serilog;

namespace Chronos.App.Services;

/// <summary>
/// Prevents the system from entering sleep/suspend while long-running operations are in progress.
/// Uses the Win32 SetThreadExecutionState API.
/// </summary>
internal static class PowerManagement
{
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    /// <summary>
    /// Prevents the system from sleeping. Call <see cref="AllowSleep"/> when the operation completes.
    /// </summary>
    public static void PreventSleep()
    {
        var result = SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS |
            EXECUTION_STATE.ES_SYSTEM_REQUIRED |
            EXECUTION_STATE.ES_AWAYMODE_REQUIRED);

        if (result == 0)
            Log.Warning("SetThreadExecutionState failed to prevent sleep");
        else
            Log.Debug("System sleep prevention enabled");
    }

    /// <summary>
    /// Re-allows the system to sleep normally.
    /// </summary>
    public static void AllowSleep()
    {
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        Log.Debug("System sleep prevention disabled");
    }
}
