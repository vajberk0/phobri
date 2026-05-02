using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Phobri.Desktop.Services;

/// <summary>
/// Sends UDP wake packets to the Android device to initiate a connection.
/// The wake packet is a simple "WAKE" string to port 9876.
/// </summary>
public interface IUdpWakeService
{
    /// <summary>
    /// Send a UDP wake packet to the phone.
    /// </summary>
    /// <param name="phoneIp">IP address of the phone.</param>
    /// <param name="port">UDP port on the phone (default 9876).</param>
    /// <returns>True if packet was sent (does not guarantee delivery).</returns>
    Task<bool> SendWakeAsync(string phoneIp, int port = 9876, CancellationToken ct = default);
}

public sealed class UdpWakeService : IUdpWakeService, IDisposable
{
    private readonly UdpClient _udpClient;
    private static readonly byte[] WakePacket = Encoding.UTF8.GetBytes("WAKE");

    public UdpWakeService()
    {
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
    }

    /// <inheritdoc/>
    public async Task<bool> SendWakeAsync(string phoneIp, int port = 9876, CancellationToken ct = default)
    {
        try
        {
            await _udpClient.SendAsync(WakePacket, WakePacket.Length, phoneIp, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }
}
