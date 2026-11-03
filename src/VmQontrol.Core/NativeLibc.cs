using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VmQontrol.Core;

/// <summary>
/// The C library calls (open with O_EXCL, sendmsg with SCM_RIGHTS, access, statx) name "libc", which is not a file on
/// either C library: glibc's libc.so is a linker script and musl ships libc.musl-ARCH.so.1. The process has its C library
/// loaded already, so "libc" resolves to the program's own global symbols (dlopen(NULL)), the same on glibc and musl.
/// </summary>
internal static partial class NativeLibc
{
#pragma warning disable CA2255 // the resolver must be in place before the first native call, from any entry point
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibc).Assembly, static (name, _, _) =>
            name == "libc" ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint Geteuid();

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int dirfd, string path, int flags, uint mask, nint buffer);

    /// <summary>The owner's id of <paramref name="path"/> itself (a link is not followed), through statx; non-zero = failed.</summary>
    internal static unsafe int Stat(string path, out uint owner)
    {
        var buffer = stackalloc byte[256];
        var r = StatX(-100 /* AT_FDCWD */, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x4 /* STATX_UID */, (nint)buffer);
        owner = r == 0 ? *(uint*)(buffer + 20) : uint.MaxValue;
        return r;
    }
}
