namespace VmQontrol.Core.Devices;

/// <summary>
/// Notices USB devices plugged into this computer while it watches: the device folders under sysfs are compared every
/// couple of seconds (a directory listing, no device is opened). Devices present when watching starts are not news.
/// Polling instead of a udev netlink socket keeps it working the same on every distribution, in containers and on musl.
/// </summary>
public sealed class UsbHotplug : IDisposable
{
    private readonly string _sysRoot;
    private readonly Func<string, IReadOnlyList<HostUsbDevice>> _list;
    private readonly CancellationTokenSource _stop = new();
    private HashSet<string> _known = [];

    public UsbHotplug(string sysRoot = "/sys/bus/usb/devices", Func<string, IReadOnlyList<HostUsbDevice>>? list = null)
    {
        _sysRoot = sysRoot;
        _list = list ?? (root => UsbHost.List(root));
    }

    /// <summary>A device was plugged in (raised on a worker).</summary>
    public event Action<HostUsbDevice>? Plugged;

    /// <summary>The device folders now ("1-4", "3-2.1"): ports, so a device re-plugged in another port is news too.</summary>
    private HashSet<string> Ports()
    {
        try
        {
            return Directory.EnumerateDirectories(_sysRoot).Select(Path.GetFileName).OfType<string>()
                .Where(p => !p.Contains(':') && !p.StartsWith("usb", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>One comparison: the devices on ports that were empty at the last look. Public for tests.</summary>
    public IReadOnlyList<HostUsbDevice> Check()
    {
        var now = Ports();
        var added = now.Except(_known).ToHashSet(StringComparer.Ordinal);
        _known = now;
        if (added.Count == 0) return [];
        return _list(_sysRoot).Where(d => added.Contains(d.Port)).ToList();
    }

    /// <summary>Takes the devices plugged in now as known, then watches until disposed.</summary>
    public void Start(TimeSpan? every = null)
    {
        _known = Ports();
        var period = every ?? TimeSpan.FromSeconds(2);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    foreach (var d in Check())
                        Plugged?.Invoke(d);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
