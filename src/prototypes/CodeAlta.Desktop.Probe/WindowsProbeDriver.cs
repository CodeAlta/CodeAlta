using System.Runtime.InteropServices;
using System.Text;

namespace CodeAlta.Desktop.Probe;

// Test-only process-scoped native automation. Never imported by the production application.
internal static class WindowsProbeDriver
{
    public static void RequestUserClose(nint window)
    {
        if (!PostMessage(window, 0x0010, 0, 0)) throw new InvalidOperationException("Could not request close of owned probe window.");
    }

    [DllImport("kernel32.dll")]
    public static extern nint GetConsoleWindow();

    public static async Task ClickDialogAsync(nint owner, string title, int button, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint match = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (pid != Environment.ProcessId || GetWindow(window, 4) != owner || !IsWindowVisible(window)) return true;
                var text = new StringBuilder(256);
                GetWindowText(window, text, text.Capacity);
                if (text.ToString() != title) return true;
                var className = new StringBuilder(64);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "#32770") return true;
                match = window;
                return false;
            }, 0);
            if (match != 0)
            {
                // TDM_CLICK_BUTTON: activates the real standard TaskDialog button.
                if (!PostMessage(match, 0x0400 + 102, button, 0)) throw new InvalidOperationException("Could not click owned TaskDialog.");
                return;
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
