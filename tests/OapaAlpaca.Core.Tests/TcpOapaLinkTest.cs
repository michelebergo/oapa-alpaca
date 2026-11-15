using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

public class TcpOapaLinkTest {
    /// <summary>A board on a local TCP port that answers every line with a fixed reply, or stays silent, or hangs up.</summary>
    private sealed class FakeBoard : IDisposable {
        private readonly TcpListener listener;
        private readonly string? reply;
        private readonly bool hangUp;

        public FakeBoard(string? reply, bool hangUp = false) {
            this.reply = reply;
            this.hangUp = hangUp;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        private async Task Serve() {
            using var client = await listener.AcceptTcpClientAsync();
            if (hangUp) {
                return;
            }
            using var stream = client.GetStream();
            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer) > 0) {
                if (reply is not null) {
                    var bytes = Encoding.ASCII.GetBytes(reply);
                    await stream.WriteAsync(bytes);
                }
            }
        }

        public void Dispose() => listener.Stop();
    }

    [Test]
    public void Reads_a_line_without_its_line_ending() {
        using var board = new FakeBoard("<P|protocol:1|>\r\n");
        using var link = new TcpOapaLink($"127.0.0.1:{board.Port}");
        link.Open();
        link.WriteLine("$P?");
        link.ReadLine().Should().Be("<P|protocol:1|>");
    }

    [Test]
    public void A_silent_board_throws_a_link_exception_within_a_few_seconds() {
        // The link waits 3 s for a reply (a weak WiFi signal stretched single replies to 1 s on the
        // bench), so a dead board is reported within that time and a slow one is not dropped.
        using var board = new FakeBoard(reply: null);
        using var link = new TcpOapaLink($"127.0.0.1:{board.Port}");
        link.Open();
        link.WriteLine("$P?");
        var started = DateTime.UtcNow;
        var act = () => link.ReadLine();
        act.Should().Throw<LinkException>();
        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(2)).And.BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void A_closed_socket_throws_a_link_exception() {
        using var board = new FakeBoard(reply: null, hangUp: true);
        using var link = new TcpOapaLink($"127.0.0.1:{board.Port}");
        link.Open();
        Thread.Sleep(100);
        var act = () => { link.WriteLine("$P?"); link.ReadLine(); };
        act.Should().Throw<LinkException>();
    }

    [Test]
    public void The_address_may_omit_the_port() {
        TcpOapaLink.ParseAddress("192.168.1.40").Should().Be(("192.168.1.40", 2323));
        TcpOapaLink.ParseAddress("oapa.local:2400").Should().Be(("oapa.local", 2400));
    }

    [Test]
    public void The_description_names_the_endpoint() {
        using var link = new TcpOapaLink("192.168.1.40");
        link.Description.Should().Be("tcp://192.168.1.40:2323");
        link.IsOpen.Should().BeFalse();
    }
}
