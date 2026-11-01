namespace Nightwave.Core.Tuning;

/// <summary>
/// Something could not be done, with what the operator can do about it. Code and values let the
/// UI translate it; the English message is what the log gets.
/// </summary>
public abstract class NightwaveException : Exception
{
    protected NightwaveException(string code, string message, string remedy)
        : base(message)
    {
        Code = code;
        Remedy = remedy;
    }

    protected NightwaveException(string code, string message, string remedy, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        Remedy = remedy;
    }

    /// <summary>Stable, machine-readable code. Untranslated; the UI maps it to translated text.</summary>
    public string Code { get; }

    /// <summary>English, untranslated: what the operator can do about it.</summary>
    public string Remedy { get; }

    /// <summary>What the message talks about, separately, for a translation that orders it differently.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();
}

/// <summary>A tuner could not do what was asked.</summary>
public sealed class TunerException : NightwaveException
{
    public TunerException(string providerId, string code, string message, string remedy)
        : base(code, message, remedy)
    {
        ProviderId = providerId;
    }

    /// <summary>The provider that raised it.</summary>
    public string ProviderId { get; }
}
