using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

/// <summary>
/// A board for tests: <see cref="Expect"/> queues an exchange (a command and its reply lines), in order;
/// a command the script does not expect next throws, so a test pins the exact sequence on the wire.
/// <see cref="Sent"/> lists every command written; <see cref="FailNext"/> makes the next read throw a
/// <see cref="LinkException"/>, like a dead link.
/// </summary>
public sealed class ScriptedLink : IOapaLink {
    private readonly Queue<(string Command, string[] Reply)> script = new();
    private readonly Queue<string> replyLines = new();
    private bool failNext;

    public List<string> Sent { get; } = new();
    public bool IsOpen { get; private set; }
    public string Description => "scripted";

    public ScriptedLink Expect(string command, params string[] replyLines) {
        script.Enqueue((command, replyLines));
        return this;
    }

    public void FailNext() => failNext = true;

    /// <summary>Makes <see cref="Open"/> throw, like a port that is missing or a host that does not answer.</summary>
    public bool FailOpen { get; set; }

    public void Open() {
        if (FailOpen) {
            throw new LinkException("scripted link cannot open");
        }
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void WriteLine(string text) {
        if (!IsOpen) {
            throw new LinkException("scripted link is not open");
        }
        Sent.Add(text);
        if (failNext) {
            return;
        }
        if (script.Count == 0) {
            throw new InvalidOperationException($"Unexpected command {text}: the script has nothing left");
        }
        var (command, reply) = script.Dequeue();
        if (command != text) {
            throw new InvalidOperationException($"Unexpected command {text}: the script expected {command}");
        }
        foreach (var line in reply) {
            replyLines.Enqueue(line);
        }
    }

    public string ReadLine() {
        if (failNext) {
            failNext = false;
            throw new LinkException("scripted link failure");
        }
        if (replyLines.Count == 0) {
            throw new InvalidOperationException("Read without a scripted reply: the device read more lines than the script gave");
        }
        return replyLines.Dequeue();
    }

    public void Dispose() => Close();
}
