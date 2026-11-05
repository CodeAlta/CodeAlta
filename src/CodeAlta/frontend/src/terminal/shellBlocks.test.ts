import assert from "node:assert/strict";
import test from "node:test";
import { commandSeparator, isShellLanguage, joinCommandLines, shellCommandOf, terminalPaste } from "./shellBlocks";

test("the blocks of a shell are those of the shells and of their transcripts, whatever the case of the language", () => {
  for (const language of ["bash", "sh", "zsh", "shell", "console", "PowerShell", "pwsh", "ps1", "cmd"]) assert.ok(isShellLanguage(language), language);
  for (const language of ["", null, undefined, "csharp", "json", "text", "python"]) assert.ok(!isShellLanguage(language), String(language));
});

test("a block pastes its commands as they are typed: without its last line ending, and without the prompt of each line", () => {
  assert.equal(shellCommandOf("bash", "npm install\nnpm test\n"), "npm install\nnpm test", "a script is pasted whole");
  assert.equal(shellCommandOf("bash", "$ cd src\n$ dotnet build\n"), "cd src\ndotnet build", "a prompt on every line is taken off");
  assert.equal(shellCommandOf("bash", "$ cd src\ndotnet build"), "$ cd src\ndotnet build", "a script that only starts with one stays as it is");
  assert.equal(shellCommandOf("bash", "# build\n$ dotnet build\n"), "# build\ndotnet build", "its comments do not count");
  assert.equal(shellCommandOf("powershell", "Get-ChildItem\r\nGet-Process\r\n"), "Get-ChildItem\nGet-Process", "the lines end as a terminal reads them");
});

test("a transcript pastes the commands after its prompts, and leaves what they printed", () => {
  const transcript = "$ git status\nOn branch main\nnothing to commit\n# then the date\nPS C:\\code> Get-Date\nThursday\n";
  assert.equal(shellCommandOf("console", transcript), "git status\n# then the date\nGet-Date", "beside $ prompts, a # line is a comment");
  assert.equal(shellCommandOf("console", "# apt install tree\nReading package lists... Done\n"), "apt install tree", "alone, # is the prompt of root");
  assert.equal(shellCommandOf("console", "$ docker run \\\n    --rm hello-world\nHello from Docker!"), "docker run \\\n    --rm hello-world",
    "a command that goes on to the next line keeps it");
  assert.equal(shellCommandOf("console", "ls -la\n"), "ls -la", "a transcript without a prompt is its commands");
});

test("for a shell that would run each line, the commands go on one line when that is safe", () => {
  assert.equal(commandSeparator("pwsh"), "; ");
  assert.equal(commandSeparator("cmd"), " & ");
  assert.equal(joinCommandLines("cd src\n\n# build first\ndotnet build -c Release\ndotnet test", "; "), "cd src; dotnet build -c Release; dotnet test",
    "blank lines and comments are left out");
  assert.equal(joinCommandLines("docker run \\\n  --rm hello-world\nGet-Process |\n  Select-Object -First 3", "; "),
    "docker run --rm hello-world; Get-Process | Select-Object -First 3", "a command that goes on keeps its line");
  assert.equal(joinCommandLines("rem setup\ncd src\ndir", " & "), "cd src & dir");
  assert.equal(joinCommandLines("set \"DIR=C:\\scratch\"\nrmdir /s /q \"%DIR%\"", " & "), null,
    "the Command Prompt would expand %DIR% before the set of the same line");
  assert.equal(joinCommandLines("$dir = 'out'\nRemove-Item $dir", "; "), "$dir = 'out'; Remove-Item $dir", "PowerShell runs them in order");
  assert.equal(joinCommandLines("cat <<EOF\nhello\nEOF", "; "), null, "a here-document is not joined");
  assert.equal(joinCommandLines("if ($x) {\n  Write-Host x\n}", "; "), null, "nor a block over several lines");
  assert.equal(joinCommandLines("echo \"a\nb\"", "; "), null, "nor a string over several lines");
  assert.equal(joinCommandLines("npm test # all of them\nnpm run lint", "; "), null, "nor a comment after a command, which would hide the next one");
  assert.equal(joinCommandLines("Write-Output one;# note\nWrite-Output two", "; "), null, "a comment right after a semicolon too");
  assert.equal(joinCommandLines("echo a & rem note\necho b", " & "), null, "nor a remark of the Command Prompt after a command");
  assert.equal(joinCommandLines("# only a comment", "; "), null);
});

test("what is pasted never runs: control characters are refused, and lines go on one line where the shell would run each", () => {
  assert.deepEqual(terminalPaste("npm test\u000f", true, "pwsh"), { refused: "control" }, "Ctrl+O accepts the line in PSReadLine");
  assert.deepEqual(terminalPaste("echo a\u001b[201~\nrm -rf x", true, "bash"), { refused: "control" }, "ESC would end a bracketed paste");
  assert.deepEqual(terminalPaste("cd src\nmake", true, "bash"), { text: "cd src\nmake" });
  assert.deepEqual(terminalPaste("cd src\nmake", false, "pwsh"), { text: "cd src; make" });
  assert.deepEqual(terminalPaste("echo\ta", false, "pwsh"), { text: "echo a" }, "a tab would complete");
  assert.deepEqual(terminalPaste("cat <<EOF\nx\nEOF", false, "pwsh"), { refused: "lines" });
});
