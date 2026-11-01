using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.LinuxDvb;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// Makes the transport a tuner streams over. For a SAT>IP server that is RTSP, the one every
/// server has to offer, which finds out by itself whether the stream comes over the connection or
/// over UDP. A card in this machine has a transport of its own.
/// </summary>
internal sealed class TsTransportFactory : ITsTransportFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public TsTransportFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ITsTransport Create(TunerDevice device, TuneRequest request)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        if (device.Card is { } card)
        {
            return new LinuxDvbTransport(device, card, request, LinuxDvbKernel.Instance, _loggerFactory.CreateLogger<LinuxDvbTransport>());
        }

        return new SatIpRtspTransport(device, request, _loggerFactory.CreateLogger<SatIpRtspTransport>());
    }
}
