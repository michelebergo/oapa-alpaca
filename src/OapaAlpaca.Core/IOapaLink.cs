namespace OapaAlpaca.Core;

/// <summary>
/// A line link to an OAPA board: one ASCII line out, one line in. <see cref="ReadLine"/> returns a line
/// without its line ending; a silent board or a dead link throws <see cref="LinkException"/>.
/// </summary>
public interface IOapaLink : IDisposable {
    void Open();
    void Close();
    bool IsOpen { get; }
    void WriteLine(string text);
    string ReadLine();
    /// <summary>Where the link goes, for logs and the setup page: a port name or <c>tcp://host:port</c>.</summary>
    string Description { get; }
}

/// <summary>The link did not deliver: timeout, closed socket, missing port.</summary>
public sealed class LinkException(string message, Exception? inner = null) : Exception(message, inner);
