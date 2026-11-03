using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

// The headless session starts before any test runs (see UiSessionFixture).
[assembly: AssemblyFixture(typeof(VmQontrol.Tests.UiSessionFixture))]

namespace VmQontrol.Tests;

/// <summary>Starts the headless app session once, before the first test of the assembly.</summary>
public sealed class UiSessionFixture : IAsyncLifetime
{
    // Awaited, never blocked on: xunit runs fixtures on its own synchronization context.
    public ValueTask InitializeAsync() => new(Ui.EnsureStartedAsync());

    public ValueTask DisposeAsync() => default;
}

/// <summary>The headless platform's app: the real styles and resources, no window opened by the app itself.</summary>
public sealed class HeadlessApp : App.App
{
    public override void OnFrameworkInitializationCompleted()
    {
    }
}

internal static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The app on Avalonia's headless platform: the real window, nothing on anybody's desktop. Avalonia is set up once per
/// process, so every UI test shares one session. A test whose body uses <see cref="Run{T}"/> is a window test and carries
/// a tier (tools/test-tiers.sh).
/// </summary>
internal static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry), AvaloniaTestIsolationLevel.PerAssembly));

    private static readonly HashSet<Avalonia.Controls.Window> OpenWindows = [];

    public static Task EnsureStartedAsync() => Session.Value.Dispatch(() =>
    {
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((w, _) => OpenWindows.Add(w));
        Avalonia.Controls.Window.WindowClosedEvent.AddClassHandler<Avalonia.Controls.Window>((w, _) => OpenWindows.Remove(w));
        return 0;
    }, CancellationToken.None);

    /// <summary>Runs a UI test's body on the session's thread; a window it leaves open is closed after it.</summary>
    public static Task<T> Run<T>(Func<T> body) => Session.Value.Dispatch(() =>
    {
        try
        {
            return body();
        }
        finally
        {
            foreach (var window in OpenWindows.ToList()) window.Close();
        }
    }, TestContext.Current.CancellationToken);

    /// <summary>Pumps until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static void PumpUntil(Func<bool> condition, string what, double seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
            Pump(0.05);
        }
    }

    /// <summary>A left click at a control's center, through the window's input like a real pointer.</summary>
    public static void Click(Avalonia.Controls.TopLevel window, Avalonia.Controls.Control target, Avalonia.Input.MouseButton button = Avalonia.Input.MouseButton.Left)
    {
        // A control in a popup (a context menu) is clicked through the popup's own top level.
        var root = Avalonia.Controls.TopLevel.GetTopLevel(target) ?? window;
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? throw new InvalidOperationException("the control is not on screen");
        root.MouseMove(center);
        root.MouseDown(center, button);
        root.MouseUp(center, button);
        Pump(0.1);
    }

    /// <summary>Saves what the window shows now to snapshots/&lt;batch&gt;/&lt;name&gt;.png for review.</summary>
    public static void Capture(Avalonia.Controls.Window window, string batch, string name)
    {
        Pump(0.1);
        var dir = Path.Combine(Path.GetDirectoryName(TestProfile.ScratchRoot)!, "snapshots", batch);
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A popup's own frame (a tooltip, a menu), saved like <see cref="Capture"/>.</summary>
    public static void CaptureTopLevel(Avalonia.Controls.TopLevel top, string batch, string name)
    {
        Pump(0.1);
        var dir = Path.Combine(Path.GetDirectoryName(TestProfile.ScratchRoot)!, "snapshots", batch);
        Directory.CreateDirectory(dir);
        using var frame = top.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>Lets the dispatcher run for <paramref name="seconds"/> (layout, posted work, timers).</summary>
    public static void Pump(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        while (DateTime.UtcNow < until);
    }
}
