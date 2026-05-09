namespace CodeAlta.LiveTool;

internal static class AltaServiceProvider
{
    public static T? Get<T>(this IServiceProvider services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(T)) as T;
    }

    public static bool TryGetRequired<T>(
        this AltaCommandContext context,
        string serviceName,
        out T service,
        int exitCode = AltaExitCodes.ServiceUnavailable)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        service = context.Services.Get<T>()!;
        if (service is not null)
        {
            return true;
        }

        AltaJsonlWriter.WriteError(
            context.Stderr,
            context.CorrelationId,
            "service.unavailable",
            exitCode,
            $"Required in-process service '{serviceName}' is unavailable.");
        return false;
    }
}
