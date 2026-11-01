using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Net;

/// <summary>A channel a client has to fetch itself: a web playlist of segments, handed over as it is.</summary>
internal sealed class RedirectedStream(string channelId, Uri address) : ITunedStream
{
    public Guid Id { get; } = Guid.NewGuid();

    public string ChannelId => channelId;

    public bool IsActive => false;

    public Uri? DirectAddress => address;

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This channel has no byte stream; a client goes to its address directly.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
