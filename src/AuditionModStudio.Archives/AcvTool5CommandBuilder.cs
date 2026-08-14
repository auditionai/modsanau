using System.Diagnostics;
using AuditionModStudio.Infrastructure.Processes;

namespace AuditionModStudio.Archives;

internal static class AcvTool5CommandBuilder
{
    public static ProcessStartInfo Create(
        string executablePath,
        string workingDirectory,
        AcvTool5Operation operation,
        string archiveArgument,
        string extractDirectoryArgument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(operation == AcvTool5Operation.Extract ? "-da" : "-ca");
        startInfo.ArgumentList.Add(archiveArgument);
        startInfo.ArgumentList.Add(extractDirectoryArgument);
        WindowsProcessLaunchHardening.HardenChildStartInfo(startInfo, workingDirectory);
        return startInfo;
    }
}
