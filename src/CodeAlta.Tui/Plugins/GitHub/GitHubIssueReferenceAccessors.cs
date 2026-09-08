using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugin.GitHub;

internal static class GitHubIssueReferenceAccessors
{
    public static readonly BindingAccessor<string> Id = new("Id", static item => ((GitHubIssueReferenceItem)item).Id, null);

    public static readonly BindingAccessor<string> Title = new("Title", static item => ((GitHubIssueReferenceItem)item).Title, null);

    public static readonly BindingAccessor<string> State = new("State", static item => ((GitHubIssueReferenceItem)item).StateText, null);

    public static readonly BindingAccessor<string> Updated = new("Updated", static item => ((GitHubIssueReferenceItem)item).UpdatedText, null);

    public static readonly BindingAccessor<string> Link = new("Link", static item => ((GitHubIssueReferenceItem)item).LinkText, null);
}
