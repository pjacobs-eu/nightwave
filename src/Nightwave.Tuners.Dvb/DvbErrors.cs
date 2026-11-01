using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// The errors this plugin raises. Each code is a key in the plugin's catalogue, with a
/// <c>.remedy</c> next to it; the English message here is what the log gets, with the values
/// written in, and the values go along separately for the translated one.
/// </summary>
internal static class DvbErrors
{
    public static TunerException ChannelMissing(string channelId) => Create(
        "tuner.channel_missing",
        $"Channel '{channelId}' is not in the channel list of the SAT>IP backend.",
        "Run a channel scan again in the tuner settings.",
        ("channel", channelId));

    public static TunerException NoDevice(string channel, string source) => Create(
        "tuner.no_device",
        $"No device can receive '{channel}' right now: none that is reachable has an input on which {source} was detected.",
        "Check that the device is switched on and reachable and that the dish or cable this channel was found on is connected, then open the channel scan so the inputs are checked again.",
        ("channel", channel));

    public static TunerException NoFreeTuner(string channel, int tuners, string busyWith) => Create(
        "tuner.no_free_tuner",
        $"All {tuners} tuners that can receive '{channel}' are busy ({busyWith}).",
        "Stop a channel or recording on another frequency, or allow more tuners in the tuner settings if the device has them.",
        ("channel", channel),
        ("tuners", tuners));

    public static TunerException ReplacedByOwnStream(string channel, string next) => Create(
        "tuner.replaced_by_own_stream",
        $"'{channel}' ended: the same client opened '{next}' while no other tuner was free, and that channel took over its tuner.",
        "To receive both channels from one address, free another tuner first.",
        ("channel", channel),
        ("next", next));

    public static TunerException TuneFailed(string channel, string device, string reason) => Create(
        "tuner.tune_failed",
        $"{device} could not tune '{channel}': {reason}",
        "Check that the device is reachable and not in use by another receiver, then try again. Rescan if the channel has moved.",
        ("channel", channel),
        ("device", device));

    public static TunerException NoSignal(string channel, string transponder, string device, TimeSpan waited) => Create(
        "tuner.no_signal",
        $"No program map for '{channel}' arrived from {device} on {transponder} within {waited.TotalSeconds:0} seconds: the tuner has no lock, or the service is not broadcast there any more.",
        "Check the cable to the dish or aerial. If the channel has moved, run a channel scan.",
        ("channel", channel),
        ("transponder", transponder));

    public static TunerException ServiceMissing(string channel, int serviceId, string transponder) => Create(
        "tuner.service_missing",
        $"'{channel}' (service {serviceId}) is not on the air on {transponder} at the moment: it is not in the transponder's program association table.",
        "Some channels only broadcast at certain times, during matches for example. If the channel has moved, run a channel scan again.",
        ("channel", channel),
        ("transponder", transponder));

    public static TunerException DescramblingNotConfigured(string channel) => Create(
        "tuner.descrambling_not_configured",
        $"'{channel}' is scrambled and descrambling is not configured.",
        "Set up a descrambling server under Descrambling in the tuner settings, or choose a channel that is free to air.",
        ("channel", channel));

    public static TunerException NoKeys(string channel, TimeSpan waited) => Create(
        "tuner.no_keys",
        $"The descrambling server returned no control word for '{channel}' within {waited.TotalSeconds:0} seconds, so it stays scrambled.",
        "Check at the descrambling server that the card is inserted, active and entitled to this channel.",
        ("channel", channel),
        ("seconds", (int)waited.TotalSeconds));

    public static TunerException DescramblerFailed(string channel, string reason) => Create(
        "tuner.descrambler_failed",
        $"Descrambling '{channel}' could not be started: {reason}",
        "Check that the descrambling server is running and that its dvbapi port is reachable from this server.",
        ("channel", channel));

    public static TunerException StreamLost(string channel, string reason) => Create(
        "tuner.stream_lost",
        $"The stream of '{channel}' stopped: {reason}",
        "Check that the device is still switched on and reachable, then tune the channel again.",
        ("channel", channel));

    public static TunerException TargetMissing(string targetId) => Create(
        "scan.target_missing",
        $"There is no scan target '{targetId}'.",
        "Reload the list of scan targets and pick one of those.",
        ("target", targetId));

    public static TunerException TargetUnusable(string target, string reason) => Create(
        "scan.target_unusable",
        $"{target} cannot be scanned: {reason}",
        "Check that the dish, cable or aerial is connected to the device and that the device is reachable, then reload the scan targets: the inputs are checked again.",
        ("target", target));

    public static TunerException ScanBusy() => Create(
        "scan.busy",
        "A channel scan of the SAT>IP backend is already running.",
        "Wait for it to finish, or cancel it, then start this scan.");

    public static TunerException NoTransponders(string target, string position) => Create(
        "scan.no_transponders",
        $"There is no table of start transponders for {position}, so {target} cannot be scanned.",
        "Nightwave knows start transponders for 19.2E, 13.0E, 23.5E and 28.2E. Other satellites cannot be scanned yet.",
        ("target", target));

    private static TunerException Create(string code, string message, string remedy, params (string Key, object? Value)[] values) =>
        new(DvbProvider.Id, code, message, remedy)
        {
            Values = values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
        };
}
