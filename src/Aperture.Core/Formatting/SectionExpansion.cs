namespace Aperture.Core.Formatting;

/// <summary>
/// Which date sections the user expanded or collapsed, remembered per granularity. A section's key means something only
/// within its own granularity (a day and the week that starts on it share one), and grouping by months for a while, then
/// by years again, finds the years as they were left. Immutable, so a view still being built keeps what it was given.
/// </summary>
public sealed class SectionExpansion
{
    private static readonly IReadOnlyDictionary<long, bool> Nothing = new Dictionary<long, bool>();

    private readonly Dictionary<BucketMode, IReadOnlyDictionary<long, bool>> _byMode;

    private SectionExpansion(Dictionary<BucketMode, IReadOnlyDictionary<long, bool>> byMode) => _byMode = byMode;

    /// <summary>Nothing remembered.</summary>
    public static SectionExpansion None { get; } = new([]);

    /// <summary>A copy that remembers <paramref name="sections"/> (key → expanded) for <paramref name="mode"/>, in place of what it had for it.</summary>
    public SectionExpansion With(BucketMode mode, IReadOnlyDictionary<long, bool> sections) =>
        new(new Dictionary<BucketMode, IReadOnlyDictionary<long, bool>>(_byMode) { [mode] = new Dictionary<long, bool>(sections) });

    /// <summary>The sections remembered for <paramref name="mode"/>: key → expanded.</summary>
    public IReadOnlyDictionary<long, bool> For(BucketMode mode) => _byMode.GetValueOrDefault(mode, Nothing);
}
