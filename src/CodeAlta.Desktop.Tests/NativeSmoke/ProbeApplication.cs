using System.Diagnostics;
using System.Runtime.InteropServices;
using NeoAstra;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Probe;

internal sealed class ProbeApplication(string root, bool smoke, Action<string> log)
{
    public int ExitCode { get; private set; } = 1;

    public async ValueTask RunAsync(NeoApplication application)
    {
        using var deadline = new CancellationTokenSource();
        if (smoke) deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            log($"HOST {RuntimeInformation.OSDescription}; {RuntimeInformation.RuntimeIdentifier}; .NET {Environment.Version}; base={AppContext.BaseDirectory}; cwd={Environment.CurrentDirectory}");
            if (OperatingSystem.IsWindows()) log($"CONSOLE hwnd={WindowsProbeDriver.GetConsoleWindow()} stdoutRedirected={Console.IsOutputRedirected} stderrRedirected={Console.IsErrorRedirected}");
            await using var window = application.CreateWindow(new NeoWindowOptions
            {
                Label = "main", Title = "CodeAlta Desktop Probe — fake data", Width = 1000, Height = 760, IsVisible = false,
            });
            application.MainWindow = window;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeCount = 0;
            window.Closed += (_, _) => closed.TrySetResult();
            window.CloseRequested += async request =>
            {
                // A real asynchronous native close decision, not a simulated window callback.
                await Task.Delay(30, request.DeadlineToken);
                closeCount++;
                if (smoke && closeCount == 1 && request.CanCancel)
                {
                    request.Cancel();
                    log("PASS native close canceled after asynchronous decision");
                    firstClose.TrySetResult();
                }
                else log($"NATIVE close approved reason={request.Reason}");
            };

            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(root, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
            }, token);
            log($"ENGINE {environment.RuntimeInfo}");
            using (var process = Process.GetCurrentProcess())
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.Contains("neoastra_native", StringComparison.OrdinalIgnoreCase)) log($"NATIVE_LIBRARY {module.FileName}");
            }

            var service = new ProbeService();
            var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true });
            builder.AddProbeService(service);
            await using var rpc = builder.Build();
            window.Show();
            await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
            {
                ViewLabel = "main",
                BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
                BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://codealta"],
            }, token);
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                request.Uri.IsAbsoluteUri && request.Uri.Scheme == "app" && request.Uri.Host == "codealta" && string.IsNullOrEmpty(request.Uri.UserInfo)
                    ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            await using var binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(new Uri("app://codealta/index.html"), token);

            if (smoke)
            {
                while (await view.EvaluateScriptAsync("document.documentElement.dataset.probeDone === 'true'", token) != "true")
                    await Task.Delay(50, token);
                var report = await view.EvaluateScriptAsync("document.querySelector('#results').textContent", token);
                log($"FRONTEND {report}");
                if (await view.EvaluateScriptAsync("document.documentElement.dataset.probePassed === 'true'", token) != "true")
                    throw new InvalidOperationException("Frontend smoke failed; see report above.");
                var state = service.State(new EmptyRequest());
                if (state is not { Started: 2, Disposed: 2 }) throw new InvalidOperationException($"Channel cleanup failed: {state}");
                log($"PASS backend enumerators started={state.Started} disposed={state.Disposed}");
                await CheckDialogsAsync(application, window, token);
                if (OperatingSystem.IsWindows()) WindowsProbeDriver.RequestUserClose(window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value);
                else window.Close();
                await firstClose.Task.WaitAsync(token);
                await Task.Delay(100, token);
                if (window.IsClosed) throw new InvalidOperationException("Cancel did not preserve native window.");
                window.Close();
            }

            await closed.Task.WaitAsync(token);
            log("PASS native window closed; draining binding/view/environment before loop exit");
            ExitCode = 0;
        }
        catch (Exception exception)
        {
            ExitCode = 1; // Includes exceptions from await-using disposal after the success assignment.
            log($"FAIL {exception}");
        }
        finally
        {
            application.ForceShutdown();
        }
    }

    private async Task CheckDialogsAsync(NeoApplication application, NeoWindow window, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            log("GAP automated native dialog interaction is implemented only for Windows; other RIDs unqualified");
            return;
        }

        var dialogs = NeoDialogs.CreateSystem(application.Dispatcher);
        foreach (var role in new[] { NeoDialogButtonRole.Cancel, NeoDialogButtonRole.Accept })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var title = $"CodeAlta Probe {Environment.ProcessId} {role}";
            var owner = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            // Driver sees and clicks only this process's owned native TaskDialog, without global input.
            var driver = Task.Run(() => WindowsProbeDriver.ClickDialogAsync(owner, title, role == NeoDialogButtonRole.Accept ? 1 : 2, timeout.Token), timeout.Token);
            try
            {
                var result = await dialogs.ShowMessageAsync(new NeoMessageDialogOptions
                {
                    Owner = window, Title = title, Message = "Isolated M0 native dialog", Buttons = [NeoDialogButtonRole.Accept, NeoDialogButtonRole.Cancel],
                }, timeout.Token);
                await driver;
                if (!result.IsSuccess || result.Value != role) throw new InvalidOperationException($"Native dialog failed: {result.Status}, {result.Value}, {result.Code}");
                log($"PASS visible owned native TaskDialog {role}");
            }
            finally
            {
                await timeout.CancelAsync();
                try { await driver; } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
        }
    }
}
