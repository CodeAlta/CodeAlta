using XenoAtom.Logging;

namespace CodeAlta.Desktop.Speech;

/// <summary>The log of speech to prompt. Nothing is written where the application has no log, as in a test.</summary>
internal static class SpeechLog
{
    private const string Name = "CodeAlta.Desktop.Speech";

    /// <summary>Writes something worth knowing.</summary>
    public static void Info(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger(Name).Info(message);
    }

    /// <summary>Writes something that went wrong.</summary>
    public static void Warn(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger(Name).Warn(message);
    }
}
