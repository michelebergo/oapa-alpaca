using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OapaAlpaca;

/// <summary>Answers Alpaca discovery on UDP 32227 with the HTTP port, so the ASCOM chooser and N.I.N.A. find the server.</summary>
public sealed class AlpacaDiscovery : BackgroundService {
    public const int DiscoveryPort = 32227;
    private readonly int port;
    private readonly ILogger<AlpacaDiscovery> log;

    public AlpacaDiscovery(IConfiguration config, ILogger<AlpacaDiscovery> log) {
        this.log = log;
        var urls = config["Urls"] ?? "http://0.0.0.0:11113";
        port = new Uri(urls.Split(';')[0].Replace("0.0.0.0", "localhost")).Port;
    }

    protected override async Task ExecuteAsync(CancellationToken token) {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        } catch (SocketException e) {
            log.LogWarning("Alpaca discovery not available: {Message}", e.Message);
            return;
        }
        var reply = Encoding.ASCII.GetBytes($"{{\"AlpacaPort\":{port}}}");
        while (!token.IsCancellationRequested) {
            try {
                var received = await udp.ReceiveAsync(token);
                if (Encoding.ASCII.GetString(received.Buffer).StartsWith("alpacadiscovery1", StringComparison.OrdinalIgnoreCase)) {
                    await udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
                }
            } catch (OperationCanceledException) {
                break;
            } catch (SocketException e) {
                log.LogWarning("Alpaca discovery: {Message}", e.Message);
            }
        }
    }
}
