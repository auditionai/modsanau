namespace AuditionModStudio.Archives;

public sealed record AcvTool5RunResult(
    AcvTool5Operation Operation,
    AcvTool5RunnerState State,
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    KeydatStatus KeydatStatusBefore,
    KeydatStatus KeydatStatusAfter,
    bool CountrySelectionSent,
    IReadOnlyList<AcvTool5Progress> Progress,
    IReadOnlyList<string> Diagnostics);
