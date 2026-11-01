using System.Globalization;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>An RTSP response (RFC 2326, 7): status, headers, body.</summary>
internal sealed record RtspResponse
{
    /// <summary>What SAT>IP servers assume when a session header names no timeout (SAT>IP 1.2.2, 3.5.4).</summary>
    public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromSeconds(60);

    public required int StatusCode { get; init; }

    public required string Reason { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public string Body { get; init; } = string.Empty;

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public int? CSeq => Headers.TryGetValue("CSeq", out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    public int? ContentLength => Headers.TryGetValue("Content-Length", out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>The session id and how long the server keeps it without hearing from us: <c>Session: 12345678;timeout=30</c>.</summary>
    public (string Id, TimeSpan Timeout)? Session
    {
        get
        {
            if (!Headers.TryGetValue("Session", out var value) || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var timeout = DefaultSessionTimeout;

            foreach (var part in parts.Skip(1))
            {
                if (part.StartsWith("timeout=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(part["timeout=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0)
                {
                    timeout = TimeSpan.FromSeconds(seconds);
                }
            }

            return (parts[0], timeout);
        }
    }

    /// <summary>The stream id SAT>IP gives the session: <c>com.ses.streamID: 1</c>.</summary>
    public int? StreamId => Headers.TryGetValue("com.ses.streamID", out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>Reads the status line and headers; the body is read separately, by its length.</summary>
    public static RtspResponse? Parse(string head)
    {
        ArgumentNullException.ThrowIfNull(head);

        var lines = head.Split(["\r\n", "\n"], StringSplitOptions.None);
        var status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (status.Length < 2
            || !status[0].StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(status[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
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

        return new RtspResponse
        {
            StatusCode = code,
            Reason = status.Length > 2 ? status[2] : string.Empty,
            Headers = headers,
        };
    }
}
