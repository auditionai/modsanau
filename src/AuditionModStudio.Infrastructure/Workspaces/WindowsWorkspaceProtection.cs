using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AuditionModStudio.Infrastructure.Workspaces;

public interface IWorkspaceProtection
{
    void Protect(string directoryPath);
    bool IsProtected(string directoryPath);
}

public sealed class WindowsWorkspaceProtection : IWorkspaceProtection
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const ushort DaclProtected = 0x1000;
    private const uint SddlRevision1 = 1;
    private const string WorkspaceSddl = "D:P(A;OICI;FA;;;OW)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    public void Protect(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows()) return;
        if (!Directory.Exists(directoryPath)) throw new DirectoryNotFoundException("Workspace directory is missing.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                WorkspaceSddl, SddlRevision1, out var descriptor, out _))
            throw SecurityFailure("create");
        try
        {
            if (!SetFileSecurity(directoryPath,
                    DaclSecurityInformation | ProtectedDaclSecurityInformation, descriptor))
                throw SecurityFailure("apply");
        }
        finally { LocalFree(descriptor); }
        if (!IsProtected(directoryPath)) throw new UnauthorizedAccessException("Workspace DACL protection failed.");
    }

    public bool IsProtected(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows()) return true;
        uint required = 0;
        _ = GetFileSecurity(directoryPath, DaclSecurityInformation, null, 0, out required);
        if (required == 0) throw SecurityFailure("query");
        var descriptor = new byte[required];
        if (!GetFileSecurity(directoryPath, DaclSecurityInformation, descriptor,
                (uint)descriptor.Length, out _)) throw SecurityFailure("read");
        if (!GetSecurityDescriptorControl(descriptor, out var control, out _))
            throw SecurityFailure("inspect");
        if ((control & DaclProtected) == 0) return false;
        if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SddlRevision1,
                DaclSecurityInformation, out var sddlPointer, out _)) throw SecurityFailure("convert");
        try { return string.Equals(Marshal.PtrToStringUni(sddlPointer), WorkspaceSddl, StringComparison.Ordinal); }
        finally { LocalFree(sddlPointer); }
    }

    private static UnauthorizedAccessException SecurityFailure(string operation) => new(
        $"Workspace ACL {operation} failed.", new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor,
        uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(byte[] securityDescriptor,
        uint revision, uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string fileName, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(string fileName, uint requestedInformation,
        byte[]? securityDescriptor, uint length, out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(byte[] securityDescriptor,
        out ushort control, out uint revision);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
