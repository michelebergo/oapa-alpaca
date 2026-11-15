using OapaAlpaca.Core;

namespace OapaAlpaca.Core.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock : IClock {
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
