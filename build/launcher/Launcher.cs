// ---------------------------------------------------------------------------
// Launcher.cs
//
// The release package's only entry point. Compiled by build/publish.ps1 into
// <product name>.exe, a small .NET Framework 4.x winexe that starts
// app\StreamingAggregation.App.exe and then waits for it.
//
// WHY A LAUNCHER EXISTS AT ALL
//
// The payload must live in ONE subdirectory next to the app host, not at the
// package root: the CLR probes the app host directory for dependencies, Win32
// side-by-side resolves the embedded application manifest against the exe
// directory, and WinRT activation resolves against the process directory.
// Scattering the payload produces a process that starts, reports
// Responding = True, owns a window handle, and never draws anything. So the
// package needs one file at the root, and that file has to start the app from
// inside app\. This is it. Steam++ ships the same shape: a tiny loader exe at
// the root of an otherwise self-similar package.
//
// WHY THE LAUNCHER WAITS FOR THE CHILD
//
// A launcher that fires and forgets makes every external observer lie: the
// Task Manager shows the launcher's exit code instead of the app's, a script
// cannot tell a clean shutdown from a crash, and closing the launcher in the
// shell would appear to leave the app running with nothing supervising it.
// Waiting makes the launcher's lifetime the app's lifetime and lets the exit
// code be passed through unchanged, so the launcher is transparent.
//
// SINGLE INSTANCE
//
// A second double-click does not start a second app. The launcher takes a
// named mutex scoped to ITS OWN PACKAGE PATH (Local\, hashed), so two separate
// copies of the release on one machine do not block each other. When the
// mutex is already held, the running app is located by executable path and
// its window is brought to the front instead.
//
// MOVABILITY
//
// Nothing here is absolute: the package directory comes from this exe's own
// path via GetModuleFileNameW + PathRemoveFileSpec, so copying the folder
// anywhere and double-clicking works. SetDllDirectory is deliberately NOT
// used -- it would change the DLL search path for the LAUNCHER's own process,
// and the child is a brand new process whose search path already starts at
// the app host directory. Nothing the parent does to its own search path can
// help the child.
//
// LANGUAGE LEVEL
//
// Compiled by the .NET Framework csc.exe, which is C# 5: no string
// interpolation, no 'out var', no discards, no expression-bodied members.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("流式聚合")]
[assembly: AssemblyProduct("流式聚合")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

internal static class Launcher
{
    // =====================================================================
    // PINNED LAYOUT
    //
    // These three constants are the launcher's contract with the release
    // pipeline. Assert-LauncherContract reads them back out of this source
    // and fails the build if any of them drifts from Common.ps1, because a
    // launcher that names the wrong file starts nothing and nothing else in
    // the pipeline would notice.
    // =====================================================================

    private const string PayloadDirectoryName = "app";
    private const string PayloadExeName = "StreamingAggregation.App.exe";
    private const string LauncherFileVersion = "0.2.0.0";

    // =====================================================================
    // EXIT CODES
    //
    // Anything other than the three below is the child's own exit code,
    // passed through untouched.
    // =====================================================================

    private const int ExitInternalError = 70;
    private const int ExitLaunchFailure = 71;
    private const int ExitPayloadMissing = 72;

    // =====================================================================
    // kernel32
    // =====================================================================

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, StringBuilder lpFilename, int nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes,
                                             [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner,
                                             string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint dwDesiredAccess,
                                           [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
                                           string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint ERROR_ALREADY_EXISTS = 183;

    // ASFW_ANY: let the freshly created child take the foreground when its
    // window first appears. A child spawned by a process that is not itself
    // in the foreground is otherwise not allowed to steal it, so a launch
    // triggered from Explorer would open the app BEHIND whatever the user
    // switched to in the meantime.
    private const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    // =====================================================================
    // shlwapi
    // =====================================================================

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "PathRemoveFileSpecW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PathRemoveFileSpecW(StringBuilder pszPath);

    // =====================================================================
    // CreateProcess
    // =====================================================================

    // Laid out field by field rather than with LayoutKind.Sequential over
    // string fields: the real STARTUPINFO holds pointers, and a string
    // marshalled into a pointer slot is not interchangeable with it for the
    // purposes of Marshal.SizeOf, which is what has to be right here.
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;

    // =====================================================================
    // user32
    // =====================================================================

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
                                                 [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    // =====================================================================
    // ENTRY POINT
    // =====================================================================

    public static int Main(string[] args)
    {
        string packageDir;
        try
        {
            packageDir = GetOwnDirectory();
        }
        catch (Exception ex)
        {
            ShowError("无法确定启动器所在目录。" + Environment.NewLine + ex.Message);
            return ExitInternalError;
        }

        string payloadDir = Path.Combine(packageDir, PayloadDirectoryName);
        string payloadExe = Path.Combine(payloadDir, PayloadExeName);

        // Checked before the mutex, not after: if the package is broken, the
        // second double-click should say so rather than silently "activate"
        // a window that was never created.
        if (!File.Exists(payloadExe))
        {
            ShowError(
                "找不到应用程序本体：" + Environment.NewLine + Environment.NewLine +
                payloadExe + Environment.NewLine + Environment.NewLine +
                "发布包不完整，或者它被单独移动过。启动器和 app\\ 目录必须留在同一个文件夹里。");
            return ExitPayloadMissing;
        }

        IntPtr instanceLock;
        bool firstInstance = TryAcquireInstanceLock(packageDir, out instanceLock);
        if (!firstInstance)
        {
            // Somebody already holds this package's lock. Hand the click over
            // to the running app instead of starting a second copy. Closing
            // our own handle is what makes the abandoned handle not pin the
            // mutex object open forever.
            if (instanceLock != IntPtr.Zero) { CloseHandle(instanceLock); }
            return ActivateRunningInstance(payloadExe);
        }

        try
        {
            return LaunchAndWait(payloadExe, payloadDir, args);
        }
        finally
        {
            if (instanceLock != IntPtr.Zero) { CloseHandle(instanceLock); }
        }
    }

    // =====================================================================
    // SELF-LOCATION
    // =====================================================================

    // The package directory, derived from this executable's own path.
    //
    // Assembly.Location is the managed equivalent and would be shorter, but
    // GetModuleFileName + PathRemoveFileSpec is the documented Win32 way to
    // ask "where am I on disk", and it is exactly the pair that keeps working
    // when the package is renamed, moved, or launched through a shell that
    // sets no working directory at all.
    private static string GetOwnDirectory()
    {
        StringBuilder buffer = new StringBuilder(1024);
        uint length = GetModuleFileNameW(IntPtr.Zero, buffer, buffer.Capacity);
        if (length == 0)
        {
            throw new InvalidOperationException("GetModuleFileNameW failed (win32 error " +
                                                Marshal.GetLastWin32Error().ToString() + ").");
        }
        // GetModuleFileNameW truncates instead of failing on some inputs.
        if (length >= buffer.Capacity)
        {
            throw new PathTooLongException("The launcher's own path exceeds " +
                                           buffer.Capacity + " characters.");
        }
        if (!PathRemoveFileSpecW(buffer))
        {
            throw new InvalidOperationException("PathRemoveFileSpecW failed (win32 error " +
                                                Marshal.GetLastWin32Error().ToString() + ").");
        }
        string dir = buffer.ToString();
        if (string.IsNullOrEmpty(dir))
        {
            throw new InvalidOperationException("The launcher's own path has no directory part.");
        }
        return dir;
    }

    // =====================================================================
    // SINGLE INSTANCE
    // =====================================================================

    // Takes this package's named mutex, or reports that another launcher for
    // the same package already has it.
    //
    // The name is derived from the package path so two copies of the release
    // side by side on one machine are two independent applications. Local\
    // keeps it in the caller's session, which is where a double-clicked
    // desktop shortcut runs; it also means no machine-wide name is claimed.
    //
    // The mutex is owned for as long as the app runs because the launcher
    // holds it until the child exits, so "mutex is held" and "an app from
    // this package is running" are the same statement.
    private static bool TryAcquireInstanceLock(string packageDir, out IntPtr mutex)
    {
        mutex = CreateMutexW(IntPtr.Zero, true, BuildInstanceMutexName(packageDir));
        int error = Marshal.GetLastWin32Error();
        if (mutex == IntPtr.Zero)
        {
            // No lock could be created at all. Refusing to launch would make a
            // benign environment problem look like a broken package, so
            // proceed without single-instance protection and say nothing --
            // starting a second app is far less bad than starting none.
            mutex = IntPtr.Zero;
            return true;
        }
        if (error == ERROR_ALREADY_EXISTS)
        {
            // Already held: another launcher owns it. Release our reference so
            // the handle does not keep the object alive past its real owner.
            CloseHandle(mutex);
            mutex = IntPtr.Zero;
            return false;
        }
        return true;
    }

    private static string BuildInstanceMutexName(string packageDir)
    {
        return "Local\\StreamingAggregation-Launcher-" +
               Fnv1a32(packageDir.ToLowerInvariant()).ToString("x8");
    }

    // FNV-1a. Only needs to spread a path across 32 bits so two packages do
    // not collide on a name; not a security primitive.
    private static uint Fnv1a32(string value)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            uint hash = offsetBasis;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }
            return hash;
        }
    }

    // Brings the already-running app forward instead of starting a second one.
    // Always succeeds from the caller's point of view: failing to find the
    // window is not worth a dialog, because the first launcher is starting
    // that very window right now.
    private static int ActivateRunningInstance(string payloadExe)
    {
        int runningProcessId;
        if (!TryFindRunningInstance(payloadExe, out runningProcessId))
        {
            return 0;
        }

        IntPtr window = FindTopLevelWindow(runningProcessId);
        if (window == IntPtr.Zero)
        {
            return 0;
        }

        if (IsIconic(window))
        {
            ShowWindow(window, SW_RESTORE);
        }
        BringToFront(window);
        return 0;
    }

    // Matches by EXECUTABLE PATH, not by process name.
    //
    // The app name is the same in every installed copy of this product, so
    // name alone would happily activate a different checkout's window. The
    // path pins it to the package the user just double-clicked.
    private static bool TryFindRunningInstance(string payloadExe, out int processId)
    {
        processId = 0;
        string wanted = NormalizePathForCompare(payloadExe);
        string name = Path.GetFileNameWithoutExtension(payloadExe);

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(name);
        }
        catch
        {
            return false;
        }

        foreach (Process candidate in candidates)
        {
            try
            {
                string actual = NormalizePathForCompare(candidate.MainModule.FileName);
                if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    processId = candidate.Id;
                    return true;
                }
            }
            catch
            {
                // Access denied or the process exited between enumeration and
                // inspection. Either way it is not the one we are after.
            }
            finally
            {
                candidate.Dispose();
            }
        }
        return false;
    }

    private static string NormalizePathForCompare(string path)
    {
        if (string.IsNullOrEmpty(path)) { return string.Empty; }
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return path.TrimEnd('\\');
        }
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindowsProc probe = delegate(IntPtr window, IntPtr state)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != (uint)processId) { return true; }
            if (!IsWindowVisible(window)) { return true; }
            if (found == IntPtr.Zero) { found = window; }
            return true;
        };

        EnumWindows(probe, IntPtr.Zero);
        // The delegate is unmanaged-backed; keeping it alive across the call
        // is not optional on every runtime.
        GC.KeepAlive(probe);
        return found;
    }

    // SetForegroundWindow only succeeds when the calling process owns the
    // foreground, OR when it has the input queues of the foreground thread
    // attached. Joining those queues for the duration of the call is the
    // standard way to buy that permission; forgetting to detach would leave
    // this thread permanently welded to another process's queue.
    private static void BringToFront(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = 0;
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out foregroundThread);
        }

        uint selfThread = GetCurrentThreadId();
        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != selfThread)
        {
            attached = AttachThreadInput(selfThread, foregroundThread, true);
        }
        try
        {
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(selfThread, foregroundThread, false);
            }
        }
    }

    // =====================================================================
    // LAUNCH
    // =====================================================================

    // Starts the app, waits for it, and hands its exit code straight back.
    private static int LaunchAndWait(string payloadExe, string workingDirectory, string[] args)
    {
        STARTUPINFO startupInfo = new STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        // SW_SHOWNORMAL through STARTF_USESHOWWINDOW: without the flag the
        // wShowWindow field is ignored and the child inherits whatever show
        // state this process had, which for a launcher started from Explorer
        // is not defined in any useful way.
        startupInfo.dwFlags = STARTF_USESHOWWINDOW;
        startupInfo.wShowWindow = SW_SHOWNORMAL;

        StringBuilder commandLine = new StringBuilder();
        commandLine.Append('"').Append(payloadExe).Append('"');
        if (args != null)
        {
            foreach (string arg in args)
            {
                if (arg == null) { continue; }
                commandLine.Append(' ').Append(QuoteArgument(arg));
            }
        }

        PROCESS_INFORMATION processInfo;
        bool created = CreateProcessW(
            payloadExe, commandLine,
            IntPtr.Zero, IntPtr.Zero,
            false, 0, IntPtr.Zero,
            // The child's working directory is the payload directory, so any
            // relative path the app resolves against its working directory
            // behaves exactly as if it had been double-clicked in place.
            workingDirectory,
            ref startupInfo, out processInfo);

        if (!created)
        {
            int error = Marshal.GetLastWin32Error();
            ShowError(
                "无法启动应用程序：" + Environment.NewLine + Environment.NewLine +
                payloadExe + Environment.NewLine + Environment.NewLine +
                "系统错误 " + error.ToString() + "（" + DescribeWin32Error(error) + "）");
            return ExitLaunchFailure;
        }

        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
            AllowSetForegroundWindow((uint)processInfo.dwProcessId);

            WaitForSingleObject(processInfo.hProcess, INFINITE);

            uint exitCode = 0;
            GetExitCodeProcess(processInfo.hProcess, out exitCode);
            return unchecked((int)exitCode);
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero) { CloseHandle(processInfo.hThread); }
            if (processInfo.hProcess != IntPtr.Zero) { CloseHandle(processInfo.hProcess); }
        }
    }

    // The standard CommandLineToArgvW quoting rules: a trailing run of
    // backslashes before a closing quote has to be doubled, or it escapes the
    // quote instead of being part of the argument.
    private static string QuoteArgument(string value)
    {
        if (value.Length == 0) { return "\"\""; }
        if (value.IndexOf(' ') < 0 && value.IndexOf('\t') < 0 && value.IndexOf('"') < 0)
        {
            return value;
        }

        StringBuilder quoted = new StringBuilder(value.Length + 8);
        quoted.Append('"');
        int pendingBackslashes = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }
            if (c == '"')
            {
                quoted.Append('\\', pendingBackslashes * 2 + 1);
            }
            else
            {
                quoted.Append('\\', pendingBackslashes);
            }
            quoted.Append(c);
            pendingBackslashes = 0;
        }
        quoted.Append('\\', pendingBackslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    // A silent exit is the worst outcome for a launcher: the user sees
    // nothing happen and has no way to tell a missing file from a locked one.
    private static void ShowError(string message)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, message, "流式聚合 - 无法启动", MB_OK | MB_ICONERROR);
        }
        catch
        {
            // Nothing left to do. Never let the dialog itself throw.
        }
    }

    private static string DescribeWin32Error(int error)
    {
        try
        {
            return new System.ComponentModel.Win32Exception(error).Message;
        }
        catch
        {
            return "未知错误";
        }
    }
}