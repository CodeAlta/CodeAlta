using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal static class FlowScrollExtensions
{
    internal static void ScrollToTailIfEnabled(this DocumentFlow flow, bool autoScroll)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!autoScroll)
        {
            return;
        }

        flow.ScrollToTailIfFollowing();
    }

    internal static void ScrollToTailIfFollowing(this DocumentFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        // We cannot rely on FollowTail for now. DocumentFlow does not always update it
        // correctly when the tail item is mutated dynamically, so we keep the existing
        // unconditional scroll workaround until XenoAtom.Terminal.UI is fixed upstream.
        {
            flow.ScrollToTail();
        }
    }
}