using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// A SAT>IP stream over RTSP (SAT>IP 1.2.2, 3.5): SETUP with the tuning in the query, PLAY to
/// start, PLAY with <c>addpids</c> and <c>delpids</c> to change what passes, OPTIONS to keep the
/// session alive, TEARDOWN to give the tuner back.
/// </summary>
/// <remarks>
/// Interleaved over the RTSP connection by default. RTP to a UDP port needs the server to reach
/// this machine on a port it picks, which a Docker bridge or a NAT does not allow; one TCP
/// connection this machine opened always works.
/// <para>
/// Where the server allows it. Interleaving is optional in SAT>IP, and a server without it does
/// not refuse the SETUP: a Kathrein EXIP-418 answers 200 with a UDP transport of its own choosing
/// and sends the stream to a port nobody listens on. So the answer's transport is checked, and a
/// server that did not take interleaving is asked again over UDP - and remembered, so the next
/// tune goes there straight away.
/// </para>
/// <para>
/// Beside the stream comes RTCP: over UDP on the port above the RTP port, interleaved on channel
/// 1. SAT>IP puts the tuner's signal report into it, which is how a scan learns within a second
/// that a transponder does not lock, and how a loss in the stream can be told apart from a weak
/// signal.
/// </para>
/// </remarks>
internal sealed partial class SatIpRtspTransport : ITsTransport
{
    /// <summary>A FRITZ!Box wants some pid in a SETUP. This one carries nothing, and the real list follows.</summary>
    private const int FritzBoxFirstPid = 21;

    /// <summary>Devices that answered an interleaved SETUP with something else, by device id.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> WithoutInterleaving =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>About 1300 datagrams of 7 packets: two seconds of a busy transponder.</summary>
    private const int QueueCapacity = 2048;

    /// <summary>What the UDP socket asks for: about 1.5 seconds of a 40 Mbit/s transponder, so a busy moment on this machine loses nothing.</summary>
    private const int WantedReceiveBuffer = 8 * 1024 * 1024;

    /// <summary>How often, at most, a loss in the stream is logged while it goes on.</summary>
    private static readonly TimeSpan LossLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>Set once the receive buffer the system granted has been logged: it is the same for every stream.</summary>
    private static int _receiveBufferLogged;

    private readonly TunerDevice _device;
    private readonly TuneRequest _request;
    private readonly ILogger _logger;
    private readonly ChunkQueue _queue = new(QueueCapacity);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _control = new(1, 1);

    private RtspConnection? _connection;
    private UdpClient? _rtp;
    private UdpClient? _rtcp;
    private Task? _keepAlive;
    private Task? _udpReader;
    private string? _session;
    private TimeSpan _timeout = RtspResponse.DefaultSessionTimeout;
    private int _streamId;
    private Task? _rtcpReader;
    private HashSet<int> _pids = [];
    private readonly RtpSequenceTracker _sequence = new();
    private readonly RtpReorderBuffer _reorder = new();
    private volatile TunerSignal? _signal;
    private long _signalReports;
    private long _lossLoggedAt = Environment.TickCount64;
    private long _lostAtLastLog;

    public SatIpRtspTransport(TunerDevice device, TuneRequest request, ILogger logger)
    {
        _device = device;
        _request = request;
        _logger = logger;
    }

    private string BaseUri => string.Create(CultureInfo.InvariantCulture, $"rtsp://{HostForUri}:{_device.RtspPort}/");

    private string StreamUri => string.Create(CultureInfo.InvariantCulture, $"{BaseUri}stream={_streamId}");

    /// <inheritdoc/>
    public TransportStatus Status => new()
    {
        Signal = _signal,
        SignalReports = Interlocked.Read(ref _signalReports),
        DatagramsReceived = _sequence.Received,
        DatagramsReordered = _reorder.Reordered,
        DatagramsLost = _sequence.Lost,
        Path = _session is null ? StreamPath.Unknown : _rtp is null ? StreamPath.RtspInterleaved : StreamPath.RtpUdp,
    };

    private string HostForUri => _device.Host.Contains(':', StringComparison.Ordinal) ? $"[{_device.Host}]" : _device.Host;

    public async Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        var tuning = _request.Tuning
            ?? throw new TunerTransportException("A SAT>IP stream needs tuning parameters, and none were given.");

        _connection = await RtspConnection.ConnectAsync(_device.Host, _device.RtspPort, cancellationToken).ConfigureAwait(false);
        _connection.OnClosed = error => _queue.Complete(
            new TunerTransportException($"{_device.Label} closed the RTSP connection: {error.Message}", error));

        var query = SatIpQuery.Build(tuning, _request.Source, pids.Count == 0 && _device.IsFritzBox ? [FritzBoxFirstPid] : pids);
        var overUdp = WithoutInterleaving.ContainsKey(_device.Id);

        var setup = await SetupAsync(query, overUdp, cancellationToken).ConfigureAwait(false);

        if (!setup.IsSuccess || setup.Session is null)
        {
            throw new TunerTransportException(Refusal("SETUP", setup, tuning));
        }

        if (!overUdp && !IsInterleaved(setup))
        {
            var answered = setup.Headers.TryGetValue("Transport", out var value) ? value : "no transport";
            WithoutInterleaving[_device.Id] = true;
            LogNoInterleaving(_device.Label, answered);

            // The session it set up streams to a port of its own choosing; given back before the
            // tuner is asked for again the way it can deliver.
            await TearDownQuietlyAsync(setup, cancellationToken).ConfigureAwait(false);

            setup = await SetupAsync(query, overUdp: true, cancellationToken).ConfigureAwait(false);

            if (!setup.IsSuccess || setup.Session is null)
            {
                throw new TunerTransportException(Refusal("SETUP", setup, tuning));
            }
        }

        var session = setup.Session!.Value;

        (_session, _timeout) = session;
        _streamId = setup.StreamId ?? 0;
        _pids = [.. pids];

        var play = await _connection.SendAsync("PLAY", StreamUri, SessionHeader(), cancellationToken).ConfigureAwait(false);
        if (!play.IsSuccess)
        {
            throw new TunerTransportException(Refusal("PLAY", play, tuning));
        }

        _keepAlive = Task.Run(KeepAliveAsync, CancellationToken.None);

        if (_rtp is not null)
        {
            _udpReader = Task.Run(ReadUdpAsync, CancellationToken.None);
            _rtcpReader = Task.Run(ReadRtcpAsync, CancellationToken.None);
        }

        LogStarted(_device.Label, tuning, _session, _timeout.TotalSeconds);
    }

    public async Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("The stream has not been started.");

        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = pids.ToHashSet();
            var added = wanted.Except(_pids).ToList();
            var removed = _pids.Except(wanted).ToList();

            if (added.Count == 0 && removed.Count == 0)
            {
                return;
            }

            var response = await connection.SendAsync(
                "PLAY",
                $"{StreamUri}?{SatIpQuery.PidChange(wanted, added, removed, _device.IsFritzBox)}",
                SessionHeader(),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                // Some servers take only the full list. Asked once more that way before giving up.
                response = await connection.SendAsync(
                    "PLAY",
                    $"{StreamUri}?pids={SatIpQuery.Pids(wanted)}",
                    SessionHeader(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccess)
            {
                throw new TunerTransportException(
                    $"{_device.Label} refused to change the pids of stream {_streamId}: RTSP {response.StatusCode} {response.Reason}.");
            }

            _pids = wanted;
        }
        finally
        {
            _control.Release();
        }
    }

    private async Task<RtspResponse> SetupAsync(string query, bool overUdp, CancellationToken cancellationToken)
    {
        var connection = _connection!;
        string transport;

        if (overUdp)
        {
            connection.OnData = null;
            connection.OnControl = null;
            var port = _rtp is null ? OpenUdpPorts() : ((IPEndPoint)_rtp.Client.LocalEndPoint!).Port;
            transport = string.Create(CultureInfo.InvariantCulture, $"RTP/AVP;unicast;client_port={port}-{port + 1}");
        }
        else
        {
            connection.OnData = OnRtp;
            connection.OnControl = OnRtcp;
            transport = "RTP/AVP/TCP;interleaved=0-1";
        }

        return await connection.SendAsync(
            "SETUP",
            $"{BaseUri}?{query}",
            [new("Transport", transport)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a SETUP answer took RTP over the RTSP connection, as asked.</summary>
    internal static bool IsInterleaved(RtspResponse setup) =>
        setup.Headers.TryGetValue("Transport", out var transport)
        && transport.Contains("interleaved", StringComparison.OrdinalIgnoreCase);

    private async Task TearDownQuietlyAsync(RtspResponse setup, CancellationToken cancellationToken)
    {
        if (setup.Session is not { } session)
        {
            return;
        }

        try
        {
            var stream = string.Create(CultureInfo.InvariantCulture, $"{BaseUri}stream={setup.StreamId ?? 0}");
            await _connection!.SendAsync("TEARDOWN", stream, [new("Session", session.Id)], cancellationToken).ConfigureAwait(false);
        }
        catch (TunerTransportException error)
        {
            // The server frees the tuner once that session times out.
            LogTeardownFailed(_device.Label, error.Message);
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var dropped = _queue.TakeDropped();
        if (dropped > 0)
        {
            LogQueueOverflow(_device.Label, dropped);
        }

        return _queue.ReadAsync(buffer, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        if (_connection is not null && _session is not null && _connection.Failure is null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connection.SendAsync("TEARDOWN", StreamUri, SessionHeader(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TunerTransportException or OperationCanceledException or IOException)
            {
                // The server frees the tuner by itself once the session times out.
                LogTeardownFailed(_device.Label, error.Message);
            }
        }

        _rtp?.Dispose();
        _rtcp?.Dispose();

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var task in new[] { _keepAlive, _udpReader, _rtcpReader })
        {
            if (task is not null)
            {
                await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        // What was lost since the last line, where loss went on or was more than a hiccup while
        // the tuner switched over; a datagram or two at the start of a short scan is not news.
        var unlogged = _sequence.Lost - Interlocked.Read(ref _lostAtLastLog);
        if (unlogged > 0 && (Interlocked.Read(ref _lostAtLastLog) > 0 || unlogged >= 10))
        {
            LogLoss(unlogged, Environment.TickCount64 - Interlocked.Read(ref _lossLoggedAt));
        }

        _queue.Complete();
        _stop.Dispose();
        _control.Dispose();
    }

    private KeyValuePair<string, string>[] SessionHeader() => [new("Session", _session ?? string.Empty)];

    /// <summary>
    /// Keeps the session: an OPTIONS well before the server's timeout, since a server that times
    /// a session out frees its tuner for somebody else (SAT>IP 1.2.2, 3.5.4).
    /// </summary>
    private async Task KeepAliveAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _timeout.TotalSeconds / 2));

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);

                var response = await _connection!.SendAsync("OPTIONS", BaseUri, SessionHeader(), _stop.Token).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    LogKeepAliveRefused(_device.Label, response.StatusCode);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (TunerTransportException error)
        {
            // The connection is gone. Over TCP that ends the stream too; over UDP it is the end
            // of the session all the same, so the reader is told.
            _queue.Complete(error);
        }
    }

    private void OnRtp(ReadOnlySpan<byte> datagram)
    {
        if (!RtpPacket.TryGetPayload(datagram, out var payload, out var sequence))
        {
            return;
        }

        // Over UDP, order is not promised. Sorting a few datagrams back into place here is the one
        // repair that gets everything back; the sequence counts what is still missing afterwards.
        _reorder.Take(sequence, datagram[payload], Deliver);
    }

    private void Deliver(int sequence, ReadOnlySpan<byte> payload)
    {
        if (_sequence.Take(sequence) > 0)
        {
            var now = Environment.TickCount64;
            var since = now - Interlocked.Read(ref _lossLoggedAt);

            if (since >= LossLogInterval.TotalMilliseconds)
            {
                var lost = _sequence.Lost - Interlocked.Read(ref _lostAtLastLog);
                LogLoss(lost, since);
                Interlocked.Exchange(ref _lostAtLastLog, _sequence.Lost);
                Interlocked.Exchange(ref _lossLoggedAt, now);
            }
        }

        _queue.Write(payload);
    }

    /// <summary>Takes the tuner's signal report out of an RTCP datagram. Anything else in it is of no use here.</summary>
    private void OnRtcp(ReadOnlySpan<byte> datagram)
    {
        if (TunerSignal.TryReadRtcp(datagram, out var signal))
        {
            _signal = signal;
            Interlocked.Increment(ref _signalReports);
        }
    }

    private void LogLoss(long lost, long milliseconds) => LogDatagramsLost(
        _device.Label,
        lost,
        Math.Max(1, (int)Math.Round(milliseconds / 1000d)),
        _sequence.Lost,
        // Out of what was sent, not what arrived: over the arrived ones alone a bad minute read
        // "52 of 25", more lost than there were to lose.
        _sequence.Received + _sequence.Lost,
        _request.Describe(),
        _signal?.Describe() ?? "no signal report");

    /// <summary>Binds an even port for RTP and the one above it for RTCP, as RTP/AVP wants them.</summary>
    private int OpenUdpPorts()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = 40000 + (2 * System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000));
            UdpClient? rtp = null;

            try
            {
                rtp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                var granted = GrowReceiveBuffer(rtp.Client);
                var rtcp = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1));

                if (Interlocked.Exchange(ref _receiveBufferLogged, 1) == 0)
                {
                    LogReceiveBuffer(granted, WantedReceiveBuffer);
                }

                _rtp = rtp;
                _rtcp = rtcp;

                return port;
            }
            catch (SocketException)
            {
                rtp?.Dispose();
            }
        }

        throw new TunerTransportException("No free pair of UDP ports was found to receive the SAT>IP stream on.");
    }

    /// <summary>
    /// Asks for <see cref="WantedReceiveBuffer"/> and less, a megabyte at a time, until the
    /// system agrees. macOS refuses anything above <c>kern.ipc.maxsockbuf</c> outright; Linux
    /// quietly caps at <c>net.core.rmem_max</c>. Returns what the socket reports it has.
    /// </summary>
    internal static int GrowReceiveBuffer(Socket socket)
    {
        for (var size = WantedReceiveBuffer; size >= 1024 * 1024; size -= 1024 * 1024)
        {
            try
            {
                socket.ReceiveBufferSize = size;
                break;
            }
            catch (SocketException)
            {
                // More than the system allows; a little less.
            }
        }

        return socket.ReceiveBufferSize;
    }

    private async Task ReadRtcpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _rtcp!.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                OnRtcp(received.Buffer);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped, or the reports stopped; the stream itself is what counts.
        }
    }

    private async Task ReadUdpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _rtp!.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                OnRtp(received.Buffer);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Stopped.
        }
        catch (SocketException error)
        {
            _queue.Complete(new TunerTransportException($"Receiving the stream from {_device.Label} failed: {error.Message}", error));
        }
    }

    private string Refusal(string method, RtspResponse response, TuningParameters tuning)
    {
        var hint = response.StatusCode switch
        {
            503 => "all of its tuners are in use, possibly by another receiver",
            404 => "it does not know the stream",
            408 => "it did not lock on the signal in time",
            400 or 414 => "it does not accept these tuning parameters",
            _ => response.Reason,
        };

        return $"{_device.Label} answered {method} for {tuning.Describe()} with RTSP {response.StatusCode}: {hint}.";
    }

    [LoggerMessage(EventId = 9810, Level = LogLevel.Information, Message = "Tuned {Device} to {Transponder}, session {Session}, timeout {TimeoutSeconds} s.")]
    private partial void LogStarted(string device, TuningParameters transponder, string session, double timeoutSeconds);

    [LoggerMessage(EventId = 9811, Level = LogLevel.Warning, Message = "{Device} did not accept a keep-alive: RTSP {Status}. The stream may stop when the session times out.")]
    private partial void LogKeepAliveRefused(string device, int status);

    [LoggerMessage(EventId = 9812, Level = LogLevel.Debug, Message = "TEARDOWN to {Device} failed: {Reason}")]
    private partial void LogTeardownFailed(string device, string reason);

    [LoggerMessage(EventId = 9814, Level = LogLevel.Information, Message = "{Device} does not stream RTP over the RTSP connection (it answered '{Transport}'); it is tuned over UDP from now on, which needs it to reach this machine directly.")]
    private partial void LogNoInterleaving(string device, string transport);

    [LoggerMessage(EventId = 9815, Level = LogLevel.Warning, Message = "{Device} lost {Lost} RTP datagrams in the last {Seconds} s ({Total} of {Sent} since tuning {Transponder}); the tuner reports {Signal}. With a lock and a good level the loss is on the network; without, it is the dish, the cable or the weather.")]
    private partial void LogDatagramsLost(string device, long lost, int seconds, long total, long sent, string transponder, string signal);

    [LoggerMessage(EventId = 9816, Level = LogLevel.Information, Message = "The UDP receive buffer for SAT>IP streams is {Granted} bytes (asked for {Wanted}). If streams lose datagrams under load, raise net.core.rmem_max (Linux) or kern.ipc.maxsockbuf (macOS).")]
    private partial void LogReceiveBuffer(int granted, int wanted);

    [LoggerMessage(EventId = 9813, Level = LogLevel.Warning, Message = "The stream from {Device} came in faster than it was read; {Count} datagrams were dropped.")]
    private partial void LogQueueOverflow(string device, long count);
}
