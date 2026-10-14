using PCAnalyzer.Core;

namespace PCAnalyzer.Gaming;

/// <summary>A swap chain belongs to one process/application; a timestamp reset starts a new segment.</summary>
public sealed record FrameStreamIdentity(string Application, int ProcessId, string SwapChainAddress, int Segment = 0,
    string? FrameType = null);

public sealed record FrameSample(long RecordNumber, double TimestampMilliseconds, double? FrameTimeMilliseconds,
    bool? Dropped, double? DisplayedTimeMilliseconds, double? CpuBusyMilliseconds = null,
    double? CpuWaitMilliseconds = null, double? GpuBusyMilliseconds = null,
    double? DisplayLatencyMilliseconds = null, double? InputLatencyMilliseconds = null, string? FrameType = null);

public sealed record FrameStatistics(int SampleCount, double? MeanFrameTimeMilliseconds, double? AverageFps,
    double? P95FrameTimeMilliseconds, double? P99FrameTimeMilliseconds, double? P999FrameTimeMilliseconds,
    double? OnePercentLowFps, double? PointOnePercentLowFps, Availability Status,
    IReadOnlyList<string> Limitations);

public sealed record FrameStream(FrameStreamIdentity Identity, string DurationSource, string TimestampSource,
    string TimestampBasis, IReadOnlyList<FrameSample> Frames, int DroppedFrameCount, int UnknownDisplayStateCount,
    FrameStatistics ApplicationStatistics, FrameStatistics DisplayedStatistics, IReadOnlyList<string> Limitations);

public sealed record PresentMonImportResult(IReadOnlyList<FrameStream> Streams, long RecordsRead,
    long InvalidRecords, long FilteredRecords, bool Complete, IReadOnlyList<string> Warnings);

public sealed record PresentMonParseOptions(int? ProcessId = null, string? Application = null,
    string? SwapChainAddress = null, double? QpcFrequencyHz = null, TimeSpan? DateTimeUtcOffset = null,
    int MaximumFrames = 1_000_000, int MaximumStreams = 256, int MaximumRecordCharacters = 65_536);

public enum PresentMonCliStyle { Unsupported, Modern, Legacy }

public sealed record PresentMonToolInspection(string ExecutablePath, Availability Status, string? Version,
    PresentMonCliStyle CliStyle, string Detail, IReadOnlyList<string> SupportedOptions);

/// <summary>Live capture is deliberately scoped to a selected PID and a new CSV destination.</summary>
public sealed record PresentMonCaptureRequest(string ExecutablePath, int ProcessId, TimeSpan Duration,
    string OutputCsvPath, PresentMonParseOptions? ParseOptions = null);

public sealed record PresentMonCaptureResult(Availability Status, string Detail, string OutputCsvPath,
    PresentMonToolInspection? Tool, PresentMonImportResult? Import = null, int? ExitCode = null,
    string? ProcessOutput = null);
