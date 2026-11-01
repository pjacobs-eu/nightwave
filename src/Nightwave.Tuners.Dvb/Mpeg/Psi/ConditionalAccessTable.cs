namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>
/// The CAT (ISO/IEC 13818-1, 2.4.4.6): the conditional access systems on a transport stream
/// and the pids of their EMMs.
/// </summary>
internal sealed record ConditionalAccessTable
{
    public const int TableId = 0x01;

    public required int Version { get; init; }

    public required IReadOnlyList<ConditionalAccessDescriptor> Systems { get; init; }

    public static ConditionalAccessTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header) || header.TableId != TableId || !header.IsCurrent)
        {
            return null;
        }

        return new ConditionalAccessTable
        {
            Version = header.Version,
            Systems =
            [
                .. Descriptor.ReadLoop(SectionHeader.Body(section))
                    .Select(ConditionalAccessDescriptor.From)
                    .OfType<ConditionalAccessDescriptor>(),
            ],
        };
    }
}
