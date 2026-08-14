using System.Reflection;

var markerConfig = Path.Combine(AppContext.BaseDirectory, "e2e-marker-path.txt");
if (!File.Exists(markerConfig)) return 2;
var markerPath = (await File.ReadAllTextAsync(markerConfig)).Trim();
if (!Path.IsPathFullyQualified(markerPath)) return 3;
Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
await File.WriteAllTextAsync(markerPath, version);
var delayConfig = Path.Combine(AppContext.BaseDirectory, "e2e-delay-ms.txt");
if (File.Exists(delayConfig) && int.TryParse((await File.ReadAllTextAsync(delayConfig)).Trim(), out var delay)
    && delay is > 0 and <= 30_000)
    await Task.Delay(delay);
return 0;
