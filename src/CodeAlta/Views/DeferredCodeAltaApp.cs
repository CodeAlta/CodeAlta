using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Screenshot;
using XenoAtom.Terminal.UI.Graphics;

namespace CodeAlta.Views;

internal sealed class DeferredCodeAltaApp : IAsyncDisposable
{
    private readonly Padder _rootHost;
    private readonly Padder _sidebarHost;
    private readonly Padder _workspaceHost;
    private readonly Padder _commandBarHost;
    private readonly ToastHost _toastHost;
    private readonly TerminalImageGraphicsPresenter _graphicsPresenter;
    private readonly CodeAltaUpdateService _updateService = new();
    private readonly PluginRuntimeManager? _prestartedPluginRuntime;
    private Task<CodeAltaOwnedServices>? _ownedServicesTask;
    private CodeAltaApp? _app;
    private ConfigRecoveryDialog? _configRecoveryDialog;
    private Exception? _startupFailure;
    private bool _configRecoveryChecked;
    private bool _exitRequested;
    private bool _openProvidersAfterStartup;
    private bool _updateToastShown;

    public DeferredCodeAltaApp(PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        _prestartedPluginRuntime = prestartedPluginRuntime;
        var sixelOptions = new TerminalSixelEncoderOptions();
        _graphicsPresenter = new TerminalImageGraphicsPresenter(new TerminalImageGraphicsPresenterOptions
        {
            SixelOptions = sixelOptions,
        });

        // Build a synchronous placeholder shell first so Program can enter Terminal.RunAsync
        // without awaiting startup work before the UI claims the main session.
        _sidebarHost = CreateStretchHost(BuildMessage(SR.T("Loading sidebar...")));
        _workspaceHost = CreateStretchHost(BuildWorkspacePlaceholder(SR.T("Starting CodeAlta...")));
        _commandBarHost = CreateStretchHost(new Placeholder { IsVisible = false });
        _toastHost = new ToastHost(
            new CodeAltaShellView(
                _sidebarHost,
                _workspaceHost,
                _commandBarHost,
                CodeAltaGlobalCommandConfigurator.Configure).Root);
        _rootHost = CreateStretchHost(_toastHost);
        _rootHost.RegisterClipboardScreenshotCommand();
        _updateService.Start();
    }

    public CodeAltaUpdateCheckSnapshot UpdateCheckSnapshot => _updateService.Snapshot;

    public ValueTask<TerminalInstance> RunAsync(CancellationToken cancellationToken)
        => Terminal.RunAsync(
            _rootHost,
            _ => OnIteration(cancellationToken),
            new TerminalRunOptions
            {
                GraphicsPresenter = _graphicsPresenter,
                UpdateWaitDuration = TimeSpan.FromMilliseconds(1),
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
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
        finally
        {
            _updateService.Dispose();
            _graphicsPresenter.Dispose();
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
            SyncUpdateNotifications();
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

        _ownedServicesTask ??= CodeAltaOwnedServices.CreateAsync(cancellationToken, _prestartedPluginRuntime);
        if (!_ownedServicesTask.IsCompleted)
        {
            // Keep async service startup behind the terminal loop so the real app is attached
            // only after the UI is already running on the main session.
            return TerminalLoopResult.Continue;
        }

        try
        {
            var ownedServices = _ownedServicesTask.GetAwaiter().GetResult();
            _app = CodeAltaApp.Create(ownedServices, _updateService);
            _app.PrepareForRun();
            _toastHost.Content = _app.GetRoot();
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
            _sidebarHost.Content = BuildMessage(SR.T("Startup failed."));
            _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("CodeAlta startup failed: {0}", ex.Message));
            _commandBarHost.Content = new Placeholder { IsVisible = false };
            return TerminalLoopResult.Continue;
        }

        SyncUpdateNotifications();
        return _app.Tick(cancellationToken);
    }

    private void SyncUpdateNotifications()
    {
        _updateService.SynchronizeUiState();
        var snapshot = _updateService.Snapshot;
        if (_updateToastShown || !snapshot.HasNewerVersion)
        {
            return;
        }

        _updateToastShown = true;
        ToastService.Show(() => new Toast
        {
            Title = SR.T("Update available"),
            Content = CodeAltaUpdateVisualFactory.CreateToastContent(snapshot, CopyUpdateCommand),
            Severity = ToastSeverity.Info,
            Duration = TimeSpan.FromSeconds(10),
            ShowCloseButton = true,
        });
    }

    private void CopyUpdateCommand(string command)
        => _rootHost.App?.Terminal.Clipboard.TrySetText(command);

    private bool EnsureConfigCanLoadBeforeStartup()
    {
        _configRecoveryChecked = true;
        var configPath = GetGlobalConfigPath();
        if (!File.Exists(configPath))
        {
            var configStore = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = GetGlobalRoot() });
            configStore.EnsureGlobalConfigExists();
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
                new CodeAltaConfigValidationResult(false, SR.T("Unable to read config file: {0}", ex.Message), null, null));
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
            _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("CodeAlta config needs repair. Waiting for the terminal UI..."));
            _configRecoveryChecked = false;
            return;
        }

        _sidebarHost.Content = BuildMessage(SR.T("Config recovery"));
        _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("Repair ~/.alta/config.toml to continue startup."));
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
        => Path.Combine(GetGlobalRoot(), "config.toml");

    private static string GetGlobalRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta");

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
