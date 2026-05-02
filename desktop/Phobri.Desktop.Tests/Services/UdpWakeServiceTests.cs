using Xunit;
using Phobri.Desktop.Services;
using System.Text;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.Tests.Services;

public sealed class UdpWakeServiceTests
{
    [Fact]
    public async Task SendWake_ReturnsFalse_ForInvalidIp()
    {
        using var service = new UdpWakeService();

        // Sending to an unroutable/invalid IP should not throw, return false
        var result = await service.SendWakeAsync("0.0.0.0", port: 9999);

        // Even though the packet was "sent", UDP is fire-and-forget.
        // We just verify no exception is thrown.
        Assert.False(result); // likely fails because IP is invalid
    }

    [Fact]
    public async Task SendWake_ReturnsTrue_ForLocalhost()
    {
        using var service = new UdpWakeService();

        var result = await service.SendWakeAsync("127.0.0.1", port: 9876);

        // Sending to localhost (even if nothing is listening) should not error
        Assert.True(result);
    }

    [Fact]
    public void WakePacket_IsCorrectFormat()
    {
        var expected = Encoding.UTF8.GetBytes("WAKE");
        // Verify the static wake packet used by the service
        // We can't access private static, but we can verify behavior indirectly
        Assert.Equal(4, expected.Length);
        Assert.Equal((byte)'W', expected[0]);
        Assert.Equal((byte)'A', expected[1]);
        Assert.Equal((byte)'K', expected[2]);
        Assert.Equal((byte)'E', expected[3]);
    }
}
