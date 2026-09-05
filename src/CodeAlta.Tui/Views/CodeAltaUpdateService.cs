using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal enum CodeAltaUpdateCheckStatus
{
    NotStarted,
    Checking,
    Latest,
    UpdateAvailable,
    PackageNotFound,
    Failed,
}

internal sealed record CodeAltaUpdateCheckSnapshot(
    CodeAltaUpdateCheckStatus Status,
    string PackageId,
    string CurrentVersionText,
    string? LatestVersionText,
    bool LatestVersionIsPrerelease,
    bool IncludePrerelease,
    string? ErrorMessage)
{
    public bool HasNewerVersion => Status == CodeAltaUpdateCheckStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(LatestVersionText);

    public bool IsCompleted => Status is CodeAltaUpdateCheckStatus.Latest or CodeAltaUpdateCheckStatus.UpdateAvailable or CodeAltaUpdateCheckStatus.PackageNotFound or CodeAltaUpdateCheckStatus.Failed;

    public string UpdateCommand => LatestVersionIsPrerelease
        ? $"dotnet tool update -g {PackageId} --prerelease"
        : $"dotnet tool update -g {PackageId}";

    public static CodeAltaUpdateCheckSnapshot CreateNotStarted()
        => new(
            CodeAltaUpdateCheckStatus.NotStarted,
            CodeAltaUpdateChecker.PackageId,
            CodeAltaApplicationInfo.GetVersionInfo().PackageVersion,
            LatestVersionText: null,
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);
}

internal sealed class CodeAltaUpdateService : IDisposable
{
    private readonly object _gate = new();
    private readonly State<int> _uiRefreshVersion = new(0);
    private CodeAltaUpdateCheckSnapshot _snapshot = CodeAltaUpdateCheckSnapshot.CreateNotStarted();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _checkTask;
    private int _generation;
    private int _observedGeneration;

    public State<int> UiRefreshVersion => _uiRefreshVersion;

    public CodeAltaUpdateCheckSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        if (_checkTask is not null)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        SetSnapshot(Snapshot with { Status = CodeAltaUpdateCheckStatus.Checking });
        _checkTask = Task.Run(() => CheckAsync(_cancellationTokenSource.Token));
    }

    public void SynchronizeUiState()
    {
        var generation = Volatile.Read(ref _generation);
        if (generation == _observedGeneration)
        {
            return;
        }

        _observedGeneration = generation;
        _uiRefreshVersion.Value++;
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await CodeAltaUpdateChecker.CheckCurrentAssemblyAsync(cancellationToken: cancellationToken);
            SetSnapshot(new CodeAltaUpdateCheckSnapshot(
                result.HasNewerVersion
                    ? CodeAltaUpdateCheckStatus.UpdateAvailable
                    : result.PackageFound
                        ? CodeAltaUpdateCheckStatus.Latest
                        : CodeAltaUpdateCheckStatus.PackageNotFound,
                result.PackageId,
                result.CurrentVersionText,
                result.LatestVersionText,
                result.LatestVersion?.IsPrerelease ?? false,
                result.IncludePrerelease,
                ErrorMessage: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetSnapshot(Snapshot with
            {
                Status = CodeAltaUpdateCheckStatus.Failed,
                ErrorMessage = ex.Message,
            });
        }
    }

    private void SetSnapshot(CodeAltaUpdateCheckSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _generation++;
        }
    }
}
