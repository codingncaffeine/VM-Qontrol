using System.Net;
using System.Net.Sockets;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// quickemu's get_port: the first free TCP port in a small range on this host. A port handed out stays reserved for a
/// while, because the machine it went to binds it only when QEMU starts: two machines started together get two ports.
/// </summary>
public static class PortAllocator
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<int, DateTime> HandedOut = [];

    public static int? FirstFree(int start, int count = 10, int? after = null)
    {
        lock (HandedOut)
        {
            var now = DateTime.UtcNow;
            for (var port = Math.Max(start, (after ?? start - 1) + 1); port < start + count; port++)
            {
                if (HandedOut.TryGetValue(port, out var when) && now - when < Hold) continue;
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    HandedOut[port] = now;
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            return null;
        }
    }

    /// <summary>Ends the hold on a port: QEMU has bound it (it is no longer free to anyone) or the start failed.</summary>
    public static void Release(int port)
    {
        lock (HandedOut) HandedOut.Remove(port);
    }
}
