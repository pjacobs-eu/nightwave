using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nightwave.Tuners.Dvb.Devices;

/// <summary>
/// The SSDP search a SAT>IP client finds servers with (SAT>IP 1.2.2, 3.3; UPnP Device
/// Architecture 1.1, 1.3): an M-SEARCH for the SAT>IP device type, sent to the multicast group,
/// or straight to one address for a device that was written down rather than found.
/// </summary>
internal static class Ssdp
{
    public const string SatIpSearchTarget = "urn:ses-com:device:SatIPServer:1";

    private static readonly IPEndPoint MulticastGroup = new(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>The M-SEARCH request. MX asks devices to answer within that many seconds.</summary>
    public static byte[] SearchRequest(string searchTarget, int maximumWaitSeconds) =>
        Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n"
            + "HOST: 239.255.255.250:1900\r\n"
            + "MAN: \"ssdp:discover\"\r\n"
            + $"MX: {maximumWaitSeconds}\r\n"
            + $"ST: {searchTarget}\r\n"
            + "USER-AGENT: Nightwave UPnP/1.1 SAT>IP/1.2\r\n"
            + "\r\n");

    /// <summary>
    /// The headers of a search response, names in any case. Null when it is not an HTTP 200
    /// response at all.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseResponse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var lines = response.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0
            || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase)
            || !lines[0].Contains(" 200", StringComparison.Ordinal))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return headers;
    }

    /// <summary>
    /// Searches and collects the device description addresses that answer, each once. Sent to
    /// the multicast group, or to <paramref name="unicastTarget"/> - most devices answer a
    /// search sent to them directly, which is how a device behind a Docker bridge is found.
    /// </summary>
    public static async Task<IReadOnlyList<SsdpAnswer>> SearchAsync(
        string searchTarget,
        TimeSpan wait,
        IPEndPoint? unicastTarget,
        CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = SearchRequest(searchTarget, Math.Max(1, (int)wait.TotalSeconds));
        var target = unicastTarget ?? MulticastGroup;

        // UDP gets lost; a search is sent twice, as the UPnP architecture suggests.
        await socket.SendAsync(request, target, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(request, target, cancellationToken).ConfigureAwait(false);

        var answers = new Dictionary<string, SsdpAnswer>(StringComparer.OrdinalIgnoreCase);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);

        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var headers = ParseResponse(Encoding.UTF8.GetString(received.Buffer));

                if (headers is null
                    || !headers.TryGetValue("LOCATION", out var location)
                    || !Uri.TryCreate(location, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (headers.TryGetValue("ST", out var st)
                    && !string.Equals(st, searchTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                answers.TryAdd(uri.ToString(), new SsdpAnswer(received.RemoteEndPoint.Address, uri));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The wait is over; what answered by now is what there is.
        }
        catch (SocketException) when (answers.Count > 0)
        {
            // An ICMP unreachable from a unicast target after it answered; nothing to add.
        }

        return [.. answers.Values];
    }
}

/// <summary>A device that answered a search, and where its description is.</summary>
internal sealed record SsdpAnswer(IPAddress Address, Uri Location);
