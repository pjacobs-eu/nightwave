using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// A tuner that is lost is tuned again, and the channels on it keep running: a picture that
/// freezes and comes back rather than a player told the channel is gone.
/// </summary>
public sealed class TunerRecoveryTests
{
    private static readonly FeedTimings Timings = new()
    {
        ProgramMap = TimeSpan.FromSeconds(5),
        Keys = TimeSpan.FromSeconds(5),
        Stall = TimeSpan.FromMilliseconds(300),
        Recovery = TimeSpan.FromSeconds(5),
        RecoveryBackoff = [TimeSpan.Zero, TimeSpan.FromMilliseconds(50)],
    };

    [Fact]
    public async Task A_stream_that_ends_is_tuned_again_and_the_channel_goes_on()
    {
        var transports = new ScriptedTransports(first => new EndingTransport(first, reads: 20));
        await using var pool = Pool(transports);

        await using var lease = await pool.OpenAsync(Channel(), Candidates(), CancellationToken.None);
        await using var reader = await new DvbTunedStream(lease).OpenReadAsync();

        await ServiceFeedTests.WaitUntilAsync(() => transports.Created.Count >= 2);

        // The second tuner existing is not the picture being back: it still has to be tuned and
        // deliver, which on a slow machine takes longer than one pause between reads.
        var after = new List<byte[]>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!after.Exists(packet => TransportPacket.Pid(packet) == SampleMux.ClearVideoPid) && DateTime.UtcNow < deadline)
        {
            after.AddRange(TsBuilder.Split(await ServiceFeedTests.ReadAvailableAsync(reader)));
        }

        Assert.False(lease.Feed.IsFaulted);
        Assert.Contains(after, packet => TransportPacket.Pid(packet) == SampleMux.ClearVideoPid);
        Assert.True(transports.Created[0].Disposed);
    }

    /// <summary>Over UDP a server that dropped the session says nothing; silence is how it is noticed.</summary>
    [Fact]
    public async Task A_tuner_that_goes_silent_counts_as_lost()
    {
        var transports = new ScriptedTransports(first => new SilentTransport(first, reads: 20));
        await using var pool = Pool(transports);

        await using var lease = await pool.OpenAsync(Channel(), Candidates(), CancellationToken.None);

        await ServiceFeedTests.WaitUntilAsync(() => transports.Created.Count >= 2);

        Assert.False(lease.Feed.IsFaulted);
    }

    /// <summary>While the tuner is away the readers still hear a PAT, so a worker does not give up on them.</summary>
    [Fact]
    public async Task Readers_hear_a_pat_while_the_tuner_is_away()
    {
        var timings = Timings with { Recovery = TimeSpan.FromSeconds(30), RecoveryBackoff = [TimeSpan.FromSeconds(10)] };
        var transports = new ScriptedTransports(first => new EndingTransport(first, reads: 500));
        await using var pool = new TunerPool(transports, new FakeDescramblerFactory(), new DescramblerSlots(), timings, NullLoggerFactory.Instance);

        await using var lease = await pool.OpenAsync(Channel(), Candidates(), CancellationToken.None);
        await using var reader = await new DvbTunedStream(lease).OpenReadAsync();

        // Drained until the stream ended; what comes after is the tuner being away.
        await ServiceFeedTests.WaitUntilAsync(() => transports.Created[0].Disposed);
        await ServiceFeedTests.ReadAvailableAsync(reader);
        var during = TsBuilder.Split(await ReadForAsync(reader, TimeSpan.FromSeconds(2.5)));

        Assert.Single(transports.Created);
        Assert.NotEmpty(during);
        Assert.All(during, packet => Assert.Equal(TransportPacket.PatPid, TransportPacket.Pid(packet)));
    }

    [Fact]
    public async Task A_tuner_that_cannot_be_had_back_ends_its_channels_with_the_reason()
    {
        var timings = Timings with { Recovery = TimeSpan.FromSeconds(1) };
        var transports = new ScriptedTransports(first => new EndingTransport(first, reads: 500), refuseAfterFirst: true);
        await using var pool = new TunerPool(transports, new FakeDescramblerFactory(), new DescramblerSlots(), timings, NullLoggerFactory.Instance);

        await using var lease = await pool.OpenAsync(Channel(), Candidates(), CancellationToken.None);

        await ServiceFeedTests.WaitUntilAsync(() => lease.Feed.IsFaulted);

        // Tried more than once before giving up, and within the window it was given.
        Assert.True(transports.Created.Count > 2);
    }

    /// <summary>Everything a reader gets within a fixed time, however slowly it trickles in.</summary>
    private static async Task<byte[]> ReadForAsync(Stream reader, TimeSpan time)
    {
        var output = new List<byte>();
        var buffer = new byte[64 * 1024];
        using var window = new CancellationTokenSource(time);

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, window.Token);
                if (read == 0)
                {
                    break;
                }

                output.AddRange(buffer.Take(read));
            }
        }
        catch (OperationCanceledException)
        {
            // The time is up.
        }

        return [.. output];
    }

    private static TunerPool Pool(ITsTransportFactory transports) =>
        new(transports, new FakeDescramblerFactory(), new DescramblerSlots(), Timings, NullLoggerFactory.Instance);

    private static Nightwave.Tuners.Dvb.Channels.StoredChannel Channel() =>
        SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

    private static IReadOnlyList<TuneCandidate> Candidates() =>
        [new TuneCandidate(SampleMux.Device(), new TuneRequest(SampleMux.Tuning, 1))];

    /// <summary>The first transport as the test scripts it, every later one a healthy tuner (or a refusal).</summary>
    private sealed class ScriptedTransports(Func<FakeTransport, ITsTransport> first, bool refuseAfterFirst = false) : ITsTransportFactory
    {
        public List<FakeTransport> Created { get; } = [];

        public ITsTransport Create(TunerDevice device, TuneRequest request)
        {
            lock (Created)
            {
                var isFirst = Created.Count == 0;
                var transport = !isFirst && refuseAfterFirst
                    ? new FakeTransport(request, [], new TunerTransportException($"{device.Label} answered SETUP with RTSP 503."))
                    : new FakeTransport(request, SampleMux.Packets());
                Created.Add(transport);

                return isFirst ? first(transport) : transport;
            }
        }
    }

    /// <summary>A stream that ends after so many reads, as a server closing the connection does.</summary>
    private sealed class EndingTransport(FakeTransport inner, int reads) : ITsTransport
    {
        private int _left = reads;

        public Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken) => inner.StartAsync(pids, cancellationToken);

        public Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken) => inner.SetPidsAsync(pids, cancellationToken);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            Interlocked.Decrement(ref _left) < 0 ? ValueTask.FromResult(0) : inner.ReadAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>A stream that stops arriving after so many reads without ending, as RTP over UDP does.</summary>
    private sealed class SilentTransport(FakeTransport inner, int reads) : ITsTransport
    {
        private int _left = reads;

        public Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken) => inner.StartAsync(pids, cancellationToken);

        public Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken) => inner.SetPidsAsync(pids, cancellationToken);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _left) < 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
