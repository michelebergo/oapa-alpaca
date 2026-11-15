using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace OapaAlpaca.Core;

/// <summary>The OAPA protocol over TCP, port 2323 by default (a WiFi board).</summary>
public sealed class TcpOapaLink : IOapaLink {
    public const int DefaultPort = 2323;
    private const int TimeoutMs = 1000;

    private readonly string host;
    private readonly int port;
    private TcpClient? client;
    private NetworkStream? stream;
    private readonly StringBuilder pending = new();

    public TcpOapaLink(string address) {
        (host, port) = ParseAddress(address);
    }

    public static (string Host, int Port) ParseAddress(string address) {
        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) {
            return (text[..colon], p);
        }
        return (text, DefaultPort);
    }

    public string Description => $"tcp://{host}:{port}";

    public bool IsOpen => client?.Connected == true && stream is not null;

    public void Open() {
        Close();
        try {
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
            if (!tcp.ConnectAsync(host, port).Wait(TimeoutMs)) {
                tcp.Dispose();
                throw new LinkException($"{Description}: no answer within {TimeoutMs} ms");
            }
            stream = tcp.GetStream();
            stream.ReadTimeout = TimeoutMs;
            stream.WriteTimeout = TimeoutMs;
            client = tcp;
        } catch (LinkException) {
            throw;
        } catch (Exception e) when (e is SocketException or IOException or AggregateException) {
            throw new LinkException($"{Description}: {Innermost(e).Message}", e);
        }
    }

    public void Close() {
        stream?.Dispose();
        client?.Dispose();
        stream = null;
        client = null;
        pending.Clear();
    }

    public void WriteLine(string text) {
        var s = stream ?? throw new LinkException($"{Description} is not open");
        try {
            s.Write(Encoding.ASCII.GetBytes(text + "\n"));
        } catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) {
            throw new LinkException($"{Description}: {Innermost(e).Message}", e);
        }
    }

    public string ReadLine() {
        var s = stream ?? throw new LinkException($"{Description} is not open");
        var buffer = new byte[1];
        try {
            while (true) {
                var read = s.Read(buffer, 0, 1);
                if (read == 0) {
                    throw new LinkException($"{Description}: the board closed the connection");
                }
                var c = (char)buffer[0];
                if (c == '\n') {
                    var line = pending.ToString().TrimEnd('\r');
                    pending.Clear();
                    return line;
                }
                pending.Append(c);
            }
        } catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) {
            throw new LinkException($"{Description}: {Innermost(e).Message}", e);
        }
    }

    public void Dispose() => Close();

    private static Exception Innermost(Exception e) {
        while (e.InnerException is not null) {
            e = e.InnerException;
        }
        return e;
    }
}
