namespace CodeAlta.Server.Hosting;

/// <summary>Another server holds the lock of the data root: this one does not start.</summary>
internal sealed class DataRootInUseException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="DataRootInUseException"/> class.</summary>
    /// <param name="message">What happened, for a person.</param>
    /// <param name="innerException">The refusal of the lock.</param>
    public DataRootInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
