namespace CodeAlta.Catalog;

/// <summary>
/// Runtime application settings that can be toggled during a session.
/// Changes take effect immediately without restarting the app.
/// </summary>
public static class AppSettings
{
    /// <summary>
    /// Gets or sets whether shell command and file change permission requests
    /// are automatically approved. When true, no approval dialog is shown.
    /// </summary>
    public static bool AutoApprove { get; set; }
}
