using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class TerminalScreenTests
{
    private const string Esc = "\u001b";

    private static TerminalScreen Screen(int columns = 20, int rows = 5, int scrollback = 100, bool console = false, bool reflow = true)
        => new(columns, rows, scrollback, console, reflow);

    private static string Shown(TerminalScreen screen) => string.Join("|", screen.ScreenRows());

    private static string History(TerminalScreen screen, int maximum = 1000) => string.Join("|", screen.Lines(maximum));

    [TestMethod]
    public void Text_IsWrittenWhereTheCursorIs_AndControlCharactersMoveIt()
    {
        var screen = Screen();
        screen.Write("hello\r\nworld");
        Assert.AreEqual("hello|world|||", Shown(screen));
        Assert.AreEqual((1, 5), screen.Cursor);
        // Backspace and carriage return move without erasing; a tab goes to the next multiple of eight.
        screen.Write("\b\bLD\rW\tx");
        Assert.AreEqual("hello|WorLD   x|||", Shown(screen));
        Assert.AreEqual((1, 9), screen.Cursor);
        // A line feed alone keeps the column.
        screen.Write("\ny");
        Assert.AreEqual("hello|WorLD   x|         y||", Shown(screen));
    }

    [TestMethod]
    public void ALineTooLongForARow_GoesOnInTheNextRow_AndIsOneLine()
    {
        var screen = Screen(columns: 10);
        screen.Write("0123456789abcdef\r\nnext");
        Assert.AreEqual("0123456789|abcdef|next||", Shown(screen));
        Assert.AreEqual("0123456789abcdef|next", History(screen));
        // A row filled to its last column wraps only when another character comes.
        var full = Screen(columns: 10);
        full.Write("0123456789");
        Assert.AreEqual((0, 9), full.Cursor);
        full.Write("\r\nab");
        Assert.AreEqual("0123456789|ab", History(full));
        // Without wrapping the last column is written over.
        var cut = Screen(columns: 10);
        cut.Write($"{Esc}[?7l0123456789abc");
        Assert.AreEqual("012345678c||||", Shown(cut));
    }

    [TestMethod]
    public void RowsThatLeaveTheScreen_AreKept_UpToTheLimit()
    {
        var screen = Screen(rows: 3, scrollback: 4);
        for (var line = 1; line <= 9; line++) screen.Write($"line {line}\r\n");
        Assert.AreEqual("line 8|line 9|", Shown(screen));
        Assert.AreEqual(4, screen.KeptRows);
        Assert.AreEqual("line 4|line 5|line 6|line 7|line 8|line 9|", History(screen));
        Assert.AreEqual("line 9|", History(screen, 2));
        // Erasing what scrolled off drops the kept rows; the screen stays.
        screen.Write($"{Esc}[3J");
        Assert.AreEqual(0, screen.KeptRows);
        Assert.AreEqual("line 8|line 9|", History(screen));
        // A terminal that keeps nothing still shows its screen.
        var none = Screen(rows: 2, scrollback: 0);
        none.Write("a\r\nb\r\nc");
        Assert.AreEqual("b|c", History(none));
    }

    [TestMethod]
    public void TheCursor_IsPlacedAndMoved_AndPartsOfTheScreenAreErased()
    {
        var screen = Screen(columns: 10, rows: 4);
        screen.Write("aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc\r\ndddddddddd");
        screen.Write($"{Esc}[2;4H{Esc}[K");
        Assert.AreEqual("aaaaaaaaaa|bbb|cccccccccc|dddddddddd", Shown(screen));
        screen.Write($"{Esc}[1K");
        Assert.AreEqual("aaaaaaaaaa||cccccccccc|dddddddddd", Shown(screen));
        screen.Write($"{Esc}[3;6H{Esc}[1J");
        Assert.AreEqual("||      cccc|dddddddddd", Shown(screen));
        screen.Write($"{Esc}[J");
        Assert.AreEqual("|||", Shown(screen));
        // Relative moves stop at the edges; a column or a row alone can be set.
        screen.Write($"{Esc}[99B{Esc}[99Cx{Esc}[99A{Esc}[99Dy{Esc}[3d{Esc}[5Gz{Esc}[2Ew{Esc}[1Fv");
        Assert.AreEqual("y||v   z|w        x", Shown(screen));
        // The whole screen is erased without moving the cursor.
        screen.Write($"{Esc}[2;2H{Esc}[2Jq");
        Assert.AreEqual("| q||", Shown(screen));
        Assert.AreEqual((1, 2), screen.Cursor);
    }

    [TestMethod]
    public void Characters_AreInsertedDeletedErasedAndRepeated_InARow()
    {
        var screen = Screen(columns: 10, rows: 2);
        screen.Write($"abcdefghij{Esc}[1;3H{Esc}[2@");
        Assert.AreEqual("ab  cdefgh|", Shown(screen));
        screen.Write($"{Esc}[3P");
        Assert.AreEqual("abdefgh|", Shown(screen));
        screen.Write($"{Esc}[2X");
        Assert.AreEqual("ab  fgh|", Shown(screen));
        screen.Write($"{Esc}[4hXY{Esc}[4l");
        Assert.AreEqual("abXY  fgh|", Shown(screen));
        screen.Write($"\r\n-{Esc}[4b");
        Assert.AreEqual("abXY  fgh|-----", Shown(screen));
    }

    [TestMethod]
    public void TheRowsBetweenMargins_Scroll_AndRowsAreInsertedAndDeleted()
    {
        var screen = Screen(columns: 6, rows: 5);
        screen.Write("top\r\n1\r\n2\r\n3\r\nbottom");
        // Rows 2 to 4 scroll alone: the first one is thrown away, not kept.
        screen.Write($"{Esc}[2;4r{Esc}[4;1H\n4");
        Assert.AreEqual("top|2|3|4|bottom", Shown(screen));
        Assert.AreEqual(0, screen.KeptRows);
        // Going up from the first row of the margins scrolls them down.
        screen.Write($"{Esc}[2;1H{Esc}M0");
        Assert.AreEqual("top|0|2|3|bottom", Shown(screen));
        screen.Write($"{Esc}[3;1H{Esc}[1L");
        Assert.AreEqual("top|0||2|bottom", Shown(screen));
        screen.Write($"{Esc}[2;1H{Esc}[2M");
        Assert.AreEqual("top|2|||bottom", Shown(screen));
        screen.Write($"{Esc}[1S{Esc}[2T");
        Assert.AreEqual("top||||bottom", Shown(screen));
        // Positions are counted from the top margin when the program asks for it.
        screen.Write($"{Esc}[?6h{Esc}[2;2Hx{Esc}[?6l{Esc}[r{Esc}[5;6Hy");
        Assert.AreEqual("top|| x||bottoy", Shown(screen));
    }

    [TestMethod]
    public void WideAndJoinedCharacters_TakeTheColumnsATerminalGivesThem()
    {
        var screen = Screen(columns: 6, rows: 3);
        screen.Write("日本語x");
        Assert.AreEqual("日本語|x|", Shown(screen));
        Assert.AreEqual("日本語x", History(screen));
        // An accent joins the letter before it; an emoji outside the basic plane is one wide character.
        var joined = Screen(columns: 10, rows: 2);
        joined.Write("e\u0301a😀b");
        Assert.AreEqual((0, 5), joined.Cursor);
        Assert.AreEqual("e\u0301a😀b|", Shown(joined));
        // Writing over half of a wide character removes the other half.
        joined.Write($"{Esc}[1;4HX");
        Assert.AreEqual("e\u0301a Xb|", Shown(joined));
        // The two halves of a surrogate pair can come in two writes.
        var split = Screen();
        split.Write("a\ud83d");
        split.Write("\ude00b");
        Assert.AreEqual("a😀b", History(split));
    }

    [TestMethod]
    public void LineDrawing_IsSelectedByTheProgram()
    {
        var screen = Screen();
        screen.Write($"{Esc}(0lqqk{Esc}(B lqqk");
        Assert.AreEqual("┌──┐ lqqk", History(screen));
        screen.Write($"\r\n{Esc})0\u000ex\u000fx");
        Assert.AreEqual("┌──┐ lqqk|│x", History(screen));
    }

    [TestMethod]
    public void AFullScreenProgram_HasItsOwnScreen_AndTheOtherOneComesBack()
    {
        var screen = Screen(rows: 3);
        screen.Write("one\r\ntwo");
        screen.Write($"{Esc}[?1049h{Esc}[Heditor");
        Assert.IsTrue(screen.Alternate);
        Assert.AreEqual("editor||", Shown(screen));
        // What the terminal showed before is still what its lines are.
        Assert.AreEqual("one|two", History(screen));
        for (var line = 0; line < 9; line++) screen.Write("\r\nscrolls");
        Assert.AreEqual(0, screen.KeptRows);
        screen.Write($"{Esc}[?1049l!");
        Assert.IsFalse(screen.Alternate);
        Assert.AreEqual("one|two!|", Shown(screen));
        // The older way to ask for it works the same.
        screen.Write($"{Esc}[?47hx{Esc}[?47l");
        Assert.AreEqual("one|two!|", Shown(screen));
    }

    [TestMethod]
    public void ASequence_CanComeInPieces_AndUnknownOnesShowNothing()
    {
        var screen = Screen();
        foreach (var piece in new[] { "a", Esc, "[", "3", "1", "m", "b", $"{Esc}]0;ti", "tle\u0007c", $"{Esc}Pignored{Esc}", "\\d", $"{Esc}[?2026h{Esc}[>4;2me" })
        {
            screen.Write(piece);
        }
        Assert.AreEqual("abcde", History(screen));
        Assert.AreEqual("title", screen.Title);
        // A sequence cut by another one is dropped, and a canceled one too.
        screen.Write($"{Esc}[12{Esc}[2Dxy{Esc}[5\u0018z");
        Assert.AreEqual("abcxyz", History(screen));
    }

    [TestMethod]
    public void TitleAndFolder_AreWhatTheProgramSays()
    {
        var signals = new List<TerminalSignal>();
        var screen = Screen();
        screen.Signal = signals.Add;
        screen.Write($"{Esc}]2;build{Esc}\\{Esc}]0;build\u0007");
        Assert.AreEqual("build", screen.Title);
        screen.Write($"{Esc}]7;file://host/home/me/my%20project\u0007");
        Assert.AreEqual("/home/me/my project", screen.Folder);
        screen.Write($"{Esc}]7;file://HOST/C:/Users/me\u0007");
        Assert.AreEqual("C:/Users/me", screen.Folder);
        screen.Write($"{Esc}]9;9;\"C:\\code\\app\"{Esc}\\");
        Assert.AreEqual(@"C:\code\app", screen.Folder);
        screen.Write($"{Esc}]1337;CurrentDir=/srv/app\u0007{Esc}]633;P;Cwd=C:\\x5cwork\\x3bdir\u0007");
        Assert.AreEqual(@"C:\work;dir", screen.Folder);
        screen.Write("\u0007");
        CollectionAssert.AreEqual(new[] { TerminalSignal.Title, TerminalSignal.Folder, TerminalSignal.Folder, TerminalSignal.Folder, TerminalSignal.Folder, TerminalSignal.Folder, TerminalSignal.Bell }, signals);
        // A progress report is not a folder, and an empty title is none.
        screen.Write($"{Esc}]9;4;1;50\u0007{Esc}]0;\u0007");
        Assert.AreEqual(@"C:\work;dir", screen.Folder);
        Assert.IsNull(screen.Title);
    }

    [TestMethod]
    public void Questions_AreAnswered()
    {
        var answers = new List<string>();
        var screen = Screen();
        screen.Reply = answers.Add;
        screen.Write($"ab\r\ncd{Esc}[6n{Esc}[5n{Esc}[c{Esc}[>c{Esc}[?2004h{Esc}[?2004$p{Esc}[?1004$p{Esc}[?6n");
        CollectionAssert.AreEqual(new[] { $"{Esc}[2;3R", $"{Esc}[0n", $"{Esc}[?1;2c", $"{Esc}[>0;276;0c", $"{Esc}[?2004;1$y", $"{Esc}[?1004;2$y", $"{Esc}[?2;3R" }, answers);
    }

    [TestMethod]
    public void TheModesOfTheTerminal_AreThoseAReplayStartsFrom()
    {
        var screen = Screen();
        Assert.AreEqual(string.Empty, screen.Modes());
        screen.Write($"{Esc}[?1h{Esc}[?2004h{Esc}[?1000;1006h{Esc}[?25l{Esc}={Esc}[?2026h{Esc}[?47h");
        Assert.AreEqual($"{Esc}[?1h{Esc}[?1000h{Esc}[?1006h{Esc}[?1049h{Esc}[?2004h{Esc}[?25l{Esc}=", screen.Modes());
        screen.Write($"{Esc}[?1l{Esc}[?2004l{Esc}[?1000;1006l{Esc}[?25h{Esc}>{Esc}[?1047l{Esc}[?7l");
        Assert.AreEqual($"{Esc}[?7l", screen.Modes());
        screen.Write($"{Esc}c");
        Assert.AreEqual(string.Empty, screen.Modes());
    }

    [TestMethod]
    public void AShellWithItsScript_ReportsItsCommands_AndTheirOutputIsRead()
    {
        var signals = new List<TerminalSignal>();
        var screen = Screen(columns: 30, rows: 4, scrollback: 50);
        screen.Signal = signals.Add;
        Assert.IsFalse(screen.Integrated);
        // A prompt, a line typed, its command line and its output, then the next prompt with the exit code.
        screen.Write($"{Esc}]633;A\u0007PS> {Esc}]633;B\u0007echo one two\r\n{Esc}]633;E;echo one\\x3b two\u0007{Esc}]633;C\u0007");
        Assert.IsTrue(screen.Integrated);
        Assert.IsTrue(screen.Busy);
        screen.Write("one\r\ntwo\r\n");
        Assert.AreEqual("one|two", screen.Output(screen.Commands[0], out var truncated).Replace("\n", "|"));
        Assert.IsFalse(truncated);
        screen.Write($"{Esc}]633;D;3\u0007{Esc}]633;A\u0007PS> {Esc}]633;B\u0007");
        Assert.IsFalse(screen.Busy);
        var command = screen.Commands.Single();
        Assert.AreEqual((1L, "echo one; two", 3, true), (command.Number, command.CommandLine, command.ExitCode, command.Finished));
        Assert.AreEqual("one|two", screen.Output(command, out _).Replace("\n", "|"));
        CollectionAssert.AreEqual(new[] { TerminalSignal.Prompt, TerminalSignal.CommandStarted, TerminalSignal.CommandFinished, TerminalSignal.Prompt }, signals);

        // The older marks have no command line: it is what was typed between the prompt and the output.
        screen.Write($"ls -la\r\n{Esc}]133;C\u0007");
        for (var line = 1; line <= 8; line++) screen.Write($"file {line}\r\n");
        screen.Write($"{Esc}]133;D;0\u0007{Esc}]133;A\u0007$ {Esc}]133;B\u0007");
        var listing = screen.Commands[1];
        Assert.AreEqual(("ls -la", 0), (listing.CommandLine, listing.ExitCode));
        // Its output left the screen, and is read from the kept rows.
        Assert.AreEqual("file 1|file 2|file 3|file 4|file 5|file 6|file 7|file 8", screen.Output(listing, out truncated).Replace("\n", "|"));
        Assert.IsFalse(truncated);
        Assert.AreEqual("one|two", screen.Output(command, out _).Replace("\n", "|"));

        // An empty line runs nothing; a command without an exit code ends at the next prompt.
        screen.Write($"\r\n{Esc}]133;A\u0007$ {Esc}]133;B\u0007sleep\r\n{Esc}]133;C\u0007{Esc}]133;A\u0007$ ");
        Assert.AreEqual(3, screen.Commands.Count);
        Assert.AreEqual(("sleep", (int?)null, true), (screen.Commands[2].CommandLine, screen.Commands[2].ExitCode, screen.Commands[2].Finished));
    }

    [TestMethod]
    public void TheOutputOfACommand_IsCutWhereTheTerminalKeepsNoMore()
    {
        var screen = Screen(columns: 20, rows: 3, scrollback: 4);
        screen.Write($"{Esc}]133;A\u0007$ {Esc}]133;B\u0007seq\r\n{Esc}]133;C\u0007");
        for (var line = 1; line <= 12; line++) screen.Write($"{line}\r\n");
        // Still running: everything the terminal has of it, with its start gone.
        Assert.AreEqual("7|8|9|10|11|12", screen.Output(screen.Commands[0], out var truncated).Replace("\n", "|"));
        Assert.IsTrue(truncated);
        screen.Write($"{Esc}]133;D;0\u0007{Esc}]133;A\u0007$ ");
        Assert.AreEqual("7|8|9|10|11|12", screen.Output(screen.Commands[0], out truncated).Replace("\n", "|"));
        Assert.IsTrue(truncated);
    }

    [TestMethod]
    public void ANarrowerScreen_CutsEachLineAgain_AndAWiderOneJoinsThem()
    {
        var screen = Screen(columns: 10, rows: 4);
        screen.Write("0123456789abcdef\r\nshort\r\n$ ");
        Assert.AreEqual("0123456789|abcdef|short|$", Shown(screen));
        screen.Resize(6, 4);
        // Five rows for four: the first leaves the screen, and the cursor stays after the prompt.
        Assert.AreEqual("6789ab|cdef|short|$", Shown(screen));
        Assert.AreEqual(1, screen.KeptRows);
        Assert.AreEqual((3, 2), screen.Cursor);
        Assert.AreEqual("0123456789abcdef|short|$", History(screen));
        screen.Resize(20, 4);
        // The row that left comes back with the line it belongs to.
        Assert.AreEqual("0123456789abcdef|short|$|", Shown(screen));
        Assert.AreEqual(0, screen.KeptRows);
        Assert.AreEqual((2, 2), screen.Cursor);
        screen.Write("typed");
        Assert.AreEqual("0123456789abcdef|short|$ typed", History(screen));
        // A wide character is not cut in two.
        var wide = Screen(columns: 6, rows: 3);
        wide.Write("ab日本語");
        wide.Resize(5, 3);
        Assert.AreEqual("ab日|本語|", Shown(wide));
        Assert.AreEqual("ab日本語", History(wide));
    }

    [TestMethod]
    public void AScreenThatDoesNotCutLinesAgain_IsOnlyCutOrExtended()
    {
        var screen = Screen(columns: 10, rows: 3, reflow: false);
        screen.Write("0123456789abc\r\nxy");
        screen.Resize(6, 3);
        Assert.AreEqual("012345|abc|xy", Shown(screen));
        screen.Resize(12, 3);
        Assert.AreEqual("012345|abc|xy", Shown(screen));
    }

    [TestMethod]
    public void AShorterScreen_LosesItsEmptyRowsFirst_AndATallerOneGetsRowsBack()
    {
        var screen = Screen(columns: 10, rows: 4);
        screen.Write("1\r\n2\r\n3");
        screen.Resize(10, 3);
        // The row under the cursor was empty: nothing leaves the screen.
        Assert.AreEqual("1|2|3", Shown(screen));
        Assert.AreEqual(0, screen.KeptRows);
        screen.Resize(10, 2);
        Assert.AreEqual("2|3", Shown(screen));
        Assert.AreEqual(1, screen.KeptRows);
        Assert.AreEqual((1, 1), screen.Cursor);
        screen.Resize(10, 4);
        // One row comes back from those that left, then there is nothing more to bring back.
        Assert.AreEqual("1|2|3|", Shown(screen));
        Assert.AreEqual(0, screen.KeptRows);
        Assert.AreEqual((2, 1), screen.Cursor);

        // Behind a pseudoconsole a row that left the screen stays where it is: empty rows are added below.
        var console = Screen(columns: 10, rows: 2, console: true);
        console.Write("1\r\n2\r\n3");
        console.Resize(10, 4);
        Assert.AreEqual("2|3||", Shown(console));
        Assert.AreEqual(1, console.KeptRows);
    }

    [TestMethod]
    public void TheMarksOfACommand_FollowItsTextWhenTheScreenChangesSize()
    {
        var screen = Screen(columns: 20, rows: 6);
        screen.Write($"{Esc}]133;A\u0007$ {Esc}]133;B\u0007print\r\n{Esc}]133;C\u0007a long line of output\r\nend\r\n{Esc}]133;D;0\u0007{Esc}]133;A\u0007$ ");
        screen.Resize(8, 6);
        Assert.AreEqual("a long line of output|end", screen.Output(screen.Commands[0], out _).Replace("\n", "|"));
        screen.Resize(40, 3);
        Assert.AreEqual("a long line of output|end", screen.Output(screen.Commands[0], out _).Replace("\n", "|"));
        Assert.AreEqual("print", screen.Commands[0].CommandLine);
    }

    [TestMethod]
    public void AReset_ClearsTheScreen_AndTheSmallestScreenStillWorks()
    {
        var screen = Screen();
        screen.Write($"text{Esc}[?1049halt{Esc}cnew");
        Assert.IsFalse(screen.Alternate);
        Assert.AreEqual("new||||", Shown(screen));
        var small = new TerminalScreen(1, 1);
        Assert.AreEqual((2, 2), (small.Columns, small.Rows));
        small.Write("abcdef日");
        small.Resize(0, 0);
        Assert.AreEqual("abcdef日", string.Concat(small.Lines(100)));
    }

    [TestMethod]
    public void WhatAShellWrote_IsReadBackAsItWasEscaped()
    {
        Assert.AreEqual("plain", TerminalIntegration.Unescape("plain"));
        Assert.AreEqual("a;b\\c\nd", TerminalIntegration.Unescape("a\\x3bb\\\\c\\x0ad"));
        Assert.AreEqual("end\\", TerminalIntegration.Unescape("end\\"));
        Assert.AreEqual("\\q", TerminalIntegration.Unescape("\\q"));
        Assert.IsNull(TerminalIntegration.FolderOfUrl("http://host/path"));
        Assert.IsNull(TerminalIntegration.FolderOfUrl("file://host"));
        Assert.AreEqual("/a b", TerminalIntegration.FolderOfUrl("file:///a%20b"));
    }

    [TestMethod]
    public void TheWidthOfACharacter_IsTheOneTerminalsUse()
    {
        Assert.AreEqual(1, TerminalWidth.Of('a'));
        Assert.AreEqual(1, TerminalWidth.Of('é'));
        Assert.AreEqual(0, TerminalWidth.Of(0x0301));
        Assert.AreEqual(0, TerminalWidth.Of(0x200D));
        Assert.AreEqual(0, TerminalWidth.Of(0xFE0F));
        Assert.AreEqual(2, TerminalWidth.Of('日'));
        Assert.AreEqual(2, TerminalWidth.Of(0xAC00));
        Assert.AreEqual(2, TerminalWidth.Of(0x1F600));
        Assert.AreEqual(1, TerminalWidth.Of(0x2500));
        // The icons of a Nerd Font are in a private area: one column each.
        Assert.AreEqual(1, TerminalWidth.Of(0xE0B0));
        Assert.AreEqual(1, TerminalWidth.Of(0xF0001));
    }
}
