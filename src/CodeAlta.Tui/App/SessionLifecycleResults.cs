namespace CodeAlta.Tui.App;

internal enum OpenSessionResult
{
    Opened,
    NotFound,
    AlreadyOpen,
}

internal enum SelectionChangeResult
{
    Changed,
    Unchanged,
}

internal enum TabCloseResult
{
    Closed,
    NotOpen,
}
