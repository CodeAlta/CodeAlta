using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// The budgets, allowances, holds and notifications of the REST API (doc/fleet/observability.md, budgets and alerts).
public sealed partial class FleetServerClient
{
    /// <summary>Lists the budgets of this controller device's person, as signed, with their consumption.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The budgets.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetListDto> ListBudgetsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/budgets", null, FleetBudgetsApiJsonContext.Default.FleetBudgetListDto, cancellationToken);

    /// <summary>Stores a budget, or a newer version of it, that this controller device signed.</summary>
    /// <param name="signed">The signed budget.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The budget as held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="ArgumentException">The budget does not read.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetDto> PutBudgetAsync(FleetSignedBudget signed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (!FleetBudgetDocuments.TryRead(signed.Payload, out var definition, out var reason)) throw new ArgumentException($"The budget does not hold: {reason}.", nameof(signed));
        return SendAsync(HttpMethod.Put, "api/v1/budgets/" + Uri.EscapeDataString(definition.BudgetId),
            Body(new PutFleetBudgetRequest(signed), FleetBudgetsApiJsonContext.Default.PutFleetBudgetRequest), FleetBudgetsApiJsonContext.Default.FleetBudgetDto, cancellationToken);
    }

    /// <summary>Lists the budgets of the owner of this machine, with what it holds of each.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The budgets.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetMachineBudgetListDto> ListMachineBudgetsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/machine-budgets", null, FleetBudgetsApiJsonContext.Default.FleetMachineBudgetListDto, cancellationToken);

    /// <summary>Asks more of an enforced budget for this machine.</summary>
    /// <param name="budgetId">The budget.</param>
    /// <param name="request">The version of the budget, what the machine spent and what it needs.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the machine holds now.</returns>
    /// <exception cref="ArgumentException"><paramref name="budgetId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetAllowanceDto> AskAllowanceAsync(string budgetId, FleetAllowanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/budgets/{Uri.EscapeDataString(budgetId)}/allowance", Body(request, FleetBudgetsApiJsonContext.Default.FleetAllowanceRequest),
            FleetBudgetsApiJsonContext.Default.FleetAllowanceDto, cancellationToken);
    }

    /// <summary>Says that a run of this machine waits for a reached budget.</summary>
    /// <param name="budgetId">The budget.</param>
    /// <param name="request">The run, its nonce, the version of the budget and the period.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hold.</returns>
    /// <exception cref="ArgumentException"><paramref name="budgetId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetHoldDto> RequestBudgetHoldAsync(string budgetId, RequestFleetBudgetHoldRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/budgets/{Uri.EscapeDataString(budgetId)}/holds", Body(request, FleetBudgetsApiJsonContext.Default.RequestFleetBudgetHoldRequest),
            FleetBudgetsApiJsonContext.Default.FleetBudgetHoldDto, cancellationToken);
    }

    /// <summary>Reads a hold.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hold, with its Continue anyway once given.</returns>
    /// <exception cref="ArgumentException"><paramref name="holdId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetHoldDto> GetBudgetHoldAsync(string holdId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(holdId);
        return SendAsync(HttpMethod.Get, "api/v1/budget-holds/" + Uri.EscapeDataString(holdId), null, FleetBudgetsApiJsonContext.Default.FleetBudgetHoldDto, cancellationToken);
    }

    /// <summary>Lists the holds of the budgets of this device's person, the newest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The holds.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetHoldListDto> ListBudgetHoldsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/budget-holds", null, FleetBudgetsApiJsonContext.Default.FleetBudgetHoldListDto, cancellationToken);

    /// <summary>Continues a run that waits, with the Continue anyway this controller device signed.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="signed">The signed Continue anyway.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hold, continued.</returns>
    /// <exception cref="ArgumentException"><paramref name="holdId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetHoldDto> ContinueBudgetHoldAsync(string holdId, FleetSignedBudgetContinue signed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(holdId);
        ArgumentNullException.ThrowIfNull(signed);
        return SendAsync(HttpMethod.Post, $"api/v1/budget-holds/{Uri.EscapeDataString(holdId)}/continue",
            Body(new ContinueFleetBudgetHoldRequest(signed), FleetBudgetsApiJsonContext.Default.ContinueFleetBudgetHoldRequest), FleetBudgetsApiJsonContext.Default.FleetBudgetHoldDto,
            cancellationToken);
    }

    /// <summary>Stops a run that waits: it does not start.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hold, stopped.</returns>
    /// <exception cref="ArgumentException"><paramref name="holdId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetHoldDto> StopBudgetHoldAsync(string holdId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(holdId);
        return SendAsync(HttpMethod.Post, $"api/v1/budget-holds/{Uri.EscapeDataString(holdId)}/stop", null, FleetBudgetsApiJsonContext.Default.FleetBudgetHoldDto, cancellationToken);
    }

    /// <summary>Lists the notifications of this controller device's person, the newest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The notifications.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetNotificationListDto> ListNotificationsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/notifications", null, FleetBudgetsApiJsonContext.Default.FleetNotificationListDto, cancellationToken);

    /// <summary>Marks a notification read on every device of the person.</summary>
    /// <param name="notificationId">The notification.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The notifications of the person.</returns>
    /// <exception cref="ArgumentException"><paramref name="notificationId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetNotificationListDto> ReadNotificationAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(notificationId);
        return SendAsync(HttpMethod.Post, $"api/v1/notifications/{Uri.EscapeDataString(notificationId)}/read", null, FleetBudgetsApiJsonContext.Default.FleetNotificationListDto,
            cancellationToken);
    }
}
