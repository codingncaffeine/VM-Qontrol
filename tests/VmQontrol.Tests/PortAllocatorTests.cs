using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class PortAllocatorTests
{
    [Fact]
    public void Machines_started_together_get_different_ports()
    {
        // A range no other test uses: two calls before anything binds must not hand out the same port.
        var first = PortAllocator.FirstFree(41870);
        var second = PortAllocator.FirstFree(41870);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_released_port_is_handed_out_again()
    {
        var first = PortAllocator.FirstFree(41880)!.Value;
        PortAllocator.Release(first);
        Assert.Equal(first, PortAllocator.FirstFree(41880));
    }

    [Fact]
    public void A_retry_looks_only_past_the_port_that_was_taken()
    {
        var next = PortAllocator.FirstFree(41890, after: 41893);
        Assert.NotNull(next);
        Assert.True(next > 41893);
    }

    [Fact]
    public void A_resumed_machine_moves_only_its_SSH_forward()
    {
        string[] saved = ["-name", "m", "-netdev", "user,hostname=m,hostfwd=tcp::22220-:22,hostfwd=tcp::2222-:22,id=nic"];
        Assert.Equal("user,hostname=m,hostfwd=tcp::22224-:22,hostfwd=tcp::2222-:22,id=nic", MachineRunner.WithSshPort(saved, 22224)[3]);
        Assert.Equal("user,hostname=m,hostfwd=tcp::2222-:22,id=nic", MachineRunner.WithSshPort(saved, null)[3]);
        Assert.Equal(["-name", "m"], MachineRunner.WithSshPort(saved, 22224).Take(2));
        Assert.Equal(["-m", "1G", "-vga", "none"], MachineSuspend.HardwareArgs(["-m", "1G", "-daemonize", "-S", "-incoming", "defer", "-vga", "none"]));
    }
}
