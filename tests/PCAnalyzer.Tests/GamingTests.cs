using PCAnalyzer.Core;
using PCAnalyzer.Gaming;
using Xunit;

namespace PCAnalyzer.Tests;

public sealed class GamingTests
{
    [Fact]
    public void UnrepresentableFpsIsUnavailableAndSerializable()
    {
        var statistics = FrameStatisticsCalculator.Calculate(Enumerable.Repeat(double.Epsilon, 10_000), 30);
        Assert.Null(statistics.AverageFps);
        Assert.Null(statistics.OnePercentLowFps);
        Assert.Null(statistics.PointOnePercentLowFps);
        Assert.Equal(PCAnalyzer.Core.Availability.InsufficientData, statistics.Status);
        Assert.Contains(statistics.Limitations, limitation => limitation.Contains("finito", StringComparison.Ordinal));
        var serialized = System.Text.Json.JsonSerializer.Serialize(statistics);
        Assert.DoesNotContain("Infinity", serialized);
    }

    [Fact]
    public async Task ModernFramesKeepDroppedAndDisplayCadenceDistinct()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPUBusy,DisplayLatency,DisplayedTime
            game.exe,42,0xABC,1000,10,8,2,7,15,16.6667
            game.exe,42,0xABC,1010,20,15,5,12,NA,NA
            game.exe,42,0xABC,1030,30,25,5,20,18,33.3333
            """);
        var stream = Assert.Single(result.Streams);
        Assert.Equal(1, stream.DroppedFrameCount);
        Assert.Equal(0, stream.UnknownDisplayStateCount);
        Assert.Equal(1000, stream.Frames[0].TimestampMilliseconds);
        Assert.Equal(50d, stream.ApplicationStatistics.AverageFps);
        Assert.Equal(40d, stream.DisplayedStatistics.AverageFps!.Value, 3);
        Assert.Equal(3, stream.ApplicationStatistics.SampleCount);
        Assert.Equal(2, stream.DisplayedStatistics.SampleCount);
        Assert.True(stream.Frames[1].Dropped);
        Assert.Null(stream.Frames[1].DisplayedTimeMilliseconds);
    }

    [Fact]
    public async Task LegacySecondsConvertOnlyTimestampsAndPreserveMillisecondDurations()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,Runtime,Dropped,TimeInSeconds,msBetweenPresents,msBetweenDisplayChange
            game.exe,7,0x11,DXGI,0,1.5,10,16
            game.exe,7,0x11,DXGI,1,1.51,20,0
            game.exe,7,0x11,DXGI,0,1.53,30,34
            """);
        var stream = Assert.Single(result.Streams);
        Assert.Equal(1500, stream.Frames[0].TimestampMilliseconds);
        Assert.Equal(20d, stream.Frames[1].FrameTimeMilliseconds);
        Assert.Equal(50d, stream.ApplicationStatistics.AverageFps);
        Assert.Equal(1, stream.DroppedFrameCount);
    }

    [Fact]
    public async Task StreamsNeverMergeDifferentPidSwapChainApplicationOrFrameType()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime,FrameType
            game.exe,1,0xA,0,10,Application
            game.exe,2,0xA,0,20,Application
            game.exe,1,0xB,0,30,Application
            other.exe,1,0xA,0,40,Application
            game.exe,1,0xA,10,50,Generated
            """);
        Assert.Equal(5, result.Streams.Count);
        Assert.All(result.Streams, stream => Assert.Single(stream.Frames));
        var filtered = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime
            game.exe,1,0xA,0,10
            game.exe,2,0xA,0,20
            game.exe,1,0xB,0,30
            other.exe,1,0xA,0,40
            """, new(ProcessId: 1, Application: "GAME.EXE", SwapChainAddress: "0x000a"));
        Assert.Single(filtered.Streams);
        Assert.Equal(3, filtered.FilteredRecords);
    }

    [Fact]
    public async Task TimestampResetSplitsSegmentAndInvalidTimestampIsNotFabricated()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime
            game.exe,1,0xA,100,10
            game.exe,1,0xA,-1,10
            game.exe,1,0xA,110,10
            game.exe,1,0xA,0,20
            game.exe,1,0xA,20,20
            """);
        Assert.Equal(1, result.InvalidRecords);
        Assert.Equal(2, result.Streams.Count);
        Assert.Equal(0, result.Streams[0].Identity.Segment);
        Assert.Equal(1, result.Streams[1].Identity.Segment);
        Assert.Equal(2, result.Streams[0].Frames.Count);
        Assert.Equal(2, result.Streams[1].Frames.Count);
    }

    [Fact]
    public async Task RawQpcRequiresMeasuredFrequency()
    {
        const string csv = "Application,ProcessID,SwapChainAddress,CPUStartQPC,FrameTime\ngame.exe,1,0x1,3000000,10";
        await Assert.ThrowsAsync<PresentMonFormatException>(() => Parse(csv));
        var result = await Parse(csv, new(QpcFrequencyHz: 1_000_000));
        Assert.Equal(3000, Assert.Single(result.Streams).Frames[0].TimestampMilliseconds);
    }

    [Fact]
    public async Task CurrentSchemaRecognizesExplicitSecondsAndUnknownDroppedRemainsUnknown()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTimeInSeconds,MsBetweenAppStart,MsCPUBusy,MsCPUWait
            game.exe,1,0x1,2,16,12,4
            game.exe,1,0x1,2.016,16,12,4
            """);
        var stream = Assert.Single(result.Streams);
        Assert.Equal(2000, stream.Frames[0].TimestampMilliseconds);
        Assert.Equal(62.5, stream.ApplicationStatistics.AverageFps);
        Assert.Equal(2, stream.UnknownDisplayStateCount);
        Assert.Equal(0, stream.DroppedFrameCount);
        Assert.Null(stream.DisplayedStatistics.AverageFps);
    }

    [Fact]
    public async Task ImportLimitIsExplicitAndCsvEscapingIsSupported()
    {
        var result = await Parse("""
            Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime
            "game, ""test"".exe",1,0x1,0,16
            "game, ""test"".exe",1,0x1,16,16
            """, new(MaximumFrames: 1));
        Assert.False(result.Complete);
        Assert.Equal("game, \"test\".exe", Assert.Single(result.Streams).Identity.Application);
        Assert.Single(result.Streams[0].Frames);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void FpsUsesInverseMeanDurationAndLowUsesSlowTailMean()
    {
        var result = FrameStatisticsCalculator.Calculate(Enumerable.Repeat(10d, 990).Concat(Enumerable.Repeat(100d, 10)), 30);
        Assert.Equal(1000 / 10.9, result.AverageFps!.Value, 10);
        Assert.Equal(10d, result.OnePercentLowFps);
        Assert.Null(result.PointOnePercentLowFps);
        Assert.Equal(10d, result.P95FrameTimeMilliseconds);
        Assert.Equal(10.9d, result.P99FrameTimeMilliseconds!.Value, 8);
        Assert.Equal(100d, result.P999FrameTimeMilliseconds);
        Assert.Equal(Availability.Available, result.Status);
    }

    [Fact]
    public void RareTailRequiresTenActualSamples()
    {
        var result = FrameStatisticsCalculator.Calculate(Enumerable.Repeat(10d, 9990).Concat(Enumerable.Repeat(100d, 10)), 30);
        Assert.Equal(10d, result.PointOnePercentLowFps);
        Assert.Equal(1000 / 19d, result.OnePercentLowFps!.Value, 10);
        var shortResult = FrameStatisticsCalculator.Calculate([10d, 30d], .04);
        Assert.Equal(50d, shortResult.AverageFps);
        Assert.Null(shortResult.OnePercentLowFps);
        Assert.Null(shortResult.P95FrameTimeMilliseconds);
        Assert.Equal(Availability.InsufficientData, shortResult.Status);
    }

    [Fact]
    public async Task LocalDatesRequireExplicitCaptureOffset()
    {
        const string csv = "Application,ProcessID,SwapChainAddress,CPUStartDateTime,FrameTime\ngame.exe,1,0x1,2026-10-14 12:00:00.123456789,10";
        await Assert.ThrowsAsync<PresentMonFormatException>(() => Parse(csv));
        var result = await Parse(csv, new(DateTimeUtcOffset: TimeSpan.FromHours(-3)));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() + 123,
            Assert.Single(result.Streams).Frames[0].TimestampMilliseconds);
    }

    [Fact]
    public async Task HugeSingleRecordStopsAfterOneBoundedBufferInsteadOfReadingWholeLine()
    {
        using var source = new InfiniteRecordReader();
        await Assert.ThrowsAsync<PresentMonFormatException>(() => new PresentMonCsvParser().ParseAsync(source,
            new(MaximumRecordCharacters: 128)));
        Assert.InRange(source.CharactersRead, 1, 4096);
    }

    [Fact]
    public async Task QuotedMultilineFieldsRemainOneRecord()
    {
        var result = await Parse("Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\r\n\"game,\n\"\"test\"\".exe\",1,0x1,0,16\r\n");
        Assert.Equal("game,\n\"test\".exe", Assert.Single(result.Streams).Identity.Application);
        Assert.Equal(1, result.RecordsRead);
    }

    [Fact]
    public async Task AmbiguousLegacyQpcHeaderDoesNotInventUnits()
    {
        await Assert.ThrowsAsync<PresentMonFormatException>(() => Parse("Application,ProcessID,SwapChainAddress,QPCTime,msBetweenPresents\ngame.exe,1,0x1,30000,16"));
    }

    [Fact]
    public void LegacyCaptureHistoryDoesNotInventSignatureVerificationOrCancellation()
    {
        const string legacyJson = """
            {"Status":0,"Detail":"Legacy capture","OutputCsvPath":"capture.csv",
             "Tool":{"ExecutablePath":"PresentMon.exe","Status":0,"Version":"2.0.0","CliStyle":1,
                     "Detail":"Legacy inspection","SupportedOptions":[]},
             "Import":{"Streams":[],"RecordsRead":0,"InvalidRecords":0,"FilteredRecords":0,"Complete":true,"Warnings":[]},
             "ExitCode":0,"ProcessOutput":""}
            """;
        var result = System.Text.Json.JsonSerializer.Deserialize<PresentMonCaptureResult>(legacyJson)!;
        Assert.False(result.Cancelled);
        Assert.False(result.Import!.Cancelled);
        Assert.Null(result.Import.ErrorDetail);
        Assert.Equal(PresentMonSignatureStatus.NotChecked, result.Tool!.SignatureStatus);
        Assert.Null(result.Tool.Publisher);
    }

    [Fact]
    public async Task TruncatedTrailingRecordPreservesEarlierFramesAndMarksReadFailure()
    {
        var result = await Parse("Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\ngame.exe,1,0x1,0,16\n\"unfinished");
        Assert.False(result.Complete);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.ErrorDetail);
        Assert.Equal(16, Assert.Single(Assert.Single(result.Streams).Frames).FrameTimeMilliseconds);
        Assert.Contains(result.Warnings, warning => warning.Contains("preservados"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAndIoFailurePreserveFramesAlreadyRead(bool cancellation)
    {
        using var cts = new CancellationTokenSource();
        using var reader = new InterruptingReader(cts, cancellation);
        var result = await new PresentMonCsvParser().ParseAsync(reader, cancellationToken: cts.Token);
        Assert.False(result.Complete);
        Assert.Equal(cancellation, result.Cancelled);
        Assert.Equal(cancellation, result.ErrorDetail is null);
        Assert.Equal(1, result.RecordsRead);
        Assert.Single(Assert.Single(result.Streams).Frames);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveringCancelledOrFailedCapturePreservesCsvAndNeverReportsCompletion(bool cancelled)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        const string csv = "Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\ngame.exe,42,0x1,0,16\n\"unfinished";
        await File.WriteAllTextAsync(path, csv);
        try
        {
            var request = new PresentMonCaptureRequest("not-executed.exe", 42, TimeSpan.FromSeconds(1), path);
            var result = cancelled
                ? await PresentMonPartialCapture.ReadCancelledAsync(request, null, "Fixture cancellation")
                : await PresentMonPartialCapture.ReadFailureAsync(request, null, "Fixture failure", exitCode: 7, processOutput: "failure");
            Assert.Equal(cancelled, result.Cancelled);
            Assert.Equal(Availability.ReadError, result.Status);
            Assert.NotNull(result.Import);
            Assert.False(result.Import.Complete);
            Assert.Single(Assert.Single(result.Import.Streams).Frames);
            Assert.Equal(csv, await File.ReadAllTextAsync(path));
            if (!cancelled) { Assert.Equal(7, result.ExitCode); Assert.Equal("failure", result.ProcessOutput); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledCaptureWithReadableFramesKeepsDataButMarksBothCaptureAndImportPartial()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        const string csv = "Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\ngame.exe,42,0x1,0,16\ngame.exe,42,0x1,16,17\n";
        await File.WriteAllTextAsync(path, csv);
        try
        {
            var result = await PresentMonPartialCapture.ReadCancelledAsync(new("unused.exe", 42, TimeSpan.FromSeconds(1), path), null, "Cancelled");
            Assert.Equal(Availability.Available, result.Status); // Availability describes retained measurements, not completion.
            Assert.True(result.Cancelled);
            Assert.True(result.Import!.Cancelled);
            Assert.False(result.Import.Complete);
            Assert.Null(result.Import.ErrorDetail);
            var stream = Assert.Single(result.Import.Streams);
            Assert.Equal(2, stream.ApplicationStatistics.SampleCount);
            Assert.Contains(stream.Limitations, limitation => limitation.Contains("Captura cancelada"));
            Assert.Equal(csv, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledCaptureWithoutCsvDoesNotInventEmptySuccessfulData()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        var result = await PresentMonPartialCapture.ReadCancelledAsync(new("unused.exe", 1, TimeSpan.FromSeconds(1), path), null, "Cancelled");
        Assert.True(result.Cancelled);
        Assert.Equal(Availability.InsufficientData, result.Status);
        Assert.Null(result.Import);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Intel Corporation", true)]
    [InlineData("Intel(R) Corporation", true)]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("Intel Corporation Fake", false)]
    [InlineData("Fake Microsoft Corporation", false)]
    [InlineData(null, false)]
    public void PublisherNamesRequireExactRecognizedIdentity(string? publisher, bool expected) =>
        Assert.Equal(expected, PresentMonSignatureVerifier.IsExpectedPublisher(publisher));

    [Fact]
    public void AuthenticodeStructuresMatchWindowsNativeAbi()
    {
        var type = typeof(PresentMonSignatureVerifier);
        var flags = System.Reflection.BindingFlags.NonPublic;
        var file = type.GetNestedType("TrustFile", flags)!;
        var data = type.GetNestedType("TrustData", flags)!;
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16, System.Runtime.InteropServices.Marshal.SizeOf(file));
        Assert.Equal(IntPtr.Size == 8 ? 88 : 52, System.Runtime.InteropServices.Marshal.SizeOf(data));
        Assert.Equal(IntPtr.Size == 8 ? 40 : 24, System.Runtime.InteropServices.Marshal.OffsetOf(data, "FileInfo").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 80 : 48, System.Runtime.InteropServices.Marshal.OffsetOf(data, "SignatureSettings").ToInt32());
    }

    [Fact]
    public async Task UnsignedProgramIsRejectedBeforeItsHelpCanRunOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The test assembly has no Authenticode signature. Its filename does not grant execution trust.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(typeof(GamingTests).Assembly.Location, path);
        try
        {
            var result = await new PresentMonCaptureService().InspectAsync(path);
            Assert.Equal(Availability.Unsupported, result.Status);
            Assert.Equal(PresentMonSignatureStatus.Unsigned, result.SignatureStatus);
            Assert.Empty(result.SupportedOptions);
        }
        finally { File.Delete(path); }
    }

    private sealed class InterruptingReader(CancellationTokenSource cts, bool cancel) : TextReader
    {
        private bool first = true;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (first)
            {
                first = false;
                const string chunk = "Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\ngame.exe,1,0x1,0,16\n";
                chunk.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(chunk.Length);
            }
            if (cancel) { cts.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new IOException("Fixture interrupted read");
        }
    }

    private sealed class InfiniteRecordReader : TextReader
    {
        private const string Prefix = "Application,ProcessID,SwapChainAddress,CPUStartTime,FrameTime\n";
        public long CharactersRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer.Span[i] = CharactersRead < Prefix.Length ? Prefix[(int)CharactersRead] : 'x';
                CharactersRead++;
            }
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private static Task<PresentMonImportResult> Parse(string csv, PresentMonParseOptions? options = null) =>
        new PresentMonCsvParser().ParseAsync(new StringReader(csv), options);
}
