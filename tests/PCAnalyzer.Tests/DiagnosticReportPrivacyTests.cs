using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Reports;

namespace PCAnalyzer.Tests;

public sealed class DiagnosticReportPrivacyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    [Fact]
    public void NewDiagnosticStructureAndProcessesAreRedactedWithoutChangingMeasurements()
    {
        var run = CreateRun();
        var json = TestReportExporter.ExportJson(run);
        var html = TestReportExporter.ExportHtml(run);
        foreach (var output in new[] { json, html })
            foreach (var identifier in new[] { "private-device-key", "office-fixture-machine", "SERIAL-PRIVATE-123", "192.168.4.51", "Alice", "private-secret-token" })
                Assert.DoesNotContain(identifier, output, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");
        var report = result.GetProperty("report");
        Assert.Equal("Partial", report.GetProperty("completion").GetString());
        Assert.Equal("Attention", report.GetProperty("components")[0].GetProperty("state").GetString());
        Assert.Equal(12.125, report.GetProperty("components")[0].GetProperty("evidence")[0].GetProperty("value").GetDouble());
        Assert.Equal(Now, report.GetProperty("stages")[0].GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("PermissionRequired", report.GetProperty("stages")[0].GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("session").GetProperty("processes")[0].GetProperty("processId").GetInt32());
        Assert.Equal(1024, result.GetProperty("session").GetProperty("processes")[0].GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(42, run.Result!.Session!.Processes![0].ProcessId);
        Assert.Contains("office-fixture-machine", run.Result.Report!.Summary);
        Assert.Contains("office-fixture-machine", TestReportExporter.ExportJson(run, anonymize: false));
    }

    [Fact]
    public void HtmlEscapesNewReportFieldsAndDisplaysConclusionEvidenceAndCoverage()
    {
        var html = TestReportExporter.ExportHtml(CreateRun(), anonymize: false);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Conclusão da análise", html);
        Assert.Contains("Avaliação por componente", html);
        Assert.Contains("Etapas executadas e cobertura", html);
        Assert.Contains("12.125", html);
        Assert.Contains("PermissionRequired", html);
    }

    private static TestSession CreateRun()
    {
        const string privateText = @"office-fixture-machine SERIAL-PRIVATE-123 192.168.4.51 C:\Users\Alice\trace.etl";
        var evidence = new Evidence(privateText, privateText, "private-device-key.usage", 12.125, "ms", Now);
        var action = new SuggestedNextAction("private-device-key", privateText, privateText, privateText);
        var finding = new Finding("private-device-key", privateText, "<script>" + privateText, privateText,
            FindingCategory.PossibleProblem, Severity.Warning, Confidence.Low, [evidence], privateText, privateText, privateText);
        var report = new DiagnosticReport(DiagnosticCompletion.Partial, privateText,
            [new("private-device-key", privateText, DiagnosticAssessmentState.Attention, privateText,
                [evidence], [privateText], Confidence.Low, action)], [finding], [action], [privateText],
            [new("private-device-key", privateText, Availability.PermissionRequired, "token=private-secret-token", Now, Now.AddSeconds(1))]);
        var session = new DiagnosticSession(Guid.NewGuid(), "Fixture", Now, Now.AddSeconds(1), new("Fixture", "manual", 1),
            new(Now, [new("private-device-key", "CPU", "CPU fixture", [new("HostName", "office-fixture-machine", "fixture"),
                new("SerialNumber", "SERIAL-PRIVATE-123", "fixture")])], []), [], [],
            Processes: [new(42, @"C:\Users\Alice\fixture.exe", 12.125, 1024, Now, Limitation: privateText)]);
        return new(Guid.NewGuid(), "general", "Fixture", TestStatus.Partial, Now, Now.AddSeconds(1), new("Finished"),
            new TechnicalResult("Fixture", [], Session: session, Report: report));
    }
}
