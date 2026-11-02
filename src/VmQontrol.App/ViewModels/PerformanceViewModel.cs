using CommunityToolkit.Mvvm.ComponentModel;
using VmQontrol.App.Controls;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>The last minute of a running machine, sampled once a second while its Performance tab is open.</summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly Queue<double> _cpu = new(), _memory = new(), _read = new(), _write = new(), _in = new(), _out = new();
    private CancellationTokenSource? _loop;

    public Func<double, string> PercentFormat { get; } = Formats.Percent;

    public Func<double, string> BytesFormat { get; } = v => Formats.Bytes(v * 1024 * 1024);

    public Func<double, string> RateFormat { get; } = Formats.Rate;

    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = [];
    [ObservableProperty] private IReadOnlyList<double> _readSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _writeSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _inSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _outSeries = [];
    [ObservableProperty] private string _cpuText = "–";
    [ObservableProperty] private string _memoryText = "–";
    [ObservableProperty] private string _diskText = "–";
    [ObservableProperty] private string _netText = "–";
    [ObservableProperty] private bool _netAvailable;
    [ObservableProperty] private double _memoryMaximum = 1;

    public void Start(MachineWatcher watcher, int cores, long memoryMiB)
    {
        if (_loop is { IsCancellationRequested: false }) return;
        MemoryMaximum = memoryMiB;
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        var sampler = new StatsSampler(watcher, cores);
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && !watcher.Exited.IsCompleted)
            {
                if (await sampler.SampleAsync(ct).ConfigureAwait(false) is { } s) Ui.Post(() => Add(s));
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private void Add(StatsSample s)
    {
        static IReadOnlyList<double> Push(Queue<double> q, double v)
        {
            q.Enqueue(v);
            while (q.Count > LiveChart.Capacity) q.Dequeue();
            return q.ToArray();
        }

        CpuSeries = Push(_cpu, s.CpuPercent);
        MemorySeries = Push(_memory, s.MemoryBytes / (1024.0 * 1024));
        ReadSeries = Push(_read, s.DiskReadPerSecond);
        WriteSeries = Push(_write, s.DiskWritePerSecond);
        CpuText = Formats.Percent(s.CpuPercent);
        MemoryText = Formats.Bytes(s.MemoryBytes);
        DiskText = $"{Formats.Rate(s.DiskReadPerSecond)} read · {Formats.Rate(s.DiskWritePerSecond)} written";
        NetAvailable = s.NetInPerSecond is not null;
        if (s.NetInPerSecond is { } inRate && s.NetOutPerSecond is { } outRate)
        {
            InSeries = Push(_in, inRate);
            OutSeries = Push(_out, outRate);
            NetText = $"{Formats.Rate(inRate)} in · {Formats.Rate(outRate)} out";
        }
        else
        {
            NetText = "Needs the guest tools";
        }
    }
}
