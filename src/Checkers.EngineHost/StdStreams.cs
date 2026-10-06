using System.Runtime.InteropServices;

namespace Checkers.EngineHost;

/// <summary>
/// Keeps the protocol channel clean. stdout carries the JSON protocol, but a native engine DLL may
/// printf diagnostics. Before any engine is loaded the original stdout handle is captured for the
/// protocol and the process-wide stdout handle is pointed at stderr, so anything the DLL's C runtime
/// prints lands in the API's log instead of corrupting a response.
/// </summary>
internal static partial class StdStreams
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    public static Stream TakeProtocolOutput()
    {
        var protocol = Console.OpenStandardOutput();
        if (OperatingSystem.IsWindows())
        {
            SetStdHandle(StdOutputHandle, GetStdHandle(StdErrorHandle));
        }

        return protocol;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int stdHandle, nint handle);
}
