using System.Diagnostics;
using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

public static class DirectXTexCommandBuilder
{
    public static ProcessStartInfo Create(
        string executablePath,
        string workingDirectory,
        DirectXTexEvaluationRequest request,
        string inputPath,
        string outputDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-y");
        if (request.Operation == DirectXTexEvaluationOperation.DecodeToPng)
        {
            startInfo.ArgumentList.Add("-ft");
            startInfo.ArgumentList.Add("png");
        }
        else
        {
            startInfo.ArgumentList.Add("-ft");
            startInfo.ArgumentList.Add("dds");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("BC3_UNORM");
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(request.TargetWidth!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add(request.TargetHeight!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(request.MipLevelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.ForceLegacyHeader)
            {
                startInfo.ArgumentList.Add("-dx9");
            }
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(inputPath);
        return startInfo;
    }
}
