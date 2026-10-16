using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LyricMotion;
using SkiaSharp;

internal static class MotionTests
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _passed++;
    }
    public static int Main()
    {
        try
        {
            AppBuilder.Configure<Application>().UseSkia().UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            foreach (var mode in new[] { Tuning.Handover.Crisp, Tuning.Handover.Smooth, Tuning.Handover.Flowing })
                foreach (var zoom in new[] { 1.0, 1.375 })
                    foreach (var duration in new[] { 80.0, 800.0 }) Probe(zoom, mode, duration);
            Console.WriteLine($"PASS: {_passed} motion checks; headless rendering only, no playback or library writes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Probe(double zoom, Tuning.Handover mode, double duration)
    {
        var word = new TimedWord { HeldDurationMs = duration };
        var baseline = new TextBlock { Text = "Motion", FontSize = 46, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Opacity = 1 };
        baseline.Classes.Add("word-base");
        var sweep = new TextBlock { Text = "Motion", FontSize = 46, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        var inner = new Panel { Children = { baseline, sweep } };
        var cell = new Panel { Children = { new Viewbox { Child = inner } } };
        var root = new Canvas { Background = Brushes.Black, RenderTransform = new ScaleTransform(zoom, zoom), RenderTransformOrigin = RelativePoint.TopLeft };
        root.Children.Add(cell); Canvas.SetLeft(cell, 70.3); Canvas.SetTop(cell, 90.7);
        var window = new Window { Width = 500, Height = 270, Background = Brushes.Black, Content = root };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var tuning = new Tuning();
        typeof(Tuning).GetProperty("Animation")!.SetValue(tuning, mode);
        using var rig = new WordRig(word, WordAccess.For(word)!, cell, 0, 1, false, tuning, false, -1, -1);
        for (int i = 0; i < 720; i++)
        {
            word.Progress = Math.Min(3, i / 60.0);
            rig.Step(1.0 / 240, true, false, 0, zoom, tuning);
        }
        var pictures = mode == Tuning.Handover.Flowing && zoom == 1.375 && duration == 800;
        var before = Center(window, pictures ? "handover-before.png" : null);
        Check(double.IsFinite(before.X) && Field(rig, "_letters") != null, "A sung word is rendered by its letter layer before handover");
        var prior = before;
        double largest = 0;
        for (int i = 0; i < 80; i++)
        {
            rig.Step(1.0 / 240, false, true, 0, zoom, tuning);
            var now = Center(window, pictures && i == 14 ? "handover-mid.png" : null);
            largest = Math.Max(largest, Math.Sqrt(Math.Pow(now.X-prior.X,2)+Math.Pow(now.Y-prior.Y,2)));
            prior = now;
            if (i == 3) Check(Field(rig, "_letters") is LetterLayer { Releasing: true } && !rig.IsAtRest, "A fast next line cannot dispose a handover while its blend is running");
        }
        var after = Center(window, pictures ? "handover-after.png" : null);
        Check(largest < .4, $"{mode} at {zoom}x: no single-frame renderer/baseline jump (largest {largest:0.000}px)");
        Check(Field(rig, "_letters") == null && rig.IsAtRest, "Stationary or unblurred finished lines release their images and settle fully");
        Check(baseline.Opacity == 1 && sweep.Opacity == 1 && baseline.Foreground == Brushes.White, "Handover restores the native text bindings and opacity");
        rig.Dispose();
        var released = Center(window);
        Check(Math.Abs(released.X-after.X) < .01 && Math.Abs(released.Y-after.Y) < .01, "Later line disposal does not drop the text by two or three pixels");
        Console.WriteLine($"{mode} zoom={zoom} word={duration}ms: largest handover step {largest:0.000}px; cleanup shift {Math.Sqrt(Math.Pow(released.X-after.X,2)+Math.Pow(released.Y-after.Y,2)):0.000}px");
        word.Progress = .5;
        using var interrupted = new WordRig(word, WordAccess.For(word)!, cell, 0, 1, false, tuning, false, -1, -1);
        for (int i = 0; i < 720; i++) { word.Progress = Math.Min(3, .5 + i / 60.0); interrupted.Step(1.0/240, true, false, 0, zoom, tuning); }
        interrupted.Step(1.0/240, false, false, 0, zoom, tuning);
        Check(Field(interrupted, "_letters") is LetterLayer { Releasing: true }, "Handover also runs when Noctis has no blur, so finished images do not accumulate");
        baseline.Opacity = .7;
        interrupted.Step(1.0/240, false, false, 0, zoom, tuning);
        Check(baseline.Opacity < .7, "A native opacity update during the blend does not bypass it");
        word.Progress = .5;
        interrupted.Step(1.0/240, true, false, 0, zoom, tuning);
        Check(Field(interrupted, "_letters") is LetterLayer { Releasing: false } && baseline.Opacity == .7 && baseline.OpacityMask is null, "Seeking back into a fading line restores its animation and keeps Noctis's latest opacity");
        for (int i = 0; i < 720; i++) { word.Progress = Math.Min(3, .5 + i / 60.0); interrupted.Step(1.0/240, true, false, 0, zoom, tuning); }
        interrupted.Step(1.0/240, false, false, 0, zoom, tuning);
        interrupted.Dispose();
        Check(baseline.Opacity == .7 && baseline.OpacityMask is null && baseline.Foreground == Brushes.White && cell.RenderTransform is null && inner.Children.Count == 2,
            "Unloading or changing song during a blend restores native text, transform and bindings without orphan layers");
        word.Progress = .5;
        using var animated = new WordRig(word, WordAccess.For(word)!, cell, 0, 1, false, tuning, false, -1, -1);
        for (int i = 0; i < 720; i++) { word.Progress = Math.Min(3, .5 + i / 60.0); animated.Step(1.0/240, true, false, 0, zoom, tuning); }
        var native = new AnimatedOpacity();
        using (baseline.Bind(Visual.OpacityProperty, native, BindingPriority.Animation))
        {
            _ = baseline.Opacity;
            native.Publish(.8);
            animated.Step(1.0/240, false, true, 0, zoom, tuning);
            var blended = baseline.Opacity;
            native.Publish(.9);
            Check(baseline.Opacity == blended, "Noctis's running opacity transition cannot overwrite the handover between frames");
            animated.Step(1.0/240, false, true, 0, zoom, tuning);
            var layer = (LetterLayer)Field(animated, "_letters")!;
            var elapsed = (double)Field(layer, "_release")!;
            Check(Math.Abs(baseline.Opacity - .9 * Curves.SmootherStep(elapsed / Tuning.LineHandoverSec)) < 1e-9, "Each blend frame uses the latest native animation value underneath");
            animated.Dispose();
            Check(baseline.Opacity == .9, "Disposing during a native fade restores its current animation value");
        }
        window.Close();
    }

    private static Point Center(Window window, string? picture = null)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(500, 270), new Vector(96, 96));
        bitmap.Render(window);
        if (picture is not null) bitmap.Save(Path.Combine(AppContext.BaseDirectory, picture), PngBitmapEncoderOptions.Default);
        using var data = new MemoryStream(); bitmap.Save(data, PngBitmapEncoderOptions.Default); data.Position = 0;
        using var pixels = SKBitmap.Decode(data);
        double weight = 0, x = 0, y = 0;
        for (int row = 0; row < pixels.Height; row++)
            for (int col = 0; col < pixels.Width; col++)
            {
                double light = pixels.GetPixel(col, row).Red;
                weight += light; x += col * light; y += row * light;
            }
        return new(x / weight, y / weight);
    }
}

public sealed class TimedWord
{
    public double Progress { get; set; }
    public double HeldDurationMs { get; set; } = 200;
    public string Text => "Motion";
    public string SweepText => Text;
    public bool IsEmphasis => HeldDurationMs >= 600;
}

internal sealed class AnimatedOpacity : IObservable<double>
{
    private IObserver<double>? _observer;
    private double _value = 1;
    public IDisposable Subscribe(IObserver<double> observer) { _observer = observer; observer.OnNext(_value); return new Stop(() => _observer = null); }
    public void Publish(double value) { _value = value; _observer?.OnNext(value); }
    private sealed class Stop(Action stop) : IDisposable { public void Dispose() => stop(); }
}
