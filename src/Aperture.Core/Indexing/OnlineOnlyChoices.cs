using System.Globalization;
using Aperture.Core.Settings;

namespace Aperture.Core.Indexing;

/// <summary>
/// The answers to the online-only question, per folder of a library (0.9.2 C5–C6): one given for the session (while the
/// library stays open; this object lives as long as its view model), and one kept with the library's settings
/// (Remember, or the folder's menu), which holds after a restart. A kept answer wins. Not thread-safe: the window uses
/// it on its UI thread only (review gate 3).
/// </summary>
public sealed class OnlineOnlyChoices(SettingsService settings)
{
    private readonly Dictionary<long, OnlineOnlyChoice> _session = [];

    /// <summary>The answer in force for the folder: kept, else the session's; <c>null</c>: ask.</summary>
    public OnlineOnlyChoice? Known(long rootId) =>
        Kept(rootId) ?? (_session.TryGetValue(rootId, out var choice) ? choice : null);

    /// <summary>The answer kept with the library; <c>null</c> when none is, or what's kept isn't one.</summary>
    public OnlineOnlyChoice? Kept(long rootId) =>
        settings.Current.OnlineOnlyChoices is { } kept
        && kept.TryGetValue(Key(rootId), out var text)
        && !int.TryParse(text, out _) // a number parses as an enum; only the names are answers
        && Enum.TryParse<OnlineOnlyChoice>(text, ignoreCase: true, out var choice)
        && Enum.IsDefined(choice)
            ? choice
            : null;

    /// <summary>Records an answer for the session, and with the library too when <paramref name="keep"/>.</summary>
    public void Answer(long rootId, OnlineOnlyChoice choice, bool keep)
    {
        _session[rootId] = choice;
        if (keep)
            settings.Update(s => (s.OnlineOnlyChoices ??= new Dictionary<string, string>(StringComparer.Ordinal))[Key(rootId)] = choice.ToString());
    }

    /// <summary>Forgets the folder's answers, kept and session (Ask each time; a folder removed).</summary>
    public void Forget(long rootId)
    {
        _session.Remove(rootId);
        if (settings.Current.OnlineOnlyChoices?.ContainsKey(Key(rootId)) == true)
            settings.Update(s => s.OnlineOnlyChoices!.Remove(Key(rootId)));
    }

    private static string Key(long rootId) => rootId.ToString(CultureInfo.InvariantCulture);
}
