using System.Text;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using UiCommand = XenoAtom.Terminal.UI.Commands.Command;

namespace CodeAlta.Plugin.Mcp;

internal sealed class McpServersDialog
{
    private readonly McpManagementService _service;
    private readonly Func<McpManagementRequest> _createRequest;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly ListBox<McpServerRow> _serverList;
    private readonly BindableList<McpServerRow> _servers;
    private readonly State<int> _selectedServerIndex = new(-1);
    private readonly Markup _summaryMarkup;
    private readonly Markup _statusMarkup;
    private readonly Visual _detailHost;
    private int _selectedDetailTabIndex;
    private readonly object _testServerSyncRoot = new();
    private McpServerRow? _cachedDetailPaneRow;
    private Visual? _cachedDetailPane;
    private McpManagementSnapshot? _snapshot;
    private CancellationTokenSource? _activeTestServerCancellation;
    private bool _isClosed;
    private string _summaryText = "[dim]MCP configuration has not been loaded yet.[/]";
    private string _statusText = "[dim]Use Refresh to reload MCP JSON config and TOML policy.[/]";

    public McpServersDialog(
        McpManagementService service,
        Func<McpManagementRequest> createRequest,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        _service = service;
        _createRequest = createRequest;
        _openFileAsync = openFileAsync;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        _serverList = new ListBox<McpServerRow>()
            .MinWidth(36)
            .Stretch();
        _servers = _serverList.Items;
        _serverList.SelectedIndex(_selectedServerIndex.Bind.Value);
        _serverList.ItemTemplate = new DataTemplate<McpServerRow>(
            (DataTemplateValue<McpServerRow> value, in DataTemplateContext context) => BuildServerListItem(value.GetValue(), context.Index),
            null);

        _summaryMarkup = new Markup(() => _summaryText) { Wrap = true };
        _statusMarkup = new Markup(() => _statusText) { Wrap = true };
        _detailHost = new ComputedVisual(
            () =>
            {
                var index = _selectedServerIndex.Value;
                return index >= 0 && index < _servers.Count
                    ? BuildDetailPane(_servers[index])
                    : BuildEmptyState();
            })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var refreshButton = new Button($"{NerdFont.MdRefresh} Refresh")
            .Tone(ControlTone.Primary)
            .Click(() => Reload(null));
        var addButton = new Button($"{NerdFont.MdPlus} Add")
            .Tone(ControlTone.Success)
            .Click(AddServerDraft);
        var saveButton = new Button($"{NerdFont.MdContentSaveCheckOutline} Save")
            .Tone(ControlTone.Success)
            .IsEnabled(() => GetSelectedRow() is { } row && CanSaveServer(row))
            .Click(() => _ = SaveSelectedServerAsync());
        var removeButton = new Button($"{NerdFont.MdTrashCanOutline} Remove")
            .Tone(ControlTone.Error)
            .IsEnabled(() => GetSelectedRow() is { } row && CanRemoveServer(row))
            .Click(RemoveSelectedServer);
        var openJsonButton = new Button($"{NerdFont.MdCodeJson} Open JSON Config")
            .Click(() => _ = OpenSelectedJsonConfigAsync());
        var openPolicyButton = new Button($"{NerdFont.MdFileCogOutline} Open Policy TOML")
            .Click(() => _ = OpenSelectedPolicyAsync());

        var header = new Grid { HorizontalAlignment = Align.Stretch }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });
        header.Cell(_summaryMarkup, 0, 0);
        header.Cell(new HStack(addButton, saveButton, removeButton, refreshButton, openJsonButton, openPolicyButton) { Spacing = 1 }, 0, 1);

        var intro = new Markup("[dim]This dialog reads fixed MCP JSON config files and CodeAlta TOML policy. Add/Edit writes JSON server definitions; enablement and tool toggles write TOML policy. Test Server connects to configured stdio and HTTP/SSE servers to list tools with redacted diagnostics.[/]")
        {
            Wrap = true,
        };

        var leftPane = new VStack(
            new Group("MCP Servers")
                .Style(GroupStyle.Rounded)
                .Content(_serverList.Stretch())
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            new Markup("[dim]Rows include effective servers, invalid/missing config sources, and global entries shadowed by project config.[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var rightPane = new Group("MCP Server Details")
            .Style(GroupStyle.Rounded)
            .Content(_detailHost)
            .Padding(1)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var splitter = new HSplitter(leftPane, rightPane)
        {
            Ratio = 0.33,
            MinFirst = 32,
            MinSecond = 64,
        };

        var content = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(header, 0, 0);
        content.Cell(intro, 1, 0);
        content.Cell(splitter, 2, 0);
        content.Cell(_statusMarkup, 3, 0);
        var contentHost = new DialogLifetimeHost(content, CancelActiveTestServerOperation)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        _dialog = new Dialog()
            .Title("MCP Servers")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(contentHost);
        _dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Mcp.Manage.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the MCP servers dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
    {
        _dialog.Show();
        Reload(null);
        _dialog.App?.Focus(_serverList);
    }

    private void Reload(string? preferredKey)
    {
        var selectedKey = preferredKey ?? GetSelectedRow()?.Entry.Key;
        try
        {
            _snapshot = _service.RefreshSnapshot(_createRequest());
            _servers.Clear();
            _servers.AddRange(_snapshot.Servers.Select(static entry => new McpServerRow(entry)));
            _summaryText = BuildSummaryMarkup(_snapshot);
            _statusText = _snapshot.Servers.Count == 0
                ? "[dim]No MCP servers are configured. Use `alta mcp server add ...` or edit the JSON config paths shown in the details.[/]"
                : "[dim]Select a row to inspect normalized config, redacted connection fields, policy, and tool discovery results.[/]";

            var selectedIndex = selectedKey is null ? -1 : FindServerIndex(selectedKey);
            if (selectedIndex < 0 && _servers.Count > 0)
            {
                selectedIndex = 0;
            }

            SetSelectedServerIndex(selectedIndex);
        }
        catch (Exception ex)
        {
            _snapshot = null;
            _servers.Clear();
            SetSelectedServerIndex(-1);
            _summaryText = "[error]Failed to load MCP management data.[/]";
            _statusText = $"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]";
        }
    }

    private void AddServerDraft()
    {
        var key = CreateNewServerKey();
        var scope = _snapshot?.DefaultWriteScope ?? McpManagementScope.Project;
        if (scope == McpManagementScope.Project && string.IsNullOrWhiteSpace(_snapshot?.ProjectDirectory))
        {
            scope = McpManagementScope.Global;
        }

        var row = new McpServerRow(new McpManagementServerSnapshot
        {
            Key = key,
            DisplayName = key,
            State = McpManagementServerState.Configured,
            StateReason = "New unsaved MCP server",
            Transport = McpManagementTransport.Stdio,
            SourceScope = scope,
            SourcePath = GetConfigPathForScope(scope),
            PolicyEnabled = true,
        }, isDraft: true);
        _servers.Add(row);
        SetSelectedServerIndex(_servers.Count - 1);
        _selectedDetailTabIndex = 0;
        _statusText = "[warning]Added a new MCP server draft. Fill in the editable fields, then Save to write JSON config.[/]";
    }

    private async Task SaveSelectedServerAsync()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            _statusText = "[warning]Select an MCP server before saving.[/]";
            return;
        }

        if (!TryBuildServerEdit(row, out var edit, out var scope, out var errorMessage))
        {
            _statusText = $"[warning]{AnsiMarkup.Escape(errorMessage)}[/]";
            return;
        }

        try
        {
            var result = await _service.AddOrUpdateServerAsync(
                edit,
                scope,
                row.IsDraft ? null : row.OriginalKey,
                row.IsDraft ? null : row.OriginalScope,
                _createRequest(),
                CancellationToken.None);
            await PublishUiAsync(() =>
            {
                ApplySnapshot(_service.CachedSnapshot ?? _service.RefreshSnapshot(_createRequest()), result.Server);
                _statusText = result.CreatedFile
                    ? $"[success]Created {AnsiMarkup.Escape(result.Path)} and saved MCP server {AnsiMarkup.Escape(result.Server)}.[/]"
                    : $"[success]Saved MCP server {AnsiMarkup.Escape(result.Server)} to {AnsiMarkup.Escape(result.Path)}.[/]";
            });
        }
        catch (Exception ex)
        {
            await PublishUiAsync(() =>
            {
                _statusText = $"[error]Failed to save MCP server:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
            });
        }
    }

    private void RemoveSelectedServer()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            _statusText = "[warning]Select an MCP server before removing.[/]";
            return;
        }

        if (row.IsDraft)
        {
            _servers.Remove(row);
            SetSelectedServerIndex(Math.Clamp(_selectedServerIndex.Value, 0, _servers.Count - 1));
            _statusText = "[dim]Discarded unsaved MCP server draft.[/]";
            return;
        }

        if (!CanRemoveServer(row))
        {
            _statusText = "[warning]This row is not a removable MCP server definition.[/]";
            return;
        }

        if (row.OriginalScope is null)
        {
            _statusText = "[warning]This row is not a removable server definition.[/]";
            return;
        }

        var key = row.OriginalKey;
        var scope = row.OriginalScope.Value;
        _ = RemoveServerAsync(key, scope);
    }

    private async Task RemoveServerAsync(string key, McpManagementScope scope)
    {
        try
        {
            var result = await _service.RemoveServerAsync(key, scope, _createRequest(), CancellationToken.None);
            await PublishUiAsync(() =>
            {
                ApplySnapshot(_service.CachedSnapshot ?? _service.RefreshSnapshot(_createRequest()), null);
                _statusText = result.Changed
                    ? $"[success]Removed MCP server {AnsiMarkup.Escape(key)} from {AnsiMarkup.Escape(result.Path)}.[/]"
                    : $"[warning]MCP server {AnsiMarkup.Escape(key)} was not present in {AnsiMarkup.Escape(result.Path)}.[/]";
            });
        }
        catch (Exception ex)
        {
            await PublishUiAsync(() =>
            {
                _statusText = $"[error]Failed to remove MCP server:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
            });
        }
    }

    private Visual BuildDetailPane(McpServerRow row)
    {
        if (ReferenceEquals(_cachedDetailPaneRow, row) && _cachedDetailPane is not null)
        {
            return _cachedDetailPane;
        }

        var tabControl = new TabControl(
                new TabPage("Details", CreateDetailScrollViewer(BuildVersionedDetailContent(row, BuildDetailsVisual))),
                new TabPage(BuildToolsTabHeader(row), BuildVersionedDetailContent(row, BuildToolsVisual)))
            {
                AllowTabDragReorder = false,
                SelectedIndex = _selectedDetailTabIndex,
            }
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        tabControl.SelectionChanged((_, e) =>
        {
            if (_selectedDetailTabIndex != e.NewIndex)
            {
                _selectedDetailTabIndex = e.NewIndex;
            }
        });
        _cachedDetailPaneRow = row;
        _cachedDetailPane = tabControl;
        return tabControl;
    }

    private static Visual BuildVersionedDetailContent(McpServerRow row, Func<McpServerRow, Visual> build)
        => new ComputedVisual(() =>
        {
            _ = row.Version.Value;
            return build(row);
        })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

    private static Visual BuildToolsTabHeader(McpServerRow row)
        => new Markup(() =>
        {
            _ = row.Version.Value;
            return BuildToolsTabTitle(row.Entry);
        });

    private Visual BuildDetailsVisual(McpServerRow row)
    {
        var entry = row.Entry;
        var canEditPolicy = !row.IsDraft && entry.State is (McpManagementServerState.Configured or McpManagementServerState.Disabled);
        var canTestServer = canEditPolicy && entry.Transport is not null;
        var enablement = new HStack(
            new CheckBox("Enabled").IsChecked(row.EnabledState).IsEnabled(canEditPolicy),
            new Button("Apply")
                .Tone(ControlTone.Success)
                .IsEnabled(() => canEditPolicy && row.EnabledState.Value != (entry.PolicyEnabled != false))
                .Click(() => ApplyEnablement(row)),
            new Markup(() => !canEditPolicy
                ? "[dim]Enablement is available for effective configured servers only.[/]"
                : row.EnabledState.Value == (entry.PolicyEnabled != false)
                    ? "[dim]Saved[/]"
                    : "[warning]Unsaved policy change[/]") { Wrap = false })
        {
            Spacing = 1,
        };

        var jsonButton = new Button($"{NerdFont.MdCodeJson} Open JSON Config")
            .IsEnabled(!string.IsNullOrWhiteSpace(entry.SourcePath))
            .Click(() => _ = OpenFileAsync(entry.SourcePath, "MCP JSON config"));
        var policyButton = new Button($"{NerdFont.MdFileCogOutline} Open Policy TOML")
            .IsEnabled(_snapshot is not null)
            .Click(() => _ = OpenSelectedPolicyAsync());
        var refreshButton = new Button($"{NerdFont.MdRefresh} Refresh")
            .Tone(ControlTone.Primary)
            .Click(() => Reload(entry.Key));
        var testServerButton = new Button("Test Server")
            .IsEnabled(canTestServer)
            .Click(() => _ = TestServerAsync(row));

        var actions = new VStack(
            new HStack(jsonButton, policyButton, refreshButton, testServerButton) { Spacing = 1 },
            new Markup("[dim]Add/Edit/Remove remain in `alta mcp server ...`. Test Server lists tools with configured timeouts. HTTP/SSE uses static headers from JSON config; OAuth UX is deferred.[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

        return new VStack(
            new Markup(BuildSelectedTitleMarkup(entry)) { Wrap = true },
            new Markup(BuildStateMarkup(entry)) { Wrap = true },
            CreateSection("Editable JSON Server", BuildServerEditVisual(row)),
            CreateSection("Enablement", enablement),
            CreateSection("Actions", actions),
            CreateSection("Normalized Details", BuildPropertiesGrid(entry)),
            CreateSection("Policy", new Markup(BuildPolicyMarkup(entry, _snapshot)) { Wrap = true }),
            CreateSection("Diagnostics", new Markup(BuildDiagnosticsMarkup(entry)) { Wrap = true }))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Spacing = 1,
        };
    }

    private static ScrollViewer CreateDetailScrollViewer(Visual content)
        => new(content)
        {
            HorizontalScrollEnabled = false,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

    private Visual BuildServerEditVisual(McpServerRow row)
        => row.Entry.Transport is null && !row.IsDraft
            ? new Markup("[dim]This row represents a missing or invalid config source, not an editable server definition. Use Open JSON Config to fix source-level JSON errors, or Add to create a new server.[/]") { Wrap = true }
            : BuildServerEditForm(row);

    private Visual BuildServerEditForm(McpServerRow row)
    {
        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                RowGap = 1,
            }
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });

        var rowIndex = 0;
        AddEditRow(form, ref rowIndex, "Key", new TextBox(row.KeyState.Bind.Value).Stretch(), new TextBlock(row.IsDraft ? "new" : "editable"));
        AddEditRow(form, ref rowIndex, "Scope", CreateScopeSelect(row), new TextBlock(() => GetConfigPathForScope(row.ScopeState.Value) ?? string.Empty) { Wrap = true });
        AddEditRow(form, ref rowIndex, "Transport", CreateTransportSelect(row), CreateSpacer());
        AddEditRow(form, ref rowIndex, "Command", new TextBox(row.CommandState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Stdio).Stretch(), new TextBlock("stdio"));
        AddEditRow(form, ref rowIndex, "Arguments", new TextBox(row.ArgsState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Stdio).Stretch(), new TextBlock("separate with ;"));
        AddEditRow(form, ref rowIndex, "Working Dir", new TextBox(row.CwdState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Stdio).Stretch(), CreateSpacer());
        AddEditRow(form, ref rowIndex, "Environment", new TextBox(row.EnvState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Stdio).IsPassword(row.HasRedactedEnv).PasswordRevealMode(PasswordRevealMode.WhileFocused).Stretch(), new TextBlock("KEY=VALUE; ..."));
        AddEditRow(form, ref rowIndex, "URL", new TextBox(row.UrlState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Http).Stretch(), new TextBlock("http/sse"));
        AddEditRow(form, ref rowIndex, "Headers", new TextBox(row.HeadersState.Bind.Value).IsEnabled(() => row.TransportState.Value == McpManagementTransport.Http).IsPassword(row.HasRedactedHeaders).PasswordRevealMode(PasswordRevealMode.WhileFocused).Stretch(), new TextBlock("KEY=VALUE; ..."));

        var guidance = new Markup("[dim]Save writes the selected JSON config scope. Arguments use semicolon-separated values. Env/header fields use semicolon-separated KEY=VALUE pairs and support ${NAME} environment-variable placeholders. Redacted placeholders must be replaced before saving to avoid overwriting secrets.[/]")
        {
            Wrap = true,
        };

        return new VStack(form, guidance)
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };
    }

    private Select<ScopeOption> CreateScopeSelect(McpServerRow row)
    {
        var select = new Select<ScopeOption>().MinWidth(12);
        var options = string.IsNullOrWhiteSpace(_snapshot?.ProjectDirectory)
            ? new[] { ScopeOption.Global }
            : [ScopeOption.Project, ScopeOption.Global];
        foreach (var option in options)
        {
            select.Items.Add(option);
        }

        select.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Scope == row.ScopeState.Value));
        select.SelectionChanged((_, e) =>
        {
            if ((uint)e.NewIndex < (uint)options.Length)
            {
                row.ScopeState.Value = options[e.NewIndex].Scope;
            }
        });
        return select;
    }

    private static Select<TransportOption> CreateTransportSelect(McpServerRow row)
    {
        var options = new[] { TransportOption.Stdio, TransportOption.Http };
        var select = new Select<TransportOption>().MinWidth(12);
        foreach (var option in options)
        {
            select.Items.Add(option);
        }

        select.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Transport == row.TransportState.Value));
        select.SelectionChanged((_, e) =>
        {
            if ((uint)e.NewIndex < (uint)options.Length)
            {
                row.TransportState.Value = options[e.NewIndex].Transport;
            }
        });
        return select;
    }

    private static void AddEditRow(Grid form, ref int row, string label, Visual content, Visual trailing)
    {
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.Cell(new TextBlock(label) { VerticalAlignment = Align.Center }, row, 0);
        form.Cell(content.Stretch(), row, 1);
        form.Cell(trailing, row, 2);
        row++;
    }

    private static Visual CreateSpacer()
        => new TextBlock(string.Empty);

    private void ApplyEnablement(McpServerRow row)
    {
        var enabled = row.EnabledState.Value;
        if (enabled == (row.Entry.PolicyEnabled != false))
        {
            _statusText = "[dim]No MCP policy changes to save.[/]";
            return;
        }

        try
        {
            var request = _createRequest();
            var scope = row.Entry.SourceScope ?? (_snapshot?.DefaultWriteScope ?? McpManagementScope.Global);
            _service.SetServerEnabledAsync(row.Entry.Key, enabled, scope, request, CancellationToken.None).GetAwaiter().GetResult();
            var statusText = enabled
                ? "[success]MCP server policy enabled.[/]"
                : "[success]MCP server policy disabled.[/]";
            Reload(row.Entry.Key);
            _statusText = statusText;
        }
        catch (Exception ex)
        {
            row.EnabledState.Value = row.Entry.PolicyEnabled != false;
            _statusText = $"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]";
        }
    }

    private Task TestServerAsync(McpServerRow row)
    {
        if (row.Entry.State is not (McpManagementServerState.Configured or McpManagementServerState.Disabled))
        {
            _statusText = "[warning]Select a configured MCP server before testing.[/]";
            return Task.CompletedTask;
        }

        var testCancellation = BeginTestServerOperation();
        if (testCancellation is null)
        {
            return Task.CompletedTask;
        }

        var serverKey = row.Entry.Key;
        var request = _createRequest();
        var cancellationToken = testCancellation.Token;
        _statusText = $"[dim]Testing MCP server {AnsiMarkup.Escape(serverKey)}...[/]";
        return Task.Run(() => RunTestServerAsync(row, serverKey, request, testCancellation, cancellationToken));
    }

    private async Task RunTestServerAsync(
        McpServerRow row,
        string serverKey,
        McpManagementRequest request,
        CancellationTokenSource testCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.TestServerAsync(serverKey, request, cancellationToken);
            var statusText = result.Status switch
            {
                McpManagementTestStatus.Succeeded => $"[success]MCP server {AnsiMarkup.Escape(result.Server)} listed {result.Tools.Count} tool(s).[/]",
                McpManagementTestStatus.Unsupported => $"[warning]MCP server {AnsiMarkup.Escape(result.Server)} uses an unsupported transport.[/]",
                McpManagementTestStatus.TimedOut => $"[warning]MCP server {AnsiMarkup.Escape(result.Server)} did not finish before the configured timeout.[/]",
                McpManagementTestStatus.Canceled => $"[warning]MCP server {AnsiMarkup.Escape(result.Server)} test was canceled.[/]",
                _ => $"[error]MCP server {AnsiMarkup.Escape(result.Server)} test failed.[/]",
            };

            if (!IsActiveTestServerOperation(testCancellation))
            {
                return;
            }

            await PublishUiAsync(() =>
            {
                if (!IsActiveTestServerOperation(testCancellation))
                {
                    return;
                }

                ApplyTestResult(row, result);
                _statusText = statusText;
                _dialog.App?.Focus(_serverList);
            });
        }
        catch (Exception ex)
        {
            if (!IsActiveTestServerOperation(testCancellation))
            {
                return;
            }

            await PublishUiAsync(() =>
            {
                if (!IsActiveTestServerOperation(testCancellation))
                {
                    return;
                }

                _statusText = $"[error]MCP server test failed:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
            });
        }
        finally
        {
            EndTestServerOperation(testCancellation);
        }
    }

    private CancellationTokenSource? BeginTestServerOperation()
    {
        CancellationTokenSource? previous;
        var current = new CancellationTokenSource();
        lock (_testServerSyncRoot)
        {
            if (_isClosed)
            {
                current.Dispose();
                return null;
            }

            previous = _activeTestServerCancellation;
            _activeTestServerCancellation = current;
        }

        CancelTestServerOperation(previous);
        return current;
    }

    private bool IsActiveTestServerOperation(CancellationTokenSource cancellation)
    {
        lock (_testServerSyncRoot)
        {
            return !_isClosed && ReferenceEquals(_activeTestServerCancellation, cancellation);
        }
    }

    private void EndTestServerOperation(CancellationTokenSource cancellation)
    {
        lock (_testServerSyncRoot)
        {
            if (ReferenceEquals(_activeTestServerCancellation, cancellation))
            {
                _activeTestServerCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private void CancelActiveTestServerOperation()
    {
        CancellationTokenSource? cancellation;
        lock (_testServerSyncRoot)
        {
            _isClosed = true;
            cancellation = _activeTestServerCancellation;
            _activeTestServerCancellation = null;
        }

        CancelTestServerOperation(cancellation);
    }

    private static void CancelTestServerOperation(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplyTestResult(McpServerRow testedRow, McpManagementServerTestResult result)
    {
        var index = IndexOfRow(testedRow);
        if (index < 0)
        {
            index = FindServerIndex(result.Server);
        }

        if (index < 0)
        {
            index = FindServerIndex(testedRow.Entry.Key);
        }

        if (index < 0)
        {
            return;
        }

        var row = _servers[index];
        var current = row.Entry;
        var tools = result.Tools;
        var updated = current with
        {
            Tools = tools,
            ExposedToolCount = tools.Count(static tool => tool.Enabled),
            TotalToolCount = tools.Count,
            LastTestStatus = result.Status,
            LastTestedAt = result.CompletedAt,
            Diagnostics = MergeDiagnostics(current.Diagnostics, result.Diagnostics),
        };

        row.UpdateEntry(updated);
        UpdateSnapshotServer(index, updated);
        SetSelectedServerIndex(index);
    }

    private void UpdateSnapshotServer(int index, McpManagementServerSnapshot updated)
    {
        if (_snapshot is null)
        {
            return;
        }

        var servers = _snapshot.Servers.ToArray();
        if ((uint)index < (uint)servers.Length && IsSameServerRow(servers[index], updated))
        {
            servers[index] = updated;
        }
        else
        {
            var snapshotIndex = Array.FindIndex(servers, candidate => IsSameServerRow(candidate, updated));
            if (snapshotIndex < 0)
            {
                return;
            }

            servers[snapshotIndex] = updated;
        }

        _snapshot = _snapshot with
        {
            Servers = servers,
            Summary = _snapshot.Summary with
            {
                ExposedToolCount = servers.Sum(static server => server.ExposedToolCount),
                TotalToolCount = servers.Sum(static server => server.TotalToolCount),
            },
        };
        _summaryText = BuildSummaryMarkup(_snapshot);
    }

    private static bool IsSameServerRow(McpManagementServerSnapshot left, McpManagementServerSnapshot right)
        => string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase) &&
           left.SourceScope == right.SourceScope &&
           left.State == right.State;

    private static IReadOnlyList<string> MergeDiagnostics(IReadOnlyList<string> existing, IReadOnlyList<string> latest)
        => latest.Count == 0 ? existing : existing.Concat(latest).Distinct(StringComparer.Ordinal).ToArray();

    private async Task ApplyToolEnablementAsync(McpServerRow row, McpManagementToolSnapshot tool, bool enabled, Action<bool> restoreChecked)
    {
        var original = !enabled;
        try
        {
            var scope = row.Entry.SourceScope ?? (_snapshot?.DefaultWriteScope ?? McpManagementScope.Global);
            var result = await _service.SetToolEnabledAsync(row.Entry.Key, tool.Name, enabled, scope, _createRequest(), CancellationToken.None);
            await PublishUiAsync(() =>
            {
                if (_service.CachedSnapshot is { } snapshot)
                {
                    ApplySnapshot(snapshot, result.Server);
                }

                _statusText = enabled
                    ? $"[success]MCP tool {AnsiMarkup.Escape(tool.Name)} enabled; removed from disabled_tools where present.[/]"
                    : $"[success]MCP tool {AnsiMarkup.Escape(tool.Name)} disabled; persisted to disabled_tools.[/]";
            });
        }
        catch (Exception ex)
        {
            await PublishUiAsync(() =>
            {
                restoreChecked(original);
                _statusText = $"[error]Failed to update MCP tool policy:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
            });
        }
    }

    private async Task OpenSelectedJsonConfigAsync()
    {
        var row = GetSelectedRow();
        var path = row?.Entry.SourcePath
            ?? _snapshot?.Sources.FirstOrDefault(static source => source.Scope == McpManagementScope.Project)?.Path
            ?? _snapshot?.Sources.FirstOrDefault(static source => source.Scope == McpManagementScope.Global)?.Path;
        await OpenFileAsync(path, "MCP JSON config");
    }

    private void ApplySnapshot(McpManagementSnapshot snapshot, string? preferredKey)
    {
        var rows = snapshot.Servers.Select(static entry => new McpServerRow(entry)).ToArray();
        _snapshot = snapshot;
        _servers.Clear();
        _servers.AddRange(rows);
        _summaryText = BuildSummaryMarkup(_snapshot);
        var selectedIndex = preferredKey is null ? -1 : FindServerIndex(preferredKey);
        if (selectedIndex < 0 && _servers.Count > 0)
        {
            selectedIndex = 0;
        }

        SetSelectedServerIndex(selectedIndex);
    }


    private async Task OpenSelectedPolicyAsync()
    {
        var scope = GetSelectedRow()?.Entry.SourceScope ?? _snapshot?.DefaultWriteScope ?? McpManagementScope.Global;
        var path = scope == McpManagementScope.Project && !string.IsNullOrWhiteSpace(_snapshot?.Policy.ProjectPath)
            ? _snapshot!.Policy.ProjectPath
            : _snapshot?.Policy.GlobalPath;
        await OpenFileAsync(path, "MCP policy TOML");
    }

    private async Task OpenFileAsync(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _statusText = $"[warning]No {AnsiMarkup.Escape(label)} path is available.[/]";
            return;
        }

        try
        {
            await _openFileAsync(path, CancellationToken.None);
            await PublishUiAsync(() =>
            {
                _statusText = $"[success]Opened {AnsiMarkup.Escape(label)}.[/]";
            });
        }
        catch (Exception ex)
        {
            await PublishUiAsync(() =>
            {
                _statusText = $"[error]Failed to open {AnsiMarkup.Escape(label)}:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
            });
        }
    }

    private Task PublishUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsClosed() || _dialog.App is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            return _dialog.Dispatcher.InvokeAsync(() =>
            {
                if (!IsClosed() && _dialog.App is not null)
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException) when (IsClosed() || _dialog.App is null)
        {
            return Task.CompletedTask;
        }
    }

    private bool IsClosed()
    {
        lock (_testServerSyncRoot)
        {
            return _isClosed;
        }
    }

    private Visual BuildServerListItem(McpServerRow row, int index)
        => new Markup(() =>
        {
            _ = row.Version.Value;
            return BuildServerListItemMarkup(row.Entry, _selectedServerIndex.Value == index);
        })
        {
            Wrap = false,
        };

    internal static string BuildServerListItemMarkup(McpManagementServerSnapshot entry, bool selected)
    {
        var (tone, icon) = GetStatusToneAndIcon(entry.State);
        var hint = BuildListHint(entry);
        var hintMarkup = selected ? AnsiMarkup.Escape(hint) : $"[dim]{AnsiMarkup.Escape(hint)}[/]";
        return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.DisplayName)}[/] {hintMarkup}";
    }

    internal static string BuildSummaryMarkup(McpManagementSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("[bold]MCP:[/] ");
        builder.Append(snapshot.Policy.Enabled ? "[success]enabled by policy[/]" : "[warning]disabled by policy[/]");
        if (!string.IsNullOrWhiteSpace(snapshot.ProjectDirectory))
        {
            builder.Append("  [bold]Project root:[/] ").Append(AnsiMarkup.Escape(snapshot.ProjectDirectory));
        }

        builder.Append("  [bold]Servers:[/] ").Append(snapshot.Summary.ActiveServerCount).Append('/').Append(snapshot.Summary.ConfiguredServerCount);
        builder.Append("  [bold]Tools:[/] ").Append(snapshot.Summary.ExposedToolCount).Append('/').Append(snapshot.Summary.TotalToolCount);
        if (snapshot.Summary.InvalidSourceCount > 0)
        {
            builder.Append("  [error]Invalid sources: ").Append(snapshot.Summary.InvalidSourceCount).Append("[/]");
        }

        if (snapshot.Summary.ShadowedServerCount > 0)
        {
            builder.Append("  [warning]Shadowed: ").Append(snapshot.Summary.ShadowedServerCount).Append("[/]");
        }

        return builder.ToString();
    }

    private static string BuildSelectedTitleMarkup(McpManagementServerSnapshot entry)
    {
        var (tone, icon) = GetStatusToneAndIcon(entry.State);
        return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.DisplayName)}[/] [dim]· {AnsiMarkup.Escape(FormatStateText(entry.State))}[/]";
    }

    private static string BuildStateMarkup(McpManagementServerSnapshot entry)
    {
        var builder = new StringBuilder();
        builder.Append("[bold]State:[/] ").Append(AnsiMarkup.Escape(FormatStateText(entry.State)));
        if (!string.IsNullOrWhiteSpace(entry.StateReason))
        {
            builder.Append(" — ").Append(AnsiMarkup.Escape(entry.StateReason));
        }

        if (entry.OverridesGlobal)
        {
            builder.Append(" [warning](overrides global)[/]");
        }

        return builder.ToString();
    }

    private static Visual BuildPropertiesGrid(McpManagementServerSnapshot entry)
    {
        var rows = new List<(string Label, string? Value)>
        {
            ("Key", entry.Key),
            ("Transport", entry.Transport?.ToString()),
            ("Source Scope", entry.SourceScope?.ToString()),
            ("Source Path", entry.SourcePath),
            ("Source Format", entry.SourceFormat?.ToString()),
            ("Command", entry.Command),
            ("Arguments", entry.Args.Count > 0 ? string.Join(" ", entry.Args) : null),
            ("Working Directory", entry.Cwd),
            ("Environment", FormatDictionary(entry.Env)),
            ("URL", entry.Url),
            ("Headers", FormatDictionary(entry.Headers)),
            ("Shadowed Global", entry.ShadowedGlobalPath),
            ("Tools", $"{entry.ExposedToolCount}/{entry.TotalToolCount} exposed"),
        };

        var grid = new Grid { HorizontalAlignment = Align.Stretch, RowGap = 0 }
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });

        var rowIndex = 0;
        foreach (var (label, value) in rows.Where(static row => !string.IsNullOrWhiteSpace(row.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Cell(new TextBlock(label) { VerticalAlignment = Align.Start }, rowIndex, 0);
            grid.Cell(new TextBlock(value!) { Wrap = true }, rowIndex, 1);
            rowIndex++;
        }

        return rowIndex == 0
            ? new TextBlock("No normalized details available.") { Wrap = true }
            : grid;
    }

    private static string BuildPolicyMarkup(McpManagementServerSnapshot entry, McpManagementSnapshot? snapshot)
    {
        var builder = new StringBuilder();
        if (snapshot is not null)
        {
            builder.Append("Global policy: ").AppendLine(AnsiMarkup.Escape(snapshot.Policy.GlobalPath));
            if (!string.IsNullOrWhiteSpace(snapshot.Policy.ProjectPath))
            {
                builder.Append("Project policy: ").AppendLine(AnsiMarkup.Escape(snapshot.Policy.ProjectPath));
            }

            builder.Append("MCP enabled: ").Append(snapshot.Policy.Enabled ? "[success]true[/]" : "[warning]false[/]").AppendLine();
            builder.Append("Connect on startup: ").Append(snapshot.Policy.ConnectOnStartup ? "true" : "false").AppendLine();
            builder.Append("Direct exposure: ").Append(AnsiMarkup.Escape(snapshot.Policy.DirectExposure)).AppendLine();
            if (!string.IsNullOrWhiteSpace(snapshot.Policy.Diagnostic))
            {
                builder.Append("[error]").Append(AnsiMarkup.Escape(snapshot.Policy.Diagnostic)).AppendLine("[/]");
            }
        }

        builder.Append("Server enabled: ").Append(entry.PolicyEnabled == false ? "[warning]false[/]" : "[success]true[/]").AppendLine();
        if (entry.PolicyRequired is not null)
        {
            builder.Append("Required: ").Append(entry.PolicyRequired.Value ? "true" : "false").AppendLine();
        }

        AppendList(builder, "Direct tools", entry.DirectTools);
        AppendList(builder, "Allowed tools", entry.AllowedTools);
        AppendList(builder, "Disabled tools", entry.DisabledTools);
        if (entry.StartupTimeoutMs is not null)
        {
            builder.Append("Startup timeout: ").Append(entry.StartupTimeoutMs.Value).AppendLine(" ms");
        }

        if (entry.ToolTimeoutMs is not null)
        {
            builder.Append("Tool timeout: ").Append(entry.ToolTimeoutMs.Value).AppendLine(" ms");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildDiagnosticsMarkup(McpManagementServerSnapshot entry)
    {
        if (entry.Diagnostics.Count == 0)
        {
            return "[success]No diagnostics.[/]";
        }

        var builder = new StringBuilder();
        foreach (var diagnostic in entry.Diagnostics)
        {
            builder.Append("[warning]•[/] ").Append(AnsiMarkup.Escape(diagnostic)).AppendLine();
        }

        return builder.ToString();
    }

    internal static string BuildToolsMarkup(McpManagementServerSnapshot entry)
    {
        if (entry.Tools.Count == 0)
        {
            return entry.LastTestStatus switch
            {
                McpManagementTestStatus.Unsupported => "[warning]This MCP server uses an unsupported transport.[/]",
                McpManagementTestStatus.TimedOut => "[warning]Tool discovery timed out before tools were listed.[/]",
                McpManagementTestStatus.Canceled => "[warning]Tool discovery was canceled before tools were listed.[/]",
                McpManagementTestStatus.Failed => "[error]Tool discovery failed before tools were listed. See Diagnostics above.[/]",
                _ => "[dim]No tools discovered yet. Use Test Server on the Details tab to connect and list tools. Columns: Enabled, raw MCP name/title, description, qualified alias, and availability.[/]",
            };
        }

        var builder = new StringBuilder();
        builder.Append("[bold]Enabled  Raw MCP name/title  Qualified alias  Availability  Description[/]").AppendLine();
        foreach (var tool in entry.Tools)
        {
            var enabled = tool.Enabled ? "[success]yes[/]" : "[warning]no[/]";
            var label = string.IsNullOrWhiteSpace(tool.Title)
                ? tool.Name
                : string.Concat(tool.Name, " (", tool.Title, ")");
            var availability = tool.Enabled ? $"[success]{AnsiMarkup.Escape(tool.Availability)}[/]" : $"[warning]{AnsiMarkup.Escape(tool.Availability)}[/]";
            builder
                .Append(enabled).Append("  ")
                .Append(AnsiMarkup.Escape(label)).Append("  ")
                .Append(AnsiMarkup.Escape(tool.Alias)).Append("  ")
                .Append(availability).Append("  ")
                .Append(AnsiMarkup.Escape(tool.Description ?? string.Empty))
                .AppendLine();
            if (!string.IsNullOrWhiteSpace(tool.Diagnostic))
            {
                builder.Append("[dim]  ↳ ").Append(AnsiMarkup.Escape(tool.Diagnostic)).AppendLine("[/]");
            }
        }

        if (entry.LastTestStatus == McpManagementTestStatus.Succeeded && entry.LastTestedAt is not null)
        {
            builder.Append("[dim]")
                .Append(entry.ExposedToolCount)
                .Append('/')
                .Append(entry.TotalToolCount)
                .Append(" tools enabled; last tested ")
                .Append(AnsiMarkup.Escape(entry.LastTestedAt.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)))
                .Append("[/]");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string BuildToolsTabTitle(McpManagementServerSnapshot entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"Tools ({entry.Tools.Count})";
    }

    private bool CanSaveServer(McpServerRow row)
        => TryBuildServerEdit(row, out _, out _, out _);

    private static bool CanRemoveServer(McpServerRow row)
        => row.IsDraft || row.Entry.State is McpManagementServerState.Configured or McpManagementServerState.Disabled or McpManagementServerState.Shadowed;

    private bool TryBuildServerEdit(McpServerRow row, out McpManagementServerEdit edit, out McpManagementScope scope, out string errorMessage)
    {
        if (row.Entry.Transport is null && !row.IsDraft)
        {
            scope = row.ScopeState.Value;
            edit = null!;
            errorMessage = "This row is not an editable MCP server definition.";
            return false;
        }

        scope = row.ScopeState.Value;
        if (scope == McpManagementScope.Project && string.IsNullOrWhiteSpace(_snapshot?.ProjectDirectory))
        {
            edit = null!;
            errorMessage = "Project scope requires an open project directory.";
            return false;
        }

        var key = (row.KeyState.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            edit = null!;
            errorMessage = "Server key is required.";
            return false;
        }

        var selectedScope = scope;
        if (_servers.Any(candidate => !ReferenceEquals(candidate, row) && string.Equals((candidate.KeyState.Value ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase) && candidate.ScopeState.Value == selectedScope))
        {
            edit = null!;
            errorMessage = $"Another MCP server row already uses key '{key}' in {FormatScopeText(scope)} scope.";
            return false;
        }

        if (!row.IsDraft && HasRedactedPlaceholder(row))
        {
            edit = null!;
            errorMessage = "Replace redacted placeholders before saving, or open the JSON config to edit secrets directly.";
            return false;
        }

        try
        {
            var isStdio = row.TransportState.Value == McpManagementTransport.Stdio;
            var args = isStdio ? ParseList(row.ArgsState.Value ?? string.Empty) : [];
            var env = isStdio ? ParseAssignments(row.EnvState.Value ?? string.Empty, "Environment") : new Dictionary<string, string>(StringComparer.Ordinal);
            var headers = isStdio ? new Dictionary<string, string>(StringComparer.Ordinal) : ParseAssignments(row.HeadersState.Value ?? string.Empty, "Headers");
            var command = (row.CommandState.Value ?? string.Empty).Trim();
            var url = (row.UrlState.Value ?? string.Empty).Trim();
            if (isStdio && string.IsNullOrWhiteSpace(command))
            {
                edit = null!;
                errorMessage = "Stdio servers require a command.";
                return false;
            }

            if (!isStdio && string.IsNullOrWhiteSpace(url))
            {
                edit = null!;
                errorMessage = "HTTP/SSE servers require a URL.";
                return false;
            }

            edit = new McpManagementServerEdit
            {
                Key = key,
                Transport = row.TransportState.Value,
                Command = isStdio ? command : null,
                Args = args,
                Cwd = isStdio && !string.IsNullOrWhiteSpace(row.CwdState.Value) ? row.CwdState.Value.Trim() : null,
                Env = env,
                Url = isStdio ? null : url,
                Headers = headers,
            };
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            edit = null!;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool HasRedactedPlaceholder(McpServerRow row)
        => ContainsRedactedPlaceholder(row.ArgsState.Value ?? string.Empty) ||
           ContainsRedactedPlaceholder(row.EnvState.Value ?? string.Empty) ||
           ContainsRedactedPlaceholder(row.UrlState.Value ?? string.Empty) ||
           ContainsRedactedPlaceholder(row.HeadersState.Value ?? string.Empty);

    private static bool ContainsRedactedPlaceholder(string value)
        => value.Contains("[redacted]", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> ParseList(string text)
        => SplitEntries(text).ToArray();

    internal static IReadOnlyDictionary<string, string> ParseAssignments(string text, string label)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in SplitEntries(text))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException($"{label} entries must use KEY=VALUE syntax.");
            }

            var key = entry[..separator].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException($"{label} entries must include a non-empty key.");
            }

            result[key] = entry[(separator + 1)..].Trim();
        }

        return result;
    }

    private static IEnumerable<string> SplitEntries(string text)
        => (text ?? string.Empty)
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value));

    private string CreateNewServerKey()
    {
        var index = 1;
        string key;
        do
        {
            key = $"server-{index++}";
        }
        while (_servers.Any(row => string.Equals((row.KeyState.Value ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase)));

        return key;
    }

    private string? GetConfigPathForScope(McpManagementScope scope)
        => scope == McpManagementScope.Project
            ? _snapshot?.Sources.FirstOrDefault(static source => source.Scope == McpManagementScope.Project)?.Path
            : _snapshot?.Sources.FirstOrDefault(static source => source.Scope == McpManagementScope.Global)?.Path;

    private static string FormatScopeText(McpManagementScope scope)
        => scope == McpManagementScope.Project ? "project" : "global";

    private Visual BuildToolsVisual(McpServerRow row)
    {
        var entry = row.Entry;
        if (entry.Tools.Count == 0)
        {
            return new Markup(BuildToolsMarkup(entry)) { Wrap = true };
        }

        var canEditPolicy = !row.IsDraft && entry.State is McpManagementServerState.Configured or McpManagementServerState.Disabled;
        var toolRows = entry.Tools.Select(static tool => new McpToolGridRow(tool)).ToArray();
        var document = new DataGridListDocument<McpToolGridRow>();
        using (document.BeginUpdate())
        {
            document
                .AddColumn(new DataGridColumnInfo<bool>("enabled", "Enabled", !canEditPolicy, McpToolGridRow.Accessor.Enabled))
                .AddColumn(new DataGridColumnInfo<string>("name", "Raw MCP name/title", true, McpToolGridRow.Accessor.NameTitle))
                .AddColumn(new DataGridColumnInfo<string>("alias", "Qualified alias", true, McpToolGridRow.Accessor.Alias))
                .AddColumn(new DataGridColumnInfo<string>("availability", "Availability", true, McpToolGridRow.Accessor.Availability))
                .AddColumn(new DataGridColumnInfo<string>("description", "Description", true, McpToolGridRow.Accessor.Description))
                .AddColumn(new DataGridColumnInfo<string>("diagnostic", "Diagnostic", true, McpToolGridRow.Accessor.Diagnostic));

            foreach (var toolRow in toolRows)
            {
                toolRow.EnabledChanged += (_, enabled) => _ = ApplyToolEnablementAsync(row, toolRow.Tool, enabled, toolRow.RestoreEnabled);
                document.AddRow(toolRow);
            }
        }

        var grid = new DataGridControl { View = new DataGridDocumentView(document) }
            .SelectionMode(DataGridSelectionMode.Cell)
            .EditMode(DataGridEditMode.OnEnter)
            .FilterRowVisible(false)
            .ShowHeader(true)
            .ShowRowAnchor(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        ConfigureToolsGridColumns(grid, canEditPolicy);
        return new ScrollViewer(grid, focusable: false)
        {
            HorizontalScrollEnabled = true,
            VerticalScrollEnabled = true,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    private static void ConfigureToolsGridColumns(DataGridControl grid, bool canEditPolicy)
    {
        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "enabled",
            Header = new TextBlock("Enabled"),
            TypedValueAccessor = McpToolGridRow.Accessor.Enabled,
            Width = GridLength.Auto,
            ReadOnly = !canEditPolicy,
            Sortable = true,
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "name",
            Header = new TextBlock("Raw MCP name/title"),
            TypedValueAccessor = McpToolGridRow.Accessor.NameTitle,
            Width = GridLength.Star(2),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "alias",
            Header = new TextBlock("Qualified alias"),
            TypedValueAccessor = McpToolGridRow.Accessor.Alias,
            Width = GridLength.Star(2),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "availability",
            Header = new TextBlock("Availability"),
            TypedValueAccessor = McpToolGridRow.Accessor.Availability,
            Width = GridLength.Star(1),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "description",
            Header = new TextBlock("Description"),
            TypedValueAccessor = McpToolGridRow.Accessor.Description,
            Width = GridLength.Star(3),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "diagnostic",
            Header = new TextBlock("Diagnostic"),
            TypedValueAccessor = McpToolGridRow.Accessor.Diagnostic,
            Width = GridLength.Star(2),
            Sortable = true,
        });
    }

    private static Visual CreateSection(string title, Visual content)
        => new Group(title)
            .Style(GroupStyle.Rounded)
            .Content(content)
            .Padding(new Thickness(1, 0, 1, 0))
            .HorizontalAlignment(Align.Stretch);

    private static Visual BuildEmptyState()
        => new TextBlock("Select an MCP server or configuration row on the left to inspect normalized details, policy, diagnostics, and tool-discovery placeholders.")
        {
            Wrap = true,
        };

    private static string BuildListHint(McpManagementServerSnapshot entry)
    {
        var parts = new List<string> { FormatStateText(entry.State) };
        if (entry.Transport is not null)
        {
            parts.Add(entry.Transport.Value == McpManagementTransport.Stdio ? "stdio" : "http");
        }

        if (entry.SourceScope is not null)
        {
            parts.Add(entry.SourceScope.Value == McpManagementScope.Project ? "project" : "global");
        }

        if (entry.OverridesGlobal)
        {
            parts.Add("overrides global");
        }

        parts.Add($"tools {entry.ExposedToolCount}/{entry.TotalToolCount}");
        return string.Join(" · ", parts);
    }

    private static (string Tone, string Icon) GetStatusToneAndIcon(McpManagementServerState state)
        => state switch
        {
            McpManagementServerState.Configured => ("success", $"{NerdFont.MdCheckCircleOutline}"),
            McpManagementServerState.Disabled => ("muted", $"{NerdFont.MdPauseCircleOutline}"),
            McpManagementServerState.MissingConfig => ("muted", $"{NerdFont.MdFileQuestionOutline}"),
            McpManagementServerState.InvalidConfig => ("error", $"{NerdFont.MdCloseCircleOutline}"),
            McpManagementServerState.Shadowed => ("warning", $"{NerdFont.MdFileCompare}"),
            _ => ("primary", $"{NerdFont.MdLan}"),
        };

    private static string FormatStateText(McpManagementServerState state)
        => state switch
        {
            McpManagementServerState.Configured => "configured",
            McpManagementServerState.Disabled => "disabled",
            McpManagementServerState.MissingConfig => "missing config",
            McpManagementServerState.InvalidConfig => "invalid config",
            McpManagementServerState.Shadowed => "shadowed",
            _ => state.ToString(),
        };

    private static string? FormatDictionary(IReadOnlyDictionary<string, string> values)
        => values.Count == 0
            ? null
            : string.Join(", ", values.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => item.Key + "=" + item.Value));

    private static string FormatEditableDictionary(IReadOnlyDictionary<string, string> values)
        => values.Count == 0
            ? string.Empty
            : string.Join("; ", values.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => item.Key + "=" + item.Value));

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            builder.Append(label).Append(": ").AppendLine(AnsiMarkup.Escape(string.Join(", ", values)));
        }
    }

    private void SetSelectedServerIndex(int index)
    {
        var normalizedIndex = _servers.Count == 0 ? -1 : Math.Clamp(index, 0, _servers.Count - 1);
        _selectedServerIndex.Value = normalizedIndex;
    }

    private int FindServerIndex(string key)
    {
        for (var index = 0; index < _servers.Count; index++)
        {
            if (string.Equals(_servers[index].Entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private int IndexOfRow(McpServerRow row)
    {
        for (var index = 0; index < _servers.Count; index++)
        {
            if (ReferenceEquals(_servers[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    private McpServerRow? GetSelectedRow()
        => _selectedServerIndex.Value >= 0 && _selectedServerIndex.Value < _servers.Count
            ? _servers[_selectedServerIndex.Value]
            : null;

    private void Close()
    {
        CancelActiveTestServerOperation();
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private sealed class McpServerRow
    {
        public McpServerRow(McpManagementServerSnapshot entry, bool isDraft = false)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Entry = entry;
            IsDraft = isDraft;
            OriginalKey = entry.Key;
            OriginalScope = entry.SourceScope;
            Version = new State<int>(0);
            EnabledState = new State<bool>(entry.PolicyEnabled != false);
            KeyState = new State<string?>(entry.Key);
            ScopeState = new State<McpManagementScope>(entry.SourceScope ?? McpManagementScope.Project);
            TransportState = new State<McpManagementTransport>(entry.Transport ?? McpManagementTransport.Stdio);
            CommandState = new State<string?>(entry.Command ?? string.Empty);
            ArgsState = new State<string?>(string.Join("; ", entry.Args));
            CwdState = new State<string?>(entry.Cwd ?? string.Empty);
            EnvState = new State<string?>(FormatEditableDictionary(entry.Env));
            UrlState = new State<string?>(entry.Url ?? string.Empty);
            HeadersState = new State<string?>(FormatEditableDictionary(entry.Headers));
            HasRedactedEnv = ContainsRedactedPlaceholder(EnvState.Value ?? string.Empty);
            HasRedactedHeaders = ContainsRedactedPlaceholder(HeadersState.Value ?? string.Empty);
        }

        public McpManagementServerSnapshot Entry { get; private set; }

        public State<int> Version { get; }

        public void UpdateEntry(McpManagementServerSnapshot entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Entry = entry;
            Version.Value++;
        }

        public bool IsDraft { get; }

        public string OriginalKey { get; }

        public McpManagementScope? OriginalScope { get; }

        public State<bool> EnabledState { get; }

        public State<string?> KeyState { get; }

        public State<McpManagementScope> ScopeState { get; }

        public State<McpManagementTransport> TransportState { get; }

        public State<string?> CommandState { get; }

        public State<string?> ArgsState { get; }

        public State<string?> CwdState { get; }

        public State<string?> EnvState { get; }

        public State<string?> UrlState { get; }

        public State<string?> HeadersState { get; }

        public bool HasRedactedEnv { get; }

        public bool HasRedactedHeaders { get; }
    }

    private sealed record ScopeOption(string Label, McpManagementScope Scope)
    {
        public static ScopeOption Project { get; } = new("Project", McpManagementScope.Project);

        public static ScopeOption Global { get; } = new("Global", McpManagementScope.Global);

        public override string ToString() => Label;
    }

    private sealed record TransportOption(string Label, McpManagementTransport Transport)
    {
        public static TransportOption Stdio { get; } = new("Stdio", McpManagementTransport.Stdio);

        public static TransportOption Http { get; } = new("HTTP/SSE", McpManagementTransport.Http);

        public override string ToString() => Label;
    }

    private sealed class DialogLifetimeHost : Padder
    {
        private readonly Action _onDetached;

        public DialogLifetimeHost(Visual content, Action onDetached)
            : base(content)
        {
            _onDetached = onDetached ?? throw new ArgumentNullException(nameof(onDetached));
        }

        protected override void OnDetachedFromApp(TerminalApp app)
        {
            _onDetached();
            base.OnDetachedFromApp(app);
        }
    }
}

internal sealed partial class McpToolGridRow
{
    private bool _suppressEnabledChanged;

    public McpToolGridRow(McpManagementToolSnapshot tool)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Enabled = tool.Enabled;
        NameTitle = string.IsNullOrWhiteSpace(tool.Title)
            ? tool.Name
            : string.Concat(tool.Name, " (", tool.Title, ")");
        Alias = tool.Alias;
        Availability = tool.Availability;
        Description = tool.Description ?? string.Empty;
        Diagnostic = tool.Diagnostic ?? string.Empty;
    }

    public event EventHandler<bool>? EnabledChanged;

    public McpManagementToolSnapshot Tool { get; }

    [Bindable]
    public partial bool Enabled { get; set; }

    [Bindable]
    public partial string NameTitle { get; set; }

    [Bindable]
    public partial string Alias { get; set; }

    [Bindable]
    public partial string Availability { get; set; }

    [Bindable]
    public partial string Description { get; set; }

    [Bindable]
    public partial string Diagnostic { get; set; }

    public void RestoreEnabled(bool enabled)
    {
        _suppressEnabledChanged = true;
        try
        {
            Enabled = enabled;
        }
        finally
        {
            _suppressEnabledChanged = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabledChanged)
        {
            EnabledChanged?.Invoke(this, value);
        }
    }
}
