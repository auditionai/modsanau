using System.Diagnostics;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Infrastructure.Processes;

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
            startInfo.ArgumentList.Add(GetTargetFormat(request.TargetFormat, request.TargetColorSpace));
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(request.TargetWidth!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add(request.TargetHeight!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(request.MipLevelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.TargetFormat == DdsFormat.BC1
                && request.TargetAlphaSemantics == DdsTargetAlphaSemantics.Binary)
            {
                startInfo.ArgumentList.Add("-at");
                startInfo.ArgumentList.Add("0.5");
            }

            if (request.ForceLegacyHeader || request.TargetHeaderType == DdsHeaderType.Legacy)
            {
                startInfo.ArgumentList.Add("-dx9");
            }
            else
            {
                startInfo.ArgumentList.Add("-dx10");
            }
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(inputPath);
        WindowsProcessLaunchHardening.HardenChildStartInfo(startInfo, workingDirectory);
        return startInfo;
    }

    private static string GetTargetFormat(DdsFormat format, DdsColorSpace colorSpace)
    {
        var suffix = colorSpace == DdsColorSpace.Srgb ? "_SRGB" : string.Empty;
        return format switch
        {
            DdsFormat.BC1 => $"BC1_UNORM{suffix}",
            DdsFormat.BC3 => $"BC3_UNORM{suffix}",
            DdsFormat.Rgba8 => $"R8G8B8A8_UNORM{suffix}",
            DdsFormat.Bgra8 => $"B8G8R8A8_UNORM{suffix}",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }
}
