namespace Archives.FakeTool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("-da" or "-ca"))
        {
            await Console.Error.WriteLineAsync("Invalid arguments");
            return 2;
        }

        var workingDirectory = Environment.CurrentDirectory;
        if (File.Exists(Path.Combine(workingDirectory, ".fake-hang")))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        var archivePath = Path.GetFullPath(args[1], workingDirectory);
        var extractDirectory = Path.GetFullPath(args[2], workingDirectory);
        var keydatPath = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            $"{Path.GetFileNameWithoutExtension(archivePath)}.keydat");

        if (!File.Exists(keydatPath))
        {
            await Console.Out.WriteLineAsync(
                "Keydat file not found. Program will automatic generate it. Please Select");
            await Console.Out.WriteLineAsync("===== SUPPORT COUNTRY LIST =====");
            await Console.Out.WriteLineAsync("1. AuditionVN");
            if (File.Exists(Path.Combine(workingDirectory, ".fake-split-prompt")))
            {
                await Console.Out.WriteAsync("Sel");
                await Console.Out.FlushAsync();
                await Task.Delay(50);
                await Console.Out.WriteAsync("ect:");
            }
            else
            {
                await Console.Out.WriteAsync("Select:");
            }

            await Console.Out.FlushAsync();
            var selection = await Console.In.ReadLineAsync();
            if (!string.Equals(selection, "1", StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync("Unexpected country selection");
                return 3;
            }

            await File.WriteAllTextAsync(keydatPath, "fake-keydat");
        }

        if (File.Exists(Path.Combine(workingDirectory, ".fake-stderr")))
        {
            await Console.Error.WriteLineAsync("controlled stderr diagnostic");
        }

        await Console.Out.WriteLineAsync("unrecognized informational output");
        if (File.Exists(Path.Combine(workingDirectory, ".fake-no-artifact")))
        {
            return 0;
        }

        if (args[0] == "-da")
        {
            var assetDirectory = Path.Combine(extractDirectory, "texture");
            Directory.CreateDirectory(assetDirectory);
            var assetPath = Path.Combine(assetDirectory, "file.dds");
            await File.WriteAllBytesAsync(assetPath, [0x44, 0x44, 0x53]);
            await Console.Out.WriteLineAsync($"writing : {Path.GetRelativePath(workingDirectory, assetPath)}");
        }
        else
        {
            await using var stream = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(new byte[] { 0x01 });
            await stream.FlushAsync();
            await Console.Out.WriteLineAsync($"Packing: {args[2]}{Path.DirectorySeparatorChar}texture{Path.DirectorySeparatorChar}file.dds");
        }

        return 0;
    }
}
