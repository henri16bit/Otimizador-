using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCAnalyzer.App.Services;

/// <summary>Available before WPF resources or the application backend are initialized.</summary>
internal sealed class StartupDiagnostics
{
    private readonly string _smokeOutput;
    private int _failureReported;
    public bool IsSmokeTest { get; }

    public StartupDiagnostics(string[] arguments)
    {
        IsSmokeTest = arguments.Contains("--smoke-test", StringComparer.Ordinal);
        var outputIndex = Array.IndexOf(arguments, "--smoke-output");
        _smokeOutput = outputIndex >= 0 && outputIndex + 1 < arguments.Length
            ? arguments[outputIndex + 1] : Path.Combine(Path.GetTempPath(), "PCAnalyzer-smoke-result.json");
    }

    public void ReportSmokeSuccess(int views, int detailViews, int categoryViews) => WriteSmokeResult(new
    {
        success = true, views, detailViews, categoryViews, compactLayoutChecked = true, passiveHardwareStarted = false, timestamp = DateTimeOffset.UtcNow
    });

    public void CaptureView(FrameworkElement element, string name)
    {
        var path = Path.GetFullPath(Path.ChangeExtension(_smokeOutput, null) + "-" + name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        // Render the element's local viewport. Rendering it directly retains its layout
        // offset (for example a modal's margin), shifting and clipping the PNG.
        // VisualBrush also includes its root's offset in content coordinates; select that
        // source rectangle explicitly and map it to the output viewport at the origin.
        var offset = VisualTreeHelper.GetOffset(element);
        var brush = new VisualBrush(element)
        {
            AutoLayoutContent = false,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(offset.X, offset.Y, bounds.Width, bounds.Height),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = bounds,
            Stretch = Stretch.Fill
        };
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(brush, null, bounds);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void ReportFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        var detail = exception.ToString();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) detail = detail.Replace(profile, "<user>", StringComparison.OrdinalIgnoreCase);
        if (detail.Length > 16_384) detail = detail[..16_384];
        string? reportPath = null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var directory = Path.Combine(root, "PCAnalyzer", "logs");
                Directory.CreateDirectory(directory);
                reportPath = Path.Combine(directory, "startup-last-error.txt");
                File.WriteAllText(reportPath, $"{DateTimeOffset.UtcNow:O}\n{detail}\n", Encoding.UTF8);
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (IsSmokeTest)
        {
            try { WriteSmokeResult(new { success = false, error = detail, timestamp = DateTimeOffset.UtcNow }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return;
        }
        var message = "O PC Performance Analyzer não conseguiu iniciar ou encontrou uma falha fatal.\n\n" +
            exception.GetType().Name + $" (0x{exception.HResult:X8})\n\n" +
            (reportPath is null ? "Não foi possível salvar o registro de diagnóstico." : "Detalhes salvos em:\n" + reportPath);
        // A native message box still works if WPF resource loading itself failed.
        MessageBoxW(IntPtr.Zero, message, "PC Performance Analyzer — falha de inicialização", 0x10);
    }

    private void WriteSmokeResult(object result)
    {
        var path = Path.GetFullPath(_smokeOutput);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result), Encoding.UTF8);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
