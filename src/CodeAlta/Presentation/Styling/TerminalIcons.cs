// Replaces XenoAtom.Terminal.UI.NerdFont with safe Unicode characters
// that render correctly without a Nerd Font installed.
namespace CodeAlta.Presentation.Styling;

internal static class TerminalIcons
{
    // Actions
    public static readonly System.Text.Rune MdCheck = new('✓');
    public static readonly System.Text.Rune MdCheckCircleOutline = new('✓');
    public static readonly System.Text.Rune MdClose = new('✗');
    public static readonly System.Text.Rune MdCloseCircleOutline = new('✗');
    public static readonly System.Text.Rune MdDelete = new('✗');
    public static readonly System.Text.Rune MdDeleteOutline = new('✗');
    public static readonly System.Text.Rune MdDeleteSweepOutline = new('✗');
    public static readonly System.Text.Rune MdPlus = new('+');
    public static readonly System.Text.Rune MdContentSaveCheckOutline = new('✓');
    public static readonly System.Text.Rune MdRefresh = new('↻');
    public static readonly System.Text.Rune MdReload = new('↻');
    public static readonly System.Text.Rune MdUpdate = new('↻');
    public static readonly System.Text.Rune MdPauseCircleOutline = new('‖');
    public static readonly System.Text.Rune MdContentCopy = new('⊞');

    // Arrows / navigation
    public static readonly System.Text.Rune CodArrowRight = new('→');
    public static readonly System.Text.Rune MdArrowRightThinCircleOutline = new('→');
    public static readonly System.Text.Rune MdAccountArrowRight = new('→');
    public static readonly System.Text.Rune MdSend = new('→');
    public static readonly System.Text.Rune MdExitRun = new('→');

    // Folders
    public static readonly System.Text.Rune FaFolderTree = new('▶');
    public static readonly System.Text.Rune MdFolder = new('▶');
    public static readonly System.Text.Rune MdFolderOutline = new('▶');
    public static readonly System.Text.Rune MdFolderMultipleOutline = new('▸');

    // Files / documents
    public static readonly System.Text.Rune MdFileDocumentOutline = new('□');
    public static readonly System.Text.Rune MdFileDocumentEditOutline = new('□');
    public static readonly System.Text.Rune MdFileEditOutline = new('□');
    public static readonly System.Text.Rune MdNoteTextOutline = new('□');
    public static readonly System.Text.Rune MdFileCodeOutline = new('☰');

    // Code symbols
    public static readonly System.Text.Rune MdCodeBraces = new('{');
    public static readonly System.Text.Rune MdCodeBracesBox = new('{');
    public static readonly System.Text.Rune MdCodeJson = new('{');
    public static readonly System.Text.Rune MdXml = new('{');

    // Language icons
    public static readonly System.Text.Rune MdLanguageCpp = new('♯');
    public static readonly System.Text.Rune MdLanguageCsharp = new('♯');
    public static readonly System.Text.Rune MdLanguageCss3 = new('#');
    public static readonly System.Text.Rune MdLanguageGo = new('○');
    public static readonly System.Text.Rune MdLanguageHtml5 = new('#');
    public static readonly System.Text.Rune MdLanguageJava = new('♨');
    public static readonly System.Text.Rune MdLanguageJavascript = new('◉');
    public static readonly System.Text.Rune MdLanguageKotlin = new('●');
    public static readonly System.Text.Rune MdLanguageMarkdown = new('▼');
    public static readonly System.Text.Rune MdLanguagePython = new('◈');
    public static readonly System.Text.Rune MdLanguageRust = new('⚙');
    public static readonly System.Text.Rune MdLanguageTypescript = new('◇');

    // Status / alerts
    public static readonly System.Text.Rune CodInfo = new('ⓘ');
    public static readonly System.Text.Rune OctInfo = new('ⓘ');
    public static readonly System.Text.Rune MdInformationOutline = new('ⓘ');
    public static readonly System.Text.Rune CodWarning = new('⚠');
    public static readonly System.Text.Rune MdAlertCircleOutline = new('⚠');
    public static readonly System.Text.Rune MdAlertOutline = new('⚠');
    public static readonly System.Text.Rune MdHelpBox = new('?');

    // UI controls
    public static readonly System.Text.Rune CodEdit = new('✎');
    public static readonly System.Text.Rune MdRenameBox = new('✎');
    public static readonly System.Text.Rune MdSquareEditOutline = new('✎');
    public static readonly System.Text.Rune MdCircleSmall = new('●');
    public static readonly System.Text.Rune MdSquare = new('■');
    public static readonly System.Text.Rune MdCogOutline = new('⚙');
    public static readonly System.Text.Rune MdCogRefreshOutline = new('⚙');
    public static readonly System.Text.Rune MdTuneVariant = new('☰');
    public static readonly System.Text.Rune MdFormatListBulleted = new('☰');
    public static readonly System.Text.Rune MdTabPlus = new('+');
    public static readonly System.Text.Rune MdTextBoxSearchOutline = new('⌕');
    public static readonly System.Text.Rune MdTrashCanOutline = new('✗');

    // Communication
    public static readonly System.Text.Rune MdChat = new('☺');
    public static readonly System.Text.Rune MdChatProcessingOutline = new('☺');
    public static readonly System.Text.Rune MdMessageText = new('☰');
    public static readonly System.Text.Rune MdMessageTextOutline = new('☰');

    // Special / misc
    public static readonly System.Text.Rune MdPacMan = new('◐');
    public static readonly System.Text.Rune MdPackageVariantClosed = new('■');
    public static readonly System.Text.Rune MdProgressWrench = new('⚒');
    public static readonly System.Text.Rune MdSelectCompare = new('⇄');
    public static readonly System.Text.Rune MdServerNetwork = new('◉');
    public static readonly System.Text.Rune MdShieldPlusOutline = new('★');
    public static readonly System.Text.Rune MdSortAlphabeticalAscending = new('☰');
    public static readonly System.Text.Rune MdSortCalendarDescending = new('▼');
    public static readonly System.Text.Rune MdDatabase = new('◈');
    public static readonly System.Text.Rune MdDocker = new('▣');
    public static readonly System.Text.Rune MdTimerOutline = new('◐');
    public static readonly System.Text.Rune MdHomeOutline = new('⌂');
    public static readonly System.Text.Rune MdCat = new('☺');
    public static readonly System.Text.Rune MdRobot = new('☻');
    public static readonly System.Text.Rune MdPuzzleOutline = new('⊞');
    public static readonly System.Text.Rune MdPuzzleCheckOutline = new('⊞');
    public static readonly System.Text.Rune MdPuzzleRemoveOutline = new('⊞');
    public static readonly System.Text.Rune MdWeb = new('◎');
    public static readonly System.Text.Rune MdCloudSearchOutline = new('◎');
    public static readonly System.Text.Rune MdCubeOutline = new('◇');
    public static readonly System.Text.Rune MdBash = new('❖');
    public static readonly System.Text.Rune MdPowershell = new('❖');
    public static readonly System.Text.Rune FaGithub = new('◈');
    public static readonly System.Text.Rune MdAccount = new('●');
    public static readonly System.Text.Rune MdAccountCogOutline = new('⚙');

    // Cod icons
    public static readonly System.Text.Rune CodLock = new('■');
    public static readonly System.Text.Rune CodLightbulb = new('☼');
    public static readonly System.Text.Rune CodTools = new('⚒');
}
