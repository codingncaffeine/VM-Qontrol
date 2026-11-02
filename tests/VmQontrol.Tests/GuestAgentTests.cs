using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>The agent client against a scripted stand-in that speaks the guest agent's protocol, sync marker included.</summary>
public sealed class GuestAgentTests
{
    private static async Task ServeAsync(Socket listener, Func<string, JsonNode?> answer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptAsync(ct);
            using var stream = new NetworkStream(client);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var request = JsonNode.Parse(line)!;
                var command = request["execute"]!.GetValue<string>();
                if (command == "guest-sync-delimited")
                {
                    // A stale half-answer, then the marker, then the sync reply: the client must skip to the marker.
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"return\": 1}\n"), ct);
                    await stream.WriteAsync(new byte[] { 0xFF }, ct);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["return"] = request["arguments"]!["id"]!.GetValue<long>() }.ToJsonString() + "\n"), ct);
                    continue;
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["return"] = answer(command) }.ToJsonString() + "\n"), ct);
            }
        }
    }

    [Fact]
    public async Task Addresses_skip_loopback_and_link_local()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("agent");
        var path = Path.Combine(Path.GetTempPath(), $"vmq-agent-{Guid.NewGuid():N}.sock"[..30]);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server = ServeAsync(listener, cmd => cmd switch
        {
            "guest-ping" => new JsonObject(),
            "guest-network-get-interfaces" => JsonNode.Parse("""
                [{"name":"lo","ip-addresses":[{"ip-address":"127.0.0.1","ip-address-type":"ipv4"}]},
                 {"name":"eth0","ip-addresses":[{"ip-address":"10.0.2.15","ip-address-type":"ipv4"},{"ip-address":"fe80::1","ip-address-type":"ipv6"},{"ip-address":"fec0::5054:ff:fe12:3456","ip-address-type":"ipv6"}]}]
                """),
            _ => null,
        }, stop.Token);
        try
        {
            var agent = new GuestAgentClient(path);
            Assert.True(await agent.PingAsync(TimeSpan.FromSeconds(2), ct));
            var addresses = await agent.AddressesAsync(TimeSpan.FromSeconds(2), ct);
            Assert.NotNull(addresses);
            Assert.Equal(["10.0.2.15", "fec0::5054:ff:fe12:3456"], addresses!.Select(a => a.Address));
        }
        finally
        {
            await stop.CancelAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task No_agent_is_a_plain_no_never_an_exception()
    {
        var agent = new GuestAgentClient("/nonexistent/agent.sock");
        Assert.False(await agent.PingAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.Null(await agent.AddressesAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
    }
}
