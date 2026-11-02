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
}
