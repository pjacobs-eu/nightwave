using System.Globalization;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// The errors of descrambling, each with the server and the service it concerns and what to
/// check. The codes are keys in the plugin's own catalogue.
/// </summary>
internal static class DescramblerErrors
{
    /// <summary>The plugin's id: it is the namespace its translation keys are served under.</summary>
    public const string ProviderId = DvbProvider.Id;

    public const string NotConfiguredCode = "descrambler.not_configured";
    public const string UnreachableCode = "descrambler.unreachable";
    public const string HandshakeFailedCode = "descrambler.handshake_failed";
    public const string TooManyServicesCode = "descrambler.too_many_services";
    public const string InvalidProgramMapCode = "descrambler.invalid_program_map";
    public const string NoControlWordCode = "descrambler.no_control_word";
    public const string ConnectionLostCode = "descrambler.connection_lost";
    public const string UnsupportedAlgorithmCode = "descrambler.unsupported_algorithm";
    public const string UnsupportedIcamModeCode = "descrambler.unsupported_icam_mode";

    private const string ListenPortSettings =
        "The descrambling server has to accept dvbapi clients on the port set here";

    public static TunerException NotConfigured(string channel) =>
        Create(
            NotConfiguredCode,
            $"'{channel}' is scrambled, and no descrambling server is set up to descramble it.",
            "Enter the host and dvbapi port of the descrambling server in the SAT>IP backend settings.",
            ("channel", channel));

    public static TunerException Unreachable(string endpoint, string reason) =>
        Create(
            UnreachableCode,
            $"The dvbapi port of the descrambling server at {endpoint} cannot be reached: {reason}",
            $"Check that the descrambling server is running and reachable from this server at {endpoint}. {ListenPortSettings}.",
            ("server", endpoint),
            ("reason", reason));

    public static TunerException HandshakeFailed(string endpoint, string reason) =>
        Create(
            HandshakeFailedCode,
            $"{endpoint} accepted the connection but did not answer in the dvbapi protocol: {reason}",
            $"Make sure {endpoint} is the dvbapi port of the descrambling server, not its web interface or a card sharing port. {ListenPortSettings}.",
            ("server", endpoint),
            ("reason", reason));

    public static TunerException TooManyServices(string channel, int slot) =>
        Create(
            TooManyServicesCode,
            $"'{channel}' would be scrambled service number {slot + 1} at once; at most {CaProgramMap.MaxSlots} can be descrambled.",
            "Stop another scrambled channel or recording first.",
            ("channel", channel),
            ("limit", CaProgramMap.MaxSlots));

    public static TunerException InvalidProgramMap(string channel, string reason) =>
        Create(
            InvalidProgramMapCode,
            $"The program map of '{channel}' cannot be passed to the descrambling server: {reason}",
            "Tune the channel again; if it persists, run a new channel scan, the channel may have moved.",
            ("channel", channel),
            ("reason", reason));

    public static TunerException NoControlWord(string channel, string endpoint, TimeSpan waited, string detail) =>
        Create(
            NoControlWordCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The descrambling server at {endpoint} sent no control word for '{channel}' within {waited.TotalSeconds:0} seconds. {detail}"),
            "Check in the log of the descrambling server why the ECM was not answered: the card may not be entitled "
                + "to this channel, the card reader may be down, or no reader serves this CAID.",
            ("channel", channel),
            ("server", endpoint),
            ("seconds", (int)waited.TotalSeconds));

    public static TunerException ConnectionLost(string channel, string endpoint, string reason) =>
        Create(
            ConnectionLostCode,
            $"The connection to the descrambling server at {endpoint} was lost while '{channel}' was being descrambled ({reason}); reconnecting.",
            $"Check that the descrambling server is still running at {endpoint}; descrambling resumes by itself once it is back.",
            ("channel", channel),
            ("server", endpoint),
            ("reason", reason));

    public static TunerException UnsupportedAlgorithm(string channel, string algorithm) =>
        Create(
            UnsupportedAlgorithmCode,
            $"The descrambling server asks for {algorithm} to descramble '{channel}', which Nightwave does not implement.",
            "Set the descrambling server to hand over the control words unchanged, as DVB-CSA, rather than in an extended key format; "
                + "otherwise this channel cannot be descrambled here.",
            ("channel", channel),
            ("algorithm", algorithm));

    public static TunerException UnsupportedIcamMode(string channel, int caid, int mode) =>
        Create(
            UnsupportedIcamModeCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The ECMs of '{channel}' (CAID {caid:X4}) ask for iCAM key schedule mode {mode}; only mode 4 is known and implemented. Descrambling as plain DVB-CSA, which will likely show nothing."),
            "Report the channel and the mode to the Nightwave project.",
            ("channel", channel),
            ("caid", caid.ToString("X4", CultureInfo.InvariantCulture)),
            ("mode", mode));

    private static TunerException Create(string code, string message, string remedy, params (string Key, object? Value)[] values) =>
        new(ProviderId, code, message, remedy)
        {
            Values = values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
        };
}
