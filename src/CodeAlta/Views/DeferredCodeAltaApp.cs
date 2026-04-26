using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Screenshot;

namespace CodeAlta.Views;

internal sealed class DeferredCodeAltaApp : IAsyncDisposable
{
    private readonly Padder _rootHost;
    private readonly Padder _sidebarHost;
    private readonly Padder _workspaceHost;
    private readonly Padder _commandBarHost;
    private Task<CodeAltaOwnedServices>? _ownedServicesTask;
    private CodeAltaApp? _app;
    private ConfigRecoveryDialog? _configRecoveryDialog;
    private Exception? _startupFailure;
    private bool _configRecoveryChecked;
    private bool _exitRequested;
    private bool _openProvidersAfterStartup;

    public DeferredCodeAltaApp()
    {
        // Build a synchronous placeholder shell first so Program can enter Terminal.RunAsync
        // without awaiting startup work before the UI claims the main thread.
        _sidebarHost = CreateStretchHost(BuildMessage("Loading sidebar..."));
        _workspaceHost = CreateStretchHost(BuildWorkspacePlaceholder("Starting CodeAlta..."));
        _commandBarHost = CreateStretchHost(new Placeholder { IsVisible = false });
        _rootHost = CreateStretchHost(
            new CodeAltaShellView(
                _sidebarHost,
                _workspaceHost,
                _commandBarHost,
                CodeAltaGlobalCommandConfigurator.Configure).Root);
        _rootHost.RegisterClipboardScreenshotCommand();
    }

    public ValueTask<TerminalInstance> RunAsync(CancellationToken cancellationToken)
        => Terminal.RunAsync(
            _rootHost,
            _ => OnIteration(cancellationToken),
            new TerminalRunOptions { UpdateWaitDuration = TimeSpan.FromMilliseconds(1) },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            return;
        }

        if (_ownedServicesTask is { IsCompletedSuccessfully: true })
        {
            await _ownedServicesTask.Result.DisposeAsync();
        }
    }

    private TerminalLoopResult OnIteration(CancellationToken cancellationToken)
    {
        if (_exitRequested)
        {
            return TerminalLoopResult.Stop;
        }

        if (_app is not null)
        {
            return _app.Tick(cancellationToken);
        }

        if (!_configRecoveryChecked && !EnsureConfigCanLoadBeforeStartup())
        {
            return TerminalLoopResult.Continue;
        }

        if (_configRecoveryDialog is not null)
        {
            return TerminalLoopResult.Continue;
        }

        if (_startupFailure is not null)
        {
            return TerminalLoopResult.Continue;
        }

        _ownedServicesTask ??= CodeAltaOwnedServices.CreateAsync(cancellationToken);
        if (!_ownedServicesTask.IsCompleted)
        {
            // Keep async service startup behind the terminal loop so the real app is attached
            // only after the UI is already running on the main thread.
            return TerminalLoopResult.Continue;
        }

        try
        {
            var ownedServices = _ownedServicesTask.GetAwaiter().GetResult();
            _app = CodeAltaApp.Create(ownedServices);
            _app.PrepareForRun();
            _rootHost.Content = _app.GetRoot();
            if (_openProvidersAfterStartup)
            {
                _openProvidersAfterStartup = false;
                _ = _app.OpenModelProvidersAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TerminalLoopResult.Continue;
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _sidebarHost.Content = BuildMessage("Startup failed.");
            _workspaceHost.Content = BuildWorkspacePlaceholder($"CodeAlta startup failed: {ex.Message}");
            _commandBarHost.Content = new Placeholder { IsVisible = false };
            return TerminalLoopResult.Continue;
        }

        return _app.Tick(cancellationToken);
    }

    private bool EnsureConfigCanLoadBeforeStartup()
    {
        _configRecoveryChecked = true;
        var configPath = GetGlobalConfigPath();
        if (!File.Exists(configPath))
        {
            _openProvidersAfterStartup = true;
            return true;
        }

        string content;
        try
        {
            content = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowConfigRecoveryDialog(
                configPath,
                string.Empty,
                new CodeAltaConfigValidationResult(false, $"Unable to read config file: {ex.Message}", null, null));
            return false;
        }

        var validation = CodeAltaConfigStore.ValidateGlobalConfigContent(content, configPath);
        if (validation.IsValid)
        {
            return true;
        }

        ShowConfigRecoveryDialog(configPath, content, validation);
        return false;
    }

    private void ShowConfigRecoveryDialog(string configPath, string content, CodeAltaConfigValidationResult validation)
    {
        if (_rootHost.App is not { } app)
        {
            _workspaceHost.Content = BuildWorkspacePlaceholder("CodeAlta config needs repair. Waiting for the terminal UI...");
            _configRecoveryChecked = false;
            return;
        }

        _sidebarHost.Content = BuildMessage("Config recovery");
        _workspaceHost.Content = BuildWorkspacePlaceholder("Repair ~/.alta/config.toml to continue startup.");
        _commandBarHost.Content = new Placeholder { IsVisible = false };
        _configRecoveryDialog = new ConfigRecoveryDialog(
            configPath,
            content,
            validation,
            saveAndContinue: () =>
            {
                _configRecoveryDialog = null;
                _startupFailure = null;
                _ownedServicesTask = null;
            },
            exit: () => _exitRequested = true);
        _configRecoveryDialog.Show(app);
    }

    private static string GetGlobalConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta",
            "config.toml");

    private static Padder CreateStretchHost(Visual content)
        => new(content)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

    private static Visual BuildMessage(string text)
        => new TextBlock
        {
            Wrap = true,
            Text = text,
        };

    private static Visual BuildWorkspacePlaceholder(string text)
        => new Center(BuildMessage(text))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
}
