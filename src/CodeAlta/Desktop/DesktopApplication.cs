using CodeAlta.Desktop.Rpc;
using NeoAstra;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop;

internal sealed class DesktopApplication(DesktopLaunchOptions options)
{
    internal int ExitCode { get; private set; } = 1;

    internal static int Run(DesktopLaunchOptions options)
    {
        Directory.CreateDirectory(options.DataRoot);
        var desktop = new DesktopApplication(options);
        var result = NeoApplication.Run(new NeoApplicationOptions
        {
            ApplicationName = "CodeAlta",
            // Keep the dispatcher alive through asynchronous browser/environment disposal.
            ShutdownMode = NeoApplicationShutdownMode.Explicit,
        }, desktop.RunAsync);
        return result == 0 ? desktop.ExitCode : result;
    }

    internal static bool IsApplicationDocument(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "app" && uri.Host == "codealta" && uri.Port == -1 &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/index.html" && string.IsNullOrEmpty(uri.Query);

    private async ValueTask RunAsync(NeoApplication application)
    {
        try
        {
            await using var window = application.CreateWindow(new NeoWindowOptions
            {
                Label = "main", Title = "CodeAlta — in development", Width = 1000, Height = 760, IsVisible = false,
            });
            application.MainWindow = window;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();

            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
            });
            var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true });
            builder.AddBootService(new BootService());
            builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));
            await using var rpc = builder.Build();
            window.Show();
            await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
            {
                ViewLabel = "main",
                BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
                BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://codealta"],
            });
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            await using var binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(new Uri("app://codealta/index.html"));
            await closed.Task;
            ExitCode = 0;
        }
        catch (Exception exception)
        {
            ExitCode = 1; // Also covers failures from asynchronous disposal after normal close.
            Console.Error.WriteLine(exception);
        }
        finally
        {
            application.ForceShutdown();
        }
    }
}
