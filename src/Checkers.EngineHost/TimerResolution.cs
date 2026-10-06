using System.Runtime.InteropServices;

namespace Checkers.EngineHost;

/// <summary>
/// Windows sleeps in 15.6 ms ticks by default, so <c>Thread.Sleep(1)</c> can oversleep a 20 ms
/// tablebase budget by 75%. The search monitor asks for 1 ms ticks instead. Since Windows 10 2004
/// this affects only the calling process, i.e. this worker, and ends when it exits.
/// </summary>
internal static partial class TimerResolution
{
    public static void RequestOneMillisecond()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint periodMilliseconds);
}
