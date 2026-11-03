using Avalonia.Threading;

namespace VmQontrol.App.ViewModels;

/// <summary>The one way work gets back to the window: everything slow runs on a worker, then posts its result here.</summary>
internal static class Ui
{
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Runs on the window's thread and hands back the result (for a change that must happen there, then work goes on).</summary>
    public static Task<T> InvokeAsync<T>(Func<T> func) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(func()) : Dispatcher.UIThread.InvokeAsync(func).GetTask();
}
