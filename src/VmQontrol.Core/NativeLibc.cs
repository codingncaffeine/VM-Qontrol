using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VmQontrol.Core;

/// <summary>
/// The C library calls (open with O_EXCL, sendmsg with SCM_RIGHTS, access, statx) name "libc", which is not a file on
/// either C library: glibc's libc.so is a linker script and musl ships libc.musl-ARCH.so.1. The process has its C library
/// loaded already, so "libc" resolves to the program's own global symbols (dlopen(NULL)), the same on glibc and musl.
/// </summary>
internal static class NativeLibc
{
#pragma warning disable CA2255 // the resolver must be in place before the first native call, from any entry point
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibc).Assembly, static (name, _, _) =>
            name == "libc" ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero);
}
