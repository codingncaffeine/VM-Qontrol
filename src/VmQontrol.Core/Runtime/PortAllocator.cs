using System.Net;
using System.Net.Sockets;

namespace VmQontrol.Core.Runtime;

/// <summary>quickemu's get_port: the first free TCP port in a small range on this host.</summary>
public static class PortAllocator
{
    public static int? FirstFree(int start, int count = 10)
    {
        for (var port = start; port < start + count; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        return null;
    }
}
