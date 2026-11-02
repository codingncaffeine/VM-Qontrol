using System.Runtime.CompilerServices;

namespace VmQontrol.Tests;

/// <summary>No test may put polkit's password dialog on the desktop: udisks2 calls fail at once instead of asking.</summary>
internal static class NoPasswordPrompts
{
#pragma warning disable CA2255 // before any test runs, in every test process
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => VmQontrol.Core.Devices.DriveAccess.Interactive = false;
}
