using CodeAlta.Desktop;

namespace CodeAlta;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The speech worker is this executable too: it has no window and answers on its standard streams.
        if (args is [Desktop.Speech.SpeechWorker.Option]) return Desktop.Speech.SpeechWorker.RunProcess();
        // So is the embedding worker of search by meaning.
        if (args is [Desktop.Search.Embedding.EmbeddingWorker.Option]) return Desktop.Search.Embedding.EmbeddingWorker.RunProcess();
        // Started by the system as CodeAlta.app, this process starts again through the user's login shell for
        // the PATH of a terminal; it returns here only when it was started in another way, or has been already.
        if (OperatingSystem.IsMacOS()) DesktopMacBundle.EnterLoginShell(args);
        // Started by an alta typed in a terminal, this process is the application: it leaves that terminal
        // before anything is written or started.
        var token = DesktopTerminalStart.Adopt();
        return DesktopCommandLine.Run(args, Console.Out, Console.Error,
            options => DesktopApplication.Run(token is null ? options : options with { StartToken = token }));
    }
}
