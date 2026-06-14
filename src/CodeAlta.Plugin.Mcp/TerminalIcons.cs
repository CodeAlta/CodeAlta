// Replaces NerdFont with safe Unicode characters for terminals without Nerd Fonts.
// Keep in sync with CodeAlta.Presentation.Styling.TerminalIcons in the CodeAlta project.
namespace CodeAlta.Plugin.Mcp;

internal static class McpTerminalIcons
{
    public static readonly System.Text.Rune MdCheckCircleOutline = new('✓');
    public static readonly System.Text.Rune MdClose = new('✗');
    public static readonly System.Text.Rune MdCloseCircleOutline = new('✗');
    public static readonly System.Text.Rune MdContentSaveCheckOutline = new('✓');
    public static readonly System.Text.Rune MdPlus = new('+');
    public static readonly System.Text.Rune MdRefresh = new('↻');
    public static readonly System.Text.Rune MdTrashCanOutline = new('✗');
    public static readonly System.Text.Rune MdPauseCircleOutline = new('‖');
    public static readonly System.Text.Rune MdServerNetwork = new('◉');
    public static readonly System.Text.Rune MdCodeJson = new('{');
    public static readonly System.Text.Rune MdFileCogOutline = new('⚙');
    public static readonly System.Text.Rune MdFileQuestionOutline = new('?');
    public static readonly System.Text.Rune MdFileCompare = new('⇄');
    public static readonly System.Text.Rune MdLan = new('◎');
}
