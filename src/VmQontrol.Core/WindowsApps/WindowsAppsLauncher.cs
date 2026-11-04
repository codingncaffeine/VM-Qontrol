namespace VmQontrol.Core.WindowsApps;

/// <summary>
/// D43: a Windows client edition has ONE interactive session, and each RDP connection takes it over (the previous client
/// is dropped; proven s14 on Windows 11 Pro: a second /app connection ended the first and showed both windows). So a
/// machine keeps ONE FreeRDP client, and every further program starts inside that session: the guest agent (SYSTEM) runs
/// this small launcher, which takes the session's user token (WTSQueryUserToken) and starts the program as that user on
/// its desktop, so its windows come out through the client already open, at once, with no new sign-in. A program that
/// needs elevation goes through cmd's start (ShellExecute), which shows Windows' own consent prompt in the session.
/// The launcher is C# compiled inside Windows by PowerShell's Add-Type (the .NET Framework compiler every Windows 10 and 11
/// carries), so nothing is shipped as a binary.
/// </summary>
public static class WindowsAppsLauncher
{
    public const string Exe = SharedFoldersFolder + @"\vmq-launch.exe";

    private const string SharedFoldersFolder = Runtime.SharedFolders.WindowsFolder;

    /// <summary>Bumped when <see cref="Source"/> changes, so an older copy in the guest is rebuilt.</summary>
    public const int Version = 4;

    public const string Source = """
        using System;
        using System.Runtime.InteropServices;
        using System.Text;

        public static class VmqLaunch
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct SessionInfo { public uint SessionId; [MarshalAs(UnmanagedType.LPWStr)] public string Station; public int State; }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct StartupInfo
            {
                public int cb; public string Reserved, Desktop, Title;
                public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
                public short ShowWindow, Reserved2; public IntPtr Reserved3, StdIn, StdOut, StdErr;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

            [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
            [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
            [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
            [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
            [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr env);
            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern bool CreateProcessAsUserW(IntPtr token, string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref StartupInfo si, out ProcessInfo pi);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

            [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IApplicationActivationManager
            {
                uint ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options);
                uint ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb);
                uint ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items);
            }

            [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
            class ApplicationActivationManager { }

            [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
            [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);

            // vmq-launch.exe session                -> the active RDP session, or result=no-session
            // vmq-launch.exe run COMMANDLINE [DIR]  -> starts it in the RDP session as its user
            // vmq-launch.exe activate AUMID         -> starts a Store app in the RDP session, as Start does (never
            //                                          through explorer.exe, which would become that session's shell
            //                                          and put a desktop and taskbar on this computer's screen)
            // vmq-launch.exe script FILE.ps1        -> runs it hidden as the user of the RDP session, else of Windows'
            //                                          own screen, else of a disconnected session; waits up to 2 minutes
            [STAThread]
            public static int Main(string[] args)
            {
                string mode = args.Length > 0 ? args[0] : "session";
                if (mode == "activate-here" && args.Length > 1)
                {
                    // Run as the session's user (started by "activate" below): the activation manager needs that user.
                    try
                    {
                        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                        Console.WriteLine("pid=" + manager.ActivateApplication(args[1], null, 0));
                        return 0;
                    }
                    catch (Exception ex) { Console.WriteLine("result=failed " + ex.Message); return 5; }
                }
                uint session = 0, console = 0, parked = 0; bool rdp = false, local = false, away = false;
                IntPtr list; int count;
                if (WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out list, out count))
                {
                    int size = Marshal.SizeOf(typeof(SessionInfo));
                    for (int i = 0; i < count; i++)
                    {
                        var s = (SessionInfo)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + i * size), typeof(SessionInfo));
                        // A signed-in session nobody is looking at (WTSDisconnected = 4) still runs programs; session 0 is services.
                        if (s.State == 4 && s.SessionId != 0) { parked = s.SessionId; away = true; }
                        if (s.State != 0 || s.Station == null) continue; // WTSActive = 0
                        if (s.Station.StartsWith("RDP-Tcp", StringComparison.OrdinalIgnoreCase)) { session = s.SessionId; rdp = true; }
                        else if (s.Station.Equals("Console", StringComparison.OrdinalIgnoreCase)) { console = s.SessionId; local = true; }
                    }
                    WTSFreeMemory(list);
                }
                if (mode == "script" && !rdp && local) { session = console; rdp = true; }
                if (mode == "script" && !rdp && away) { session = parked; rdp = true; }
                if (!rdp) { Console.WriteLine("result=no-session"); return 2; }
                Console.WriteLine("session=" + session);
                if (mode == "session" || args.Length < 2) { Console.WriteLine("result=ok"); return 0; }

                IntPtr token;
                if (!WTSQueryUserToken(session, out token)) { Console.WriteLine("result=no-token " + Marshal.GetLastWin32Error()); return 3; }
                IntPtr env;
                CreateEnvironmentBlock(out env, token, false);
                int error;
                if (mode == "script")
                {
                    IntPtr process;
                    error = Start(token, env, "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + args[1] + "\"", null, true, out process);
                    if (error == 0)
                    {
                        uint code = 1;
                        if (WaitForSingleObject(process, 120000) == 0) GetExitCodeProcess(process, out code); else Console.WriteLine("timeout");
                        Console.WriteLine("exit=" + code);
                        CloseHandle(process);
                    }
                }
                else if (mode == "activate")
                {
                    IntPtr process;
                    string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    error = Start(token, env, "\"" + self + "\" activate-here " + args[1], null, true, out process);
                    if (error == 0)
                    {
                        uint code = 1;
                        if (WaitForSingleObject(process, 30000) == 0) GetExitCodeProcess(process, out code);
                        if (code != 0) error = 1000 + (int)code;
                        CloseHandle(process);
                    }
                }
                else
                {
                    IntPtr process;
                    string dir = args.Length > 2 && args[2].Length > 0 ? args[2] : null;
                    error = Start(token, env, args[1], dir, false, out process);
                    // 740 = the program asks for elevation: let ShellExecute (cmd's start) show Windows' consent prompt.
                    if (error == 740) error = Start(token, env, "cmd.exe /d /c start \"\" " + args[1], dir, true, out process);
                    if (process != IntPtr.Zero) CloseHandle(process);
                }
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                CloseHandle(token);
                Console.WriteLine(error == 0 ? "result=ok" : "result=failed " + error);
                return error == 0 ? 0 : 4;
            }

            static int Start(IntPtr token, IntPtr env, string command, string dir, bool hidden, out IntPtr process)
            {
                process = IntPtr.Zero;
                var si = new StartupInfo();
                si.cb = Marshal.SizeOf(typeof(StartupInfo));
                si.Desktop = "winsta0\\default";
                ProcessInfo pi;
                // CREATE_UNICODE_ENVIRONMENT, and CREATE_NO_WINDOW for hidden console programs (no console, no Terminal).
                uint flags = 0x400u | (hidden ? 0x08000000u : 0u);
                if (!CreateProcessAsUserW(token, null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, flags, env, dir, ref si, out pi))
                    return Marshal.GetLastWin32Error();
                Console.WriteLine("pid=" + pi.ProcessId);
                process = pi.Process;
                CloseHandle(pi.Thread);
                return 0;
            }
        }
        """;

    /// <summary>PowerShell that compiles the launcher into the guest when it is missing or older.</summary>
    public static string InstallScript() => $$"""
        $ErrorActionPreference = 'Stop'
        $exe = '{{Exe}}'
        $stamp = "$exe.v{{Version}}"
        if (-not ((Test-Path $exe) -and (Test-Path $stamp))) {
            New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null
            Remove-Item $exe, "$exe.v*" -Force -ErrorAction SilentlyContinue
            Add-Type -TypeDefinition @'
        {{Source}}
        '@ -OutputAssembly $exe -OutputType ConsoleApplication
            Set-Content $stamp '{{Version}}'
        }
        'result=ok'
        """;

    /// <summary>A Windows command line: the program quoted, then its arguments as given.</summary>
    public static string CommandLine(string program, string? arguments) =>
        "\"" + program.Replace("\"", "", StringComparison.Ordinal) + "\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
}
