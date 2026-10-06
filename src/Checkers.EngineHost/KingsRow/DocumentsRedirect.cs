using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Checkers.EngineHost.KingsRow;

/// <summary>
/// Points KingsRow's "Documents" folder, where it writes its log, at a folder of our choosing.
/// </summary>
/// <remarks>
/// KingsRow asks <c>SHGetFolderPathA(CSIDL_PERSONAL)</c> for Documents, creates
/// <c>Ed Gilbert\Kingsrow</c> under it and opens its log there on the first engine command; if that
/// fails it ends the whole process with a C runtime fast-fail (0xC0000409). An IIS app pool identity
/// has no Documents folder Windows will return, even with its user profile loaded, so every worker
/// died silently. Rewriting that one import in the loaded DLL's import address table answers
/// CSIDL_PERSONAL with the given folder and passes every other request through to Windows.
/// Nothing outside this process changes.
/// </remarks>
internal static unsafe partial class DocumentsRedirect
{
    private const int CsidlPersonal = 0x0005;
    private const int CsidlMask = 0x00FF;
    private const int MaxPath = 260;
    private const uint PageReadWrite = 0x04;

    private static byte[] s_folder = [];
    private static delegate* unmanaged[Stdcall]<nint, int, nint, uint, byte*, int> s_original;

    /// <summary>Rewrites the SHELL32 import of <paramref name="module"/>; call before its first command.</summary>
    /// <param name="folder">An existing folder with an ASCII path (the import is the 8-bit "A" variant).</param>
    public static void Install(nint module, string folder)
    {
        if (!Ascii.IsValid(folder) || folder.Length >= MaxPath)
        {
            throw new InvalidOperationException($"KingsRow's log folder must be an ASCII path shorter than {MaxPath}: '{folder}'.");
        }

        var entry = FindImport(module, "SHELL32.dll", "SHGetFolderPathA");
        if (entry is null)
        {
            throw new InvalidOperationException("Kingsrow64.dll does not import SHGetFolderPathA; cannot place its log.");
        }

        s_folder = [.. Encoding.ASCII.GetBytes(folder), 0];
        s_original = (delegate* unmanaged[Stdcall]<nint, int, nint, uint, byte*, int>)*entry;

        if (!VirtualProtect(entry, (nuint)sizeof(nint), PageReadWrite, out var previous))
        {
            throw new InvalidOperationException($"VirtualProtect failed ({Marshal.GetLastPInvokeError()}).");
        }

        *entry = (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, uint, byte*, int>)&GetFolderPath;
        VirtualProtect(entry, (nuint)sizeof(nint), previous, out _);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFolderPath(nint window, int folder, nint token, uint flags, byte* path)
    {
        if ((folder & CsidlMask) != CsidlPersonal)
        {
            return s_original(window, folder, token, flags, path);
        }

        s_folder.AsSpan().CopyTo(new Span<byte>(path, MaxPath));
        return 0; // S_OK
    }

    /// <summary>The import address table slot for <paramref name="function"/> imported from <paramref name="dll"/> by name.</summary>
    internal static nint* FindImport(nint module, string dll, string function)
    {
        var image = (byte*)module;
        var ntHeaders = image + *(int*)(image + 0x3C);
        const int OptionalHeaderOffset = 24;
        const int ImportDirectoryOffset = 120; // DataDirectory[1] in a PE32+ optional header
        if (*(ushort*)(ntHeaders + OptionalHeaderOffset) != 0x20B)
        {
            throw new InvalidOperationException("Expected a 64-bit (PE32+) module.");
        }

        var importRva = *(uint*)(ntHeaders + OptionalHeaderOffset + ImportDirectoryOffset);
        for (var descriptor = image + importRva; *(uint*)(descriptor + 12) != 0; descriptor += 20)
        {
            var name = Marshal.PtrToStringAnsi((nint)(image + *(uint*)(descriptor + 12)));
            if (!string.Equals(name, dll, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var names = (ulong*)(image + *(uint*)descriptor);           // OriginalFirstThunk
            var addresses = (nint*)(image + *(uint*)(descriptor + 16)); // FirstThunk
            for (var i = 0; names[i] != 0; i++)
            {
                if ((names[i] & 0x8000_0000_0000_0000) == 0
                    && Marshal.PtrToStringAnsi((nint)(image + (uint)names[i] + 2)) == function)
                {
                    return addresses + i;
                }
            }
        }

        return null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);
}
