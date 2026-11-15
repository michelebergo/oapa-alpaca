using System.IO.Ports;

namespace OapaAlpaca.Core;

/// <summary>The OAPA protocol over a serial port: 115200 baud, 8N1, lines ending in <c>\n</c>.</summary>
public sealed class SerialOapaLink : IOapaLink {
    private const int TimeoutMs = 1000;
    private readonly string portName;
    private SerialPort? port;

    public SerialOapaLink(string portName) {
        this.portName = portName;
    }

    public string Description => portName;

    public bool IsOpen => port?.IsOpen == true;

    public void Open() {
        Close();
        try {
            var p = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One) {
                NewLine = "\n",
                ReadTimeout = TimeoutMs,
                WriteTimeout = TimeoutMs,
                DtrEnable = true
            };
            p.Open();
            port = p;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) {
            throw new LinkException($"{portName}: {e.Message}", e);
        }
    }

    public void Close() {
        try {
            port?.Dispose();
        } catch (IOException) {
            // A port that vanished while open throws on dispose; there is nothing left to close.
        }
        port = null;
    }

    public void WriteLine(string text) {
        var p = port ?? throw new LinkException($"{portName} is not open");
        try {
            p.WriteLine(text);
        } catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException) {
            throw new LinkException($"{portName}: {e.Message}", e);
        }
    }

    public string ReadLine() {
        var p = port ?? throw new LinkException($"{portName} is not open");
        try {
            return p.ReadLine().TrimEnd('\r');
        } catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException) {
            throw new LinkException($"{portName}: {e.Message}", e);
        }
    }

    public void Dispose() => Close();
}
