using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugin.Git;

internal static class GitIssueReferenceAccessors
{
    public static readonly BindingAccessor<string> Id = new("Id", static item => ((GitIssueReferenceItem)item).Id, null);

    public static readonly BindingAccessor<string> Title = new("Title", static item => ((GitIssueReferenceItem)item).Title, null);

    public static readonly BindingAccessor<string> State = new("State", static item => ((GitIssueReferenceItem)item).StateText, null);

    public static readonly BindingAccessor<string> Updated = new("Updated", static item => ((GitIssueReferenceItem)item).UpdatedText, null);

    public static readonly BindingAccessor<string> Link = new("Link", static item => ((GitIssueReferenceItem)item).LinkText, null);
}
