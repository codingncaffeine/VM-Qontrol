using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// The machine's serial port as text: what the guest prints (boot messages, a login prompt on Linux), and a line to
/// type into it. Terminal color and cursor codes are removed; it is a console, not a full terminal.
/// Output is a list of lines (the view virtualizes them, so only visible lines are laid out), and arriving text is
/// gathered on the reader's thread and handed to the window at most ten times a second: a Linux boot prints hundreds
/// of lines, and re-laying out the whole log for each one stalled the window for minutes.
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    private const int KeepLines = 3000;
    private readonly Lock _gate = new();
    private readonly StringBuilder _arrived = new();
    private bool _flushQueued;
    private Socket? _socket;
    private CancellationTokenSource? _reader;

    /// <summary>The lines shown; the last one may still be growing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Lines { get; } = [];

    /// <summary>All of the output as one text (copying, tests).</summary>
    public string Text => string.Join('\n', Lines);

    /// <summary>Raised after new output reached <see cref="Lines"/> (the view scrolls to it).</summary>
    public event Action? OutputAdded;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _note = "The console connects when the machine is running.";

    public async Task ConnectAsync(RunningMachine running)
    {
        if (Connected) return;
        var path = running.Paths.Serial;
        if (!File.Exists(path))
        {
            Ui.Post(() => Note = "This machine has no serial port. Windows and macOS do not use one; for others it can be turned on in Settings.");
            return;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            Ui.Post(() => Note = $"The serial port did not answer ({ex.Message}).");
            return;
        }

        _socket = socket;
        _reader = new CancellationTokenSource();
        var ct = _reader.Token;
        Ui.Post(() =>
        {
            Connected = true;
            Note = "Connected to the serial port. Press Enter in the box below to wake a login prompt.";
        });
        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[4096];
            var cleaner = new EscapeCleaner();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    var count = decoder.GetChars(buffer, 0, n, chars, 0);
                    var chunk = cleaner.Feed(new string(chars, 0, count));
                    if (chunk.Length > 0) Arrive(chunk);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            Ui.Post(() =>
            {
                Connected = false;
                Note = "The serial port closed.";
            });
        }, ct);
    }

    public void Disconnect()
    {
        _reader?.Cancel();
        _socket?.Dispose();
        _socket = null;
        Connected = false;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_socket is not { } socket) return;
        var line = Input;
        Input = "";
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(line + "\r"), SocketFlags.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Ui.Post(() => Note = "The serial port closed.");
        }
    }

    /// <summary>Reader thread: gather the text; one flush is queued for the window, a tenth of a second later.</summary>
    private void Arrive(string chunk)
    {
        lock (_gate)
        {
            _arrived.Append(chunk);
            if (_flushQueued) return;
            _flushQueued = true;
        }

        _ = Task.Delay(100).ContinueWith(_ => Ui.Post(Flush), TaskScheduler.Default);
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            text = _arrived.ToString();
            _arrived.Clear();
            _flushQueued = false;
        }

        var parts = text.Split('\n');
        if (Lines.Count == 0) Lines.Add("");
        Lines[^1] += parts[0];
        for (var i = 1; i < parts.Length; i++) Lines.Add(parts[i]);
        while (Lines.Count > KeepLines) Lines.RemoveAt(0);
        OnPropertyChanged(nameof(Text));
        OutputAdded?.Invoke();
    }

    [RelayCommand]
    private void CopyAll() => CopyRequested?.Invoke(Text);

    /// <summary>Set by the shell: puts text on the clipboard.</summary>
    public Action<string>? CopyRequested { get; set; }

    /// <summary>
    /// Removes terminal escape sequences and carriage returns from a stream that arrives in pieces: an escape sequence
    /// cut in two by the network is held back until its end arrives, so no fragment of one ever shows.
    /// </summary>
    public sealed partial class EscapeCleaner
    {
        private string _pending = "";

        public string Feed(string chunk)
        {
            var text = Escapes().Replace(_pending + chunk, "");
            _pending = "";
            // Only a tail that could still become a sequence waits for the next piece; anything else is stray and dropped.
            if (Incomplete().Match(text) is { Success: true } tail)
            {
                _pending = tail.Value;
                text = text[..tail.Index];
            }

            return text.Replace("\x1B", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
        }

        /// <summary>
        /// Every complete sequence: CSI (ESC [ … final), OSC (ESC ] … BEL or ESC \), character-set and other two-byte
        /// designations (ESC ( B, ESC ) 0, ESC % @), and single-character commands (ESC c, ESC 7, ESC M…); [ and ] always
        /// open a longer sequence, so they are never one on their own.
        /// </summary>
        [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[ -/][ -/]*[0-~]|[0-Z\\^-~])")]
        private static partial Regex Escapes();

        /// <summary>A sequence cut off at the end of what has arrived so far.</summary>
        [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*|\][^\x07\x1B]*\x1B?|[ -/]*)$")]
        private static partial Regex Incomplete();
    }
}
