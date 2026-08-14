using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuditionModStudio.Infrastructure.Processes;

public static class WindowsProcessLaunchHardening
{
    private const uint LoadLibrarySearchApplicationDirectory = 0x00000200;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private static readonly object Sync = new();
    private static bool _applied;

    public static void ApplyProcessDllPolicy()
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref _applied)) return;
        lock (Sync)
        {
            if (_applied) return;

            // Empty SetDllDirectory removes the current directory from the standard DLL search path and is inherited
            // by unpackaged child processes. The default policy then limits this process to app-local and System32.
            if (!SetDllDirectory(string.Empty))
                throw PlatformFailure("PROCESS_DLL_CURRENT_DIRECTORY_REMOVAL_FAILED");
            if (!SetDefaultDllDirectories(LoadLibrarySearchApplicationDirectory | LoadLibrarySearchSystem32))
                throw PlatformFailure("PROCESS_DLL_DEFAULT_DIRECTORIES_FAILED");
            Volatile.Write(ref _applied, true);
        }
    }

    public static void HardenChildStartInfo(ProcessStartInfo startInfo, string controlledTemporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!Path.IsPathFullyQualified(startInfo.FileName)
            || !string.Equals(startInfo.FileName, Path.GetFullPath(startInfo.FileName), PathComparison))
            throw new ArgumentException("PROCESS_EXECUTABLE_PATH_NOT_EXACT", nameof(startInfo));
        if (!Path.IsPathFullyQualified(startInfo.WorkingDirectory)
            || !string.Equals(startInfo.WorkingDirectory, Path.GetFullPath(startInfo.WorkingDirectory), PathComparison))
            throw new ArgumentException("PROCESS_WORKING_DIRECTORY_NOT_CONTROLLED", nameof(startInfo));
        if (!Path.IsPathFullyQualified(controlledTemporaryDirectory)
            || !string.Equals(controlledTemporaryDirectory, Path.GetFullPath(controlledTemporaryDirectory), PathComparison))
            throw new ArgumentException("PROCESS_TEMP_DIRECTORY_NOT_CONTROLLED", nameof(controlledTemporaryDirectory));
        if (startInfo.UseShellExecute || !string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException("PROCESS_SHELL_OR_RAW_ARGUMENTS_REJECTED", nameof(startInfo));

        var executableName = Path.GetFileName(startInfo.FileName);
        if (executableName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PROCESS_SHELL_EXECUTABLE_REJECTED", nameof(startInfo));

        startInfo.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
            var windowsDirectory = Directory.GetParent(systemDirectory)?.FullName
                ?? throw new InvalidOperationException("PROCESS_WINDOWS_DIRECTORY_UNAVAILABLE");
            startInfo.Environment["SystemRoot"] = windowsDirectory;
            startInfo.Environment["WINDIR"] = windowsDirectory;
            startInfo.Environment["SystemDrive"] = Path.GetPathRoot(windowsDirectory)!;
            startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, systemDirectory, windowsDirectory);
        }
        else
        {
            startInfo.Environment["PATH"] = "/usr/bin:/bin";
        }
        startInfo.Environment["TEMP"] = Path.GetFullPath(controlledTemporaryDirectory);
        startInfo.Environment["TMP"] = Path.GetFullPath(controlledTemporaryDirectory);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException PlatformFailure(string diagnosticCode) =>
        new(diagnosticCode, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string pathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
