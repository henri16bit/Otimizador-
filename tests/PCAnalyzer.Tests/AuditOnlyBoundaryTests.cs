using System.Text.Json;
using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class AuditOnlyBoundaryTests
{
    [Theory]
    [InlineData(PrivilegedAction.SetPowerPlan)]
    [InlineData(PrivilegedAction.SetAdapterProperty)]
    [InlineData((PrivilegedAction)(-1))]
    [InlineData((PrivilegedAction)99)]
    public async Task WorkerDispatchRejectsSerializedLegacyAndUnknownVerbsBeforeExecution(PrivilegedAction action)
    {
        // Exercise the same dispatch method invoked after parsing a direct worker request.
        var request = new PrivilegedRequest(action, DateTimeOffset.UtcNow, Guid.NewGuid(), "*RSS", "[\"1\"]", "[\"0\"]");
        var parsed = JsonSerializer.Deserialize<PrivilegedRequest>(JsonSerializer.Serialize(request))!;
        var calls = 0;
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => PrivilegedProtocol.DispatchReadOnlyAsync(parsed, _ =>
        {
            calls++;
            return Task.FromResult(new PrivilegedResponse(true, "must never execute"));
        }));
        Assert.Equal(0, calls);
        Assert.Equal(AuditOnlyPolicy.Explanation, error.Message);
    }

    [Fact]
    public async Task WorkerStillAllowsExplicitBoundedDpcCapture()
    {
        var request = new PrivilegedRequest(PrivilegedAction.CaptureDpc, DateTimeOffset.UtcNow, DurationSeconds: 30);
        var calls = 0;
        var result = await PrivilegedProtocol.DispatchReadOnlyAsync(request, capture =>
        {
            Assert.Same(request, capture);
            calls++;
            return Task.FromResult(new PrivilegedResponse(true, "captured", "{}"));
        });
        Assert.True(result.Success);
        Assert.Equal(1, calls);
        Assert.Equal("{}", result.PayloadJson);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    public async Task WorkerRejectsUnboundedCaptureBeforeExecutor(int seconds)
    {
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PrivilegedProtocol.DispatchReadOnlyAsync(
            new(PrivilegedAction.CaptureDpc, DateTimeOffset.UtcNow, DurationSeconds: seconds), _ =>
            {
                calls++;
                return Task.FromResult(new PrivilegedResponse(true, "must never execute"));
            }));
        Assert.Equal(0, calls);
    }
}
