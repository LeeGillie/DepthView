using System;

namespace DepthView.Integrations.LightBurn.Control;

/// <summary>
/// The LightBurn UDP commands known to exist.
///
/// Every one of these is community knowledge rather than published documentation. LightBurn
/// listens on a UDP port and accepts a small set of plain-text commands; there is no official
/// reference, and the list below is what has been reported and used in the wild. Treat it as
/// evidence, not specification - and note the corollary, that a command missing from this list
/// is not proven not to exist.
///
/// That is exactly why <see cref="LightBurnControl.SendRawAsync"/> exists alongside the typed
/// methods. A closed API surface over an undocumented protocol would be a wall around the
/// interesting part.
/// </summary>
public static class LightBurnCommands
{
    /// <summary>
    /// Are you there. The cheapest way to find out if LightBurn is running.
    /// Observed reply, LightBurn Core 2.1.04: "OK".
    /// </summary>
    public const string Ping = "PING";

    /// <summary>
    /// Observed reply, LightBurn Core 2.1.04: "OK" - the same answer PING gives.
    ///
    /// Worth knowing before building anything on it: against that version this is an
    /// acknowledgement rather than a state, so it does not distinguish idle from busy and
    /// cannot be polled to find out whether a job has finished. Whether that is the whole
    /// story, or whether some configuration makes it answer with more, is not known.
    /// </summary>
    public const string Status = "STATUS";

    /// <summary>
    /// Load a project, prefix for a path: "LOADFILE:C:\path\job.lbrn2".
    ///
    /// Observed reply, LightBurn Core 2.1.04: "OK", with the project genuinely opened - the
    /// window title changes to match.
    ///
    /// A path that does not exist gets NO reply rather than an error, which was tested rather
    /// than assumed. So on this version the reply is worth something: "OK" means it loaded, and
    /// silence means it did not. Do not lean on that too hard - silence is also what a dropped
    /// datagram looks like, and the two are indistinguishable from this end.
    /// </summary>
    public const string LoadFile = "LOADFILE:";

    /// <summary>Load a project, discarding unsaved changes without prompting.</summary>
    public const string ForceLoad = "FORCELOAD:";

    /// <summary>Begin the job currently loaded.</summary>
    public const string Start = "START";

    /// <summary>Ask LightBurn to close.</summary>
    public const string Close = "CLOSE";

    /// <summary>Close without prompting to save.</summary>
    public const string ForceClose = "FORCECLOSE";

    /// <summary>Every command above, for enumeration and for a test that exercises the lot.</summary>
    public static readonly string[] All =
    {
        Ping, Status, LoadFile, ForceLoad, Start, Close, ForceClose
    };

    /// <summary>
    /// Tried against LightBurn Core 2.1.04 and answered with nothing: VERSION, GETSTATUS, HELP.
    ///
    /// Recorded so nobody spends an afternoon trying them again. It is weak evidence and not
    /// proof - a command that does something without replying would look exactly the same from
    /// out here, and so would a dropped datagram - but it is better than the nothing that was
    /// written down before.
    /// </summary>
    public static readonly string[] NoReplyObserved = { "VERSION", "GETSTATUS", "HELP" };
}

/// <summary>One datagram received from LightBurn.</summary>
public sealed record LightBurnMessage(string Text, DateTimeOffset ReceivedAt, string From)
{
    public override string ToString() => $"{ReceivedAt:HH:mm:ss.fff} {From}: {Text}";
}
