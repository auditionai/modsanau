using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Imaging;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Tasks;
using AuditionModStudio.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan100PerformanceTests(ITestOutputHelper output)
{
    private const string ArchiveHash = "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string PointerHash = "854189B91972C17C483C2434FC077B1F94AE73DDA947FF72173C362A9D2512EA";
    private const string PointerRelativePath = "texture/hud/pointer.dds";

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Coverage", "Plan100")]
    public async Task Private_extracted_corpus_reports_first_pass_and_repeated_header_scan_throughput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var archivePath = Path.Combine(repositoryRoot, "015.ab");
        var extractedRoot = Path.Combine(repositoryRoot, "015");
        RequireFile(archivePath, ArchiveHash, "PLAN 100 archive fixture is unavailable or changed.");
        if (!Directory.Exists(extractedRoot))
        {
            throw SkipException.ForSkip("PLAN 100 private extracted corpus is unavailable.");
        }

        var allFiles = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ddsPaths = allFiles.Where(path => string.Equals(Path.GetExtension(path), ".dds",
            StringComparison.OrdinalIgnoreCase)).ToArray();
        var logicalBytes = allFiles.Sum(path => new FileInfo(path).Length);
        var directoryCount = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.AllDirectories).Count();
        var reader = new DdsMetadataReader();

        var firstPass = await MeasureAsync(() => ReadAllMetadataAsync(reader, ddsPaths));
        var repeatedSamples = new List<double>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            repeatedSamples.Add((await MeasureAsync(() => ReadAllMetadataAsync(reader, ddsPaths))).ElapsedMilliseconds);
        }

        Assert.Equal(320, allFiles.Length);
        Assert.Equal(310, ddsPaths.Length);
        Assert.Equal(ArchiveHash, await HashAsync(archivePath));
        var repeatedStats = PerformanceStatistics.From(repeatedSamples);
        WriteEvidence(
            "archive-header-scan",
            $"files={allFiles.Length};dds={ddsPaths.Length};directories={directoryCount};logicalBytes={logicalBytes}",
            1,
            firstPass.ElapsedMilliseconds,
            firstPass.ElapsedMilliseconds,
            firstPass.ElapsedMilliseconds,
            null,
            firstPass.AllocatedBytes,
            firstPass.WorkingSetDeltaBytes,
            $"first pass with uncontrolled OS file cache; header-only; repeatedRuns=5; repeatedMinMs={F(repeatedStats.Minimum)};repeatedMedianMs={F(repeatedStats.Median)};repeatedMaxMs={F(repeatedStats.Maximum)};repeatedFilesPerSecond={F(ddsPaths.Length / (repeatedStats.Median / 1000d))}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Coverage", "Plan100")]
    public async Task Real_thumbnail_reports_generated_memory_disk_and_single_flight_states()
    {
        var texconvPath = RequireApprovedTexconv();
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(repositoryRoot, "015",
            PointerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        RequireFile(sourcePath, PointerHash, "PLAN 100 pointer DDS fixture is unavailable or changed.");
        var root = Path.Combine(Path.GetTempPath(), "Audition PLAN 100 thumbnail", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectoriesExist();
            var secure = new TestSecureWorkspace(Path.Combine(root, "workspace"));
            var projectWorkspace = new TestProjectWorkspace(secure);
            var stagedSource = secure.ResolveRelativePath(Path.Combine(
                "Extracted", "015", PointerRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedSource)!);
            File.Copy(sourcePath, stagedSource);
            var pathSecurity = new PathSecurity();
            var metadataReader = new DdsMetadataReader();
            var harness = new DirectXTexEvaluationHarness(
                pathSecurity, metadataReader, DirectXTexEvaluationToolCatalog.May2026X64);
            var preview = new DdsPreviewService(
                metadataReader,
                harness,
                pathSecurity,
                new(texconvPath, TimeSpan.FromMinutes(2), DdsPreviewResourcePolicy.Default));
            var import = new ImageImportService(ImageImportResourcePolicy.Default);
            var resize = new ImageResizeService(ImageImportResourcePolicy.Default);
            ThumbnailCache CreateCache() => new(
                paths, pathSecurity, preview, import, resize, ThumbnailCacheOptions.Default);
            var request = new ThumbnailCacheRequest(
                projectWorkspace, new(PointerRelativePath), new(PointerHash), 192);

            var cache = CreateCache();
            ThumbnailCacheResult? generated = null;
            var generatedMeasurement = await MeasureAsync(async () =>
            {
                generated = await cache.GetOrCreateAsync(request);
            });
            Assert.True(generated!.Succeeded, generated.DiagnosticCode);
            Assert.Equal(ThumbnailCacheSource.Generated, generated.Source);

            var memorySamples = new List<double>();
            for (var iteration = 0; iteration < 25; iteration++)
            {
                ThumbnailCacheResult? memory = null;
                var measured = await MeasureAsync(async () => memory = await cache.GetOrCreateAsync(request));
                Assert.Equal(ThumbnailCacheSource.Memory, memory!.Source);
                memorySamples.Add(measured.ElapsedMilliseconds);
            }

            var diskSamples = new List<double>();
            for (var iteration = 0; iteration < 5; iteration++)
            {
                ThumbnailCacheResult? disk = null;
                var diskCache = CreateCache();
                var measured = await MeasureAsync(async () => disk = await diskCache.GetOrCreateAsync(request));
                Assert.Equal(ThumbnailCacheSource.Disk, disk!.Source);
                diskSamples.Add(measured.ElapsedMilliseconds);
            }

            var concurrentRequest = request with { MaximumDimension = 191 };
            var concurrentCache = CreateCache();
            ThumbnailCacheResult[]? concurrentResults = null;
            var concurrent = await MeasureAsync(async () => concurrentResults = await Task.WhenAll(
                Enumerable.Range(0, 16).Select(_ => concurrentCache.GetOrCreateAsync(concurrentRequest))));
            Assert.All(concurrentResults!, result => Assert.True(result.Succeeded, result.DiagnosticCode));
            Assert.Single(concurrentResults!, result => result.Source == ThumbnailCacheSource.Generated);
            Assert.Equal(PointerHash, await HashAsync(sourcePath));
            Assert.Equal(PointerHash, await HashAsync(stagedSource));

            var memoryStats = PerformanceStatistics.From(memorySamples);
            var diskStats = PerformanceStatistics.From(diskSamples);
            WriteEvidence(
                "thumbnail-cache",
                $"sourceBytes={new FileInfo(sourcePath).Length};source=pointer.dds;thumbnail={generated.Image!.Width}x{generated.Image.Height}",
                1,
                generatedMeasurement.ElapsedMilliseconds,
                generatedMeasurement.ElapsedMilliseconds,
                generatedMeasurement.ElapsedMilliseconds,
                null,
                generatedMeasurement.AllocatedBytes,
                generatedMeasurement.WorkingSetDeltaBytes,
                $"generated;memoryRuns=25;memoryMinMs={F(memoryStats.Minimum)};memoryMedianMs={F(memoryStats.Median)};memoryMaxMs={F(memoryStats.Maximum)};memoryP95Ms={F(memoryStats.P95!.Value)};diskRuns=5;diskMinMs={F(diskStats.Minimum)};diskMedianMs={F(diskStats.Median)};diskMaxMs={F(diskStats.Maximum)};singleFlight16Ms={F(concurrent.ElapsedMilliseconds)}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Coverage", "Plan100")]
    public async Task Large_image_resize_and_adjustment_report_latency_and_memory_observations()
    {
        var source = CreatePatternImage(6000, 1801);
        var sourceHash = Convert.ToHexString(SHA256.HashData(source.Pixels.AsSpan()));
        var resize = new ImageResizeService(ImageImportResourcePolicy.Default);
        var adjustment = new ImageAdjustmentService(ImageImportResourcePolicy.Default);
        Assert.True((await resize.ResizeAsync(new(
            CreatePatternImage(64, 64), 32, 32,
            new(ImageResizeMode.Stretch, ImageInterpolationMode.Linear)))).Succeeded);

        var resizeSamples = new List<double>();
        long resizeAllocated = 0;
        long resizeWorkingSetDelta = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            ImageResizeResult? result = null;
            var measured = await MeasureAsync(async () => result = await resize.ResizeAsync(new(
                source, 3000, 901, new(ImageResizeMode.Stretch, ImageInterpolationMode.Linear))));
            Assert.True(result!.Succeeded, result.DiagnosticCode);
            Assert.Equal((3000, 901), (result.Image!.Width, result.Image.Height));
            resizeSamples.Add(measured.ElapsedMilliseconds);
            resizeAllocated += measured.AllocatedBytes;
            resizeWorkingSetDelta = Math.Max(resizeWorkingSetDelta, measured.WorkingSetDeltaBytes);
        }

        Assert.True((await adjustment.AdjustAsync(new(
            CreatePatternImage(64, 64), new(Brightness: 0.01)))).Succeeded);
        var adjustmentSamples = new List<double>();
        long adjustmentAllocated = 0;
        long adjustmentWorkingSetDelta = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            ImageAdjustmentResult? result = null;
            var measured = await MeasureAsync(async () => result = await adjustment.AdjustAsync(new(
                source, new(Brightness: 0.01))));
            Assert.True(result!.Succeeded, result.DiagnosticCode);
            Assert.Equal((6000, 1801), (result.Image!.Width, result.Image.Height));
            adjustmentSamples.Add(measured.ElapsedMilliseconds);
            adjustmentAllocated += measured.AllocatedBytes;
            adjustmentWorkingSetDelta = Math.Max(adjustmentWorkingSetDelta, measured.WorkingSetDeltaBytes);
        }

        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(source.Pixels.AsSpan())));
        var resizeStats = PerformanceStatistics.From(resizeSamples);
        var adjustmentStats = PerformanceStatistics.From(adjustmentSamples);
        WriteEvidence(
            "resize-6000x1801",
            $"source=6000x1801;sourceBytes={source.Pixels.Length};target=3000x901;filter=Linear",
            3,
            resizeStats.Minimum,
            resizeStats.Median,
            resizeStats.Maximum,
            null,
            resizeAllocated,
            resizeWorkingSetDelta,
            "warm JIT; managed allocation is process-wide observation across measured runs");
        WriteEvidence(
            "adjustment-6000x1801",
            $"source=6000x1801;sourceBytes={source.Pixels.Length};brightness=0.01",
            3,
            adjustmentStats.Minimum,
            adjustmentStats.Median,
            adjustmentStats.Maximum,
            null,
            adjustmentAllocated,
            adjustmentWorkingSetDelta,
            "warm JIT; simple adjustment; managed allocation is process-wide observation across measured runs");
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Coverage", "Plan100")]
    public async Task Background_manager_reports_large_batch_throughput_and_dispatch_responsiveness()
    {
        const int jobCount = 320;
        const int concurrency = 4;
        await using var manager = new BackgroundTaskManager(
            new(concurrency, jobCount, jobCount + 8),
            NullLogger<BackgroundTaskManager>.Instance);
        await manager.StartAsync(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var started = 0;
        long firstRunningTimestamp = 0;
        var measurementStart = Stopwatch.GetTimestamp();
        manager.Notification += (_, notification) =>
        {
            if (notification.Snapshot.State == BackgroundTaskState.Running)
            {
                Interlocked.CompareExchange(ref firstRunningTimestamp, Stopwatch.GetTimestamp(), 0);
            }
        };
        var beforeAllocated = GC.GetTotalAllocatedBytes(false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var queued = new List<BackgroundTaskEnqueueResult>(jobCount);
        for (var index = 0; index < jobCount; index++)
        {
            queued.Add(await manager.EnqueueAsync(new(BackgroundTaskKind.Thumbnail, async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (Interlocked.Increment(ref started) == concurrency)
                {
                    allWorkersStarted.TrySetResult();
                }
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return BackgroundTaskExecutionResult.Success();
            })));
        }

        Assert.All(queued, item => Assert.True(item.Succeeded, item.DiagnosticCode));
        await allWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        var completed = await Task.WhenAll(queued.Select(item => manager.WaitForCompletionAsync(item.TaskId)));
        var elapsed = Stopwatch.GetElapsedTime(measurementStart);
        process.Refresh();
        var workingSetDelta = process.WorkingSet64 - workingSetBefore;
        var allocated = GC.GetTotalAllocatedBytes(false) - beforeAllocated;

        Assert.All(completed, item => Assert.Equal(BackgroundTaskState.Succeeded, item!.State));
        Assert.Equal(concurrency, maximumActive);
        Assert.True(firstRunningTimestamp > measurementStart);
        var firstRunning = Stopwatch.GetElapsedTime(measurementStart, firstRunningTimestamp).TotalMilliseconds;
        WriteEvidence(
            "background-batch-320",
            $"jobs={jobCount};configuredConcurrency={concurrency};observedConcurrency={maximumActive}",
            1,
            elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            null,
            allocated,
            workingSetDelta,
            $"orchestration-only;firstRunningMs={F(firstRunning)};jobsPerSecond={F(jobCount / elapsed.TotalSeconds)};bounded queue; cancellation/backpressure reuse existing correctness tests");
    }

    private static async Task ReadAllMetadataAsync(DdsMetadataReader reader, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var result = await reader.ReadAsync(path);
            Assert.True(result.IsSuccess, $"{Path.GetFileName(path)}:{result.ErrorCode}");
        }
    }

    private static async Task<Measurement> MeasureAsync(Func<Task> operation)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var started = Stopwatch.GetTimestamp();
        await operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        process.Refresh();
        return new(elapsed.TotalMilliseconds, allocated, process.WorkingSet64 - workingSetBefore);
    }

    private void WriteEvidence(
        string scenario,
        string input,
        int runs,
        double minimum,
        double median,
        double maximum,
        double? p95,
        long allocatedBytes,
        long workingSetDeltaBytes,
        string notes)
    {
        output.WriteLine(
            "PLAN 100 PERF|scenario={0}|input={1}|runs={2}|minMs={3}|medianMs={4}|maxMs={5}|p95Ms={6}|allocatedBytes={7}|workingSetDeltaBytes={8}|status=INFORMATIONAL|notes={9}",
            scenario,
            input,
            runs,
            F(minimum),
            F(median),
            F(maximum),
            p95 is null ? "NA" : F(p95.Value),
            allocatedBytes,
            workingSetDeltaBytes,
            notes);
    }

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static InternalImage CreatePatternImage(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var pixel = index / 4;
            pixels[index] = (byte)(pixel % 251);
            pixels[index + 1] = (byte)((pixel / width) % 241);
            pixels[index + 2] = (byte)((pixel / 17) % 239);
            pixels[index + 3] = (byte)(128 + pixel % 128);
        }
        return new(
            width,
            height,
            checked(width * 4),
            pixels,
            new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void RequireFile(string path, string expectedHash, string skipReason)
    {
        if (!File.Exists(path)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), expectedHash,
                StringComparison.Ordinal))
        {
            throw SkipException.ForSkip(skipReason);
        }
    }

    private static string RequireApprovedTexconv()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 100 DirectXTex evidence requires Windows.");
        }
        var path = Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw SkipException.ForSkip("PLAN 100 requires approved texconv.exe.");
        }
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, DirectXTexEvaluationToolCatalog.May2026X64.Sha256,
                StringComparison.Ordinal))
        {
            throw SkipException.ForSkip("PLAN 100 approved texconv.exe hash does not match policy.");
        }
        return Path.GetFullPath(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed record Measurement(double ElapsedMilliseconds, long AllocatedBytes, long WorkingSetDeltaBytes);

    private sealed record PerformanceStatistics(double Minimum, double Median, double Maximum, double? P95)
    {
        public static PerformanceStatistics From(IEnumerable<double> samples)
        {
            var ordered = samples.Order().ToArray();
            if (ordered.Length == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(samples));
            }
            var middle = ordered.Length / 2;
            var median = ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2d
                : ordered[middle];
            double? p95 = null;
            if (ordered.Length >= 20)
            {
                var index = Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
                p95 = ordered[index];
            }
            return new(ordered[0], median, ordered[^1], p95);
        }
    }

    private sealed class TestSecureWorkspace : ISecureWorkspace
    {
        public TestSecureWorkspace(string root)
        {
            Id = Guid.NewGuid().ToString("N");
            Paths = new(
                root,
                Path.Combine(root, "Working"),
                Path.Combine(root, "Extracted"),
                Path.Combine(root, "BuildOutput"));
            Directory.CreateDirectory(Paths.WorkingDirectory);
            Directory.CreateDirectory(Paths.ExtractedDirectory);
            Directory.CreateDirectory(Paths.BuildOutputDirectory);
        }

        public string Id { get; }
        public SecureWorkspacePaths Paths { get; }
        public string ResolveRelativePath(string relativePath) =>
            new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestProjectWorkspace : IProjectArchiveWorkspace
    {
        public TestProjectWorkspace(ISecureWorkspace secure)
        {
            var template = new AuditionArchiveTemplate(
                "archive-015", "015.ab", "015.ab", ArchiveEngineType.AcvTool5,
                GameRegionProfile.AuditionVietnam.RegionId, "015", "1", ArchiveHash, "audition-vn-fixture");
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(
                1,
                Guid.NewGuid(),
                "PLAN 100 thumbnail",
                secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab",
                "Extracted/015",
                "BuildOutput",
                null,
                ".project-archive-workspace.json",
                new string('A', 64),
                now,
                now,
                ProjectArchiveWorkspaceState.Ready);
        }

        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
