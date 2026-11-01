import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot, WorkspaceClaudeCodeConversation } from "#neoastra";
import { categoryLimit, fieldsScore, flatResults, groupPreview, matchRanges, nextCategory, readQuery, searchArchivedSessions, searchCategories, searchProjects, searchSessions, shownGroups, searchClaudeCodeConversations,
  wordScore, type SearchGroup, type SearchResult } from "./searchResults";

const project = (id: string, name: string, path = `C:\\code\\${name}`, archived = false): WorkspaceProject => ({ id, name, path, archived });
const session = (id: string, projectId: string | null, title: string, updatedAt: string, parentSessionId: string | null = null): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id, title, fullTitle: title,
  fullTitleTruncated: false, parentSessionId, scopeKind: projectId === null ? "global" : "project", projectId, lineageIssue: null, workspacePath: null, providerKey: null, updatedAt });
const snapshot = (projects: WorkspaceProject[], sessions: WorkspaceSession[] = []): WorkspaceSnapshot => ({
  configured: true, projects, sessions, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false });
const day = (value: number) => `2026-10-${String(value).padStart(2, "0")}T10:00:00Z`;

test("what is typed is read as words without their case, and a text that starts with a slash looks for a command", () => {
  assert.deepEqual(readQuery("  Fix   the Parser "), { commands: false, words: ["fix", "the", "parser"] });
  assert.deepEqual(readQuery("/New_Session now"), { commands: true, words: ["new_session", "now"] });
  assert.deepEqual(readQuery("/"), { commands: true, words: [] });
  assert.deepEqual(readQuery("   "), { commands: false, words: [] });
  assert.deepEqual(readQuery("a/b"), { commands: false, words: ["a/b"] });
});

test("a word counts most as the whole text, then at its start, then at the start of a word, then anywhere", () => {
  assert.equal(wordScore("Markdig", "markdig"), 0);
  assert.equal(wordScore("Markdig", "mark"), 1000);
  assert.equal(wordScore("lunet/markdig", "mark"), 2006);
  assert.equal(wordScore("C:\\code\\markdig", "mark"), 2008);
  assert.equal(wordScore("bookmark", "mark"), 3004);
  assert.equal(wordScore("Markdig", "yaml"), null);
  // Every word has to be found in one of the texts; each counts where it is found best, with the bias of that text.
  assert.equal(fieldsScore(["mark", "tests"], [["Add tests", 0], ["markdig", 60]]), 2004 + 1060);
  assert.equal(fieldsScore(["mark", "yaml"], [["Add tests", 0], ["markdig", 60]]), null);
  assert.equal(fieldsScore([], [["anything", 0]]), 0);
});

test("the parts that a query found are marked in order, and those that touch are one", () => {
  assert.deepEqual(matchRanges("Add intraword emphasis tests", ["emph", "add"]), [[0, 3], [14, 18]]);
  assert.deepEqual(matchRanges("aaaa", ["aa", "aaa"]), [[0, 4]]);
  assert.deepEqual(matchRanges("Parser parser", ["parser"]), [[0, 6], [7, 13]]);
  assert.deepEqual(matchRanges("Parser", ["yaml"]), []);
  assert.deepEqual(matchRanges("Parser", []), []);
});

test("a project is found by its name, then by its folder, then by the rest of its path", () => {
  const value = snapshot([project("a", "scriban", "C:\\code\\lunet\\scriban"), project("b", "lunet", "C:\\code\\lunet\\lunet"), project("c", "Templates", "C:\\code\\lunet_template"),
    project("d", "Old lunet", "C:\\old\\site", true), project("e", "SharpYaml", "C:\\code\\SharpYaml")],
    [session("s1", "a", "One", day(1)), session("s2", "a", "Two", day(2)), session("s3", "b", "Three", day(3)), session("s4", null, "Chat", day(4))]);
  const found = searchProjects(value, ["lunet"]);
  // The name, the folder, the path; an archived project comes after the others, however well it matches.
  assert.deepEqual(found.map(result => result.project.id), ["b", "c", "a", "d"]);
  assert.deepEqual(found.map(result => result.sessions), [1, 0, 2, 0]);
  assert.deepEqual(searchProjects(value, ["code", "yaml"]).map(result => result.project.id), ["e"]);
  assert.deepEqual(searchProjects(value, ["missing"]), []);
  // Nothing typed lists them all by name, the favorite ones first.
  assert.deepEqual(searchProjects(value, [], ["e", "gone"]).map(result => [result.project.id, result.favorite]), [["e", true], ["b", false], ["a", false], ["c", false], ["d", false]]);
  // As good a match, a favorite comes first.
  const twins = snapshot([project("x", "Alpha"), project("y", "Alpha")]);
  assert.deepEqual(searchProjects(twins, ["alpha"], ["y"]).map(result => result.project.id), ["y", "x"]);
  assert.deepEqual(found[0].key, "project:b");
});

test("a session is found by its title, its project or its id, the last one updated first", () => {
  const value = snapshot([project("m", "markdig"), project("y", "SharpYaml")], [
    session("0199aaaa", "m", "Add intraword **emphasis** tests", day(3)), session("0199bbbb", "m", "Coordinate two Markdig tasks", day(5)),
    session("0199cccc", "m", "Document the emphasis parser", day(4), "0199bbbb"), session("0199dddd", "y", "Quote indicator scalars", day(6)),
    session("0199eeee", null, "Emphasis in a chat", day(1)), session("0199ffff", "gone", "Orphan emphasis", day(2))]);
  const ids = (words: string[], scope?: string | null) => searchSessions(value, words, scope).map(result => result.session.id);
  // Nothing typed: every session, the last one updated first.
  assert.deepEqual(ids([]), ["0199dddd", "0199bbbb", "0199cccc", "0199aaaa", "0199ffff", "0199eeee"]);
  // The start of a title comes before the start of a word of it, which comes before the time.
  assert.deepEqual(ids(["emphasis"]), ["0199eeee", "0199cccc", "0199aaaa", "0199ffff"]);
  // The name of the project finds its sessions, after the titles that hold the word.
  assert.deepEqual(ids(["markdig"]), ["0199bbbb", "0199cccc", "0199aaaa"]);
  assert.deepEqual(ids(["sharp", "quote"]), ["0199dddd"]);
  assert.deepEqual(ids(["0199cc"]), ["0199cccc"]);
  // The search keeps to one project, or to the chats.
  assert.deepEqual(ids(["emphasis"], "m"), ["0199cccc", "0199aaaa"]);
  assert.deepEqual(ids([], null), ["0199eeee"]);
  assert.deepEqual(ids(["emphasis"], "y"), []);
  const found = searchSessions(value, ["emphasis"]);
  assert.deepEqual(found.map(result => [result.project?.name ?? null, result.child]), [[null, false], ["markdig", true], ["markdig", false], [null, false]]);
  assert.equal(found[0].key, "session:0199eeee");
});

const results = (group: SearchGroup, count: number, score: number): SearchResult[] => Array.from({ length: count }, (_, index) =>
  ({ kind: "command", key: `${group}:${index}`, score: score + index, name: `${group}${index}`, label: "", description: "", group: "", keys: [], enabled: true, run: () => {} }));

test("every category shows the first results of each group, the group with the best match first", () => {
  const found = { conversations: [], docs: [], tools: [], settings: [], sessions: results("sessions", 30, 3000), projects: results("projects", 2, 1000), files: [], commands: results("commands", 9, 3005), skills: [], archived: [] };
  const all = shownGroups("all", found);
  assert.deepEqual(all.map(group => [group.group, group.results.length, group.total]), [["projects", 2, 2], ["sessions", groupPreview.sessions, 30], ["commands", groupPreview.commands, 9]]);
  assert.equal(flatResults(all).length, 2 + groupPreview.sessions + groupPreview.commands);
  assert.equal(flatResults(all)[0].key, "projects:0");
  // As good a match, the groups keep their order: the sessions, the projects, the files, the commands.
  const even = shownGroups("all", { conversations: [], docs: [], tools: [], settings: [], sessions: results("sessions", 1, 0), projects: results("projects", 1, 0), files: results("files", 1, 900), commands: results("commands", 1, 0), skills: [], archived: [] });
  assert.deepEqual(even.map(group => group.group), ["sessions", "projects", "files", "commands"]);
  assert.deepEqual(shownGroups("all", { conversations: [], docs: [], tools: [], settings: [], sessions: [], projects: [], files: [], commands: [], skills: [], archived: [] }), []);
  // The archived sessions come last, however well they match.
  const archived = shownGroups("all", { conversations: [], docs: [], tools: [], settings: [], sessions: results("sessions", 1, 3000), projects: [], files: [], commands: [], skills: [], archived: results("archived", 9, 0) });
  assert.deepEqual(archived.map(group => [group.group, group.results.length]), [["sessions", 1], ["archived", groupPreview.archived]]);
});

test("one category shows its group whole, up to a limit, and a query for a command shows the commands only", () => {
  const found = { conversations: [], docs: [], tools: [], settings: [], sessions: results("sessions", categoryLimit + 20, 0), projects: results("projects", 2, 0), files: [], commands: results("commands", 9, 0), skills: [], archived: [] };
  const sessions = shownGroups("sessions", found);
  assert.deepEqual(sessions.map(group => [group.group, group.results.length, group.total]), [["sessions", categoryLimit, categoryLimit + 20]]);
  assert.deepEqual(shownGroups("files", found), []);
  assert.deepEqual(shownGroups("all", found, true).map(group => [group.group, group.results.length]), [["commands", 9]]);
  assert.deepEqual(shownGroups("projects", found, true).map(group => group.group), ["commands"]);
});

test("after a slash the skills are shown with the commands: the first commands then every skill, or the better match first", () => {
  const found = { conversations: [], docs: [], tools: [], settings: [], sessions: [], projects: [], files: [], commands: results("commands", 9, 1000), skills: results("skills", 8, 0), archived: [] };
  assert.deepEqual(shownGroups("all", found, true, true).map(group => [group.group, group.results.length, group.total]),
    [["commands", groupPreview.commands, 9], ["skills", 8, 8]], "with nothing after the slash, the first commands come first");
  assert.deepEqual(shownGroups("all", found, true).map(group => [group.group, group.results.length]), [["skills", 8], ["commands", 9]], "the better match first");
  assert.deepEqual(shownGroups("skills", found, true).map(group => group.group), ["skills"]);
  assert.deepEqual(shownGroups("commands", found, true).map(group => group.group), ["commands"]);
});

test("Tab goes through the categories and around", () => {
  assert.deepEqual(searchCategories, ["all", "sessions", "conversations", "projects", "files", "commands", "settings", "skills", "docs", "tools", "archived"]);
  assert.equal(nextCategory("all", 1), "sessions");
  assert.equal(nextCategory("commands", 1), "settings");
  assert.equal(nextCategory("settings", 1), "skills");
  assert.equal(nextCategory("tools", 1), "archived");
  assert.equal(nextCategory("archived", 1), "all");
  assert.equal(nextCategory("all", -1), "archived");
  assert.equal(nextCategory("files", -1), "projects");
  // Without archived sessions, skills or an index, their categories are not offered.
  const offered = searchCategories.filter(value => !["archived", "skills", "conversations", "docs", "tools", "settings"].includes(value));
  assert.equal(nextCategory("commands", 1, offered), "all");
  assert.equal(nextCategory("all", -1, offered), "commands");
});

test("the archived sessions are found only by a query, and choosing one restores it", () => {
  const app = project("p", "App");
  const kept = { ...session("a", "p", "Parser rewrite", day(3)), skills: [], archived: true, archivedAt: day(4) };
  const chat = { ...session("b", null, "Parser notes", day(2)), skills: [], archived: true, archivedAt: day(5) };
  const catalog = snapshot([app]);
  assert.deepEqual(searchArchivedSessions(catalog, [kept, chat], []), [], "Nothing is shown before something is typed.");
  const found = searchArchivedSessions(catalog, [kept, chat], ["parser"]);
  assert.deepEqual(found.map(result => [result.key, result.archived, result.project?.id ?? null]), [["archived:a", true, "p"], ["archived:b", true, null]]);
  assert.deepEqual(searchArchivedSessions(catalog, [kept, chat], ["parser"], null).map(result => result.session.id), ["b"], "A search kept to the chats keeps to them.");
  assert.deepEqual(searchArchivedSessions(catalog, [], ["parser"]), []);
});

test("the Claude Code sessions started elsewhere are found once something is typed, of the projects listed or of none, by title, project and folder", () => {
  const listed = snapshot([project("p1", "Stride"), project("p2", "Old", "C:\\code\\Old", true)]);
  const conversation = (id: string, title: string, projectId: string | null, lastChangedAt: string, tag: string | null = null, startDirectory: string | null = null)
    : WorkspaceClaudeCodeConversation => ({ cliSessionId: id, title, lastChangedAt, origin: "vscode", projectId, tag, startDirectory });
  const all = [
    conversation("c1", "Fix the shader compiler", "p1", day(3)),
    conversation("c2", "Shader cache", null, day(5), "C:\\Users\\me", "C:\\Users\\me"),
    conversation("c3", "Shader of another space", "p9", day(6)),
    conversation("c4", "Shader of an archived project", "p2", day(7)),
    conversation("c5", "Review", "p1", day(8), "stride-gal", "C:\\dev2\\stride-gal"),
  ];
  const ids = (results: readonly { conversation: WorkspaceClaudeCodeConversation }[]) => results.map(result => result.conversation.cliSessionId);
  assert.deepEqual(searchClaudeCodeConversations(all, "claude-code", listed, []), [], "nothing typed: the recent sessions of CodeAlta only");
  const shader = searchClaudeCodeConversations(all, "claude-code", listed, ["shader"]);
  assert.deepEqual(ids(shader), ["c2", "c1"], "the projects of the list, or none; the one changed last first as good a match");
  assert.deepEqual([shader[0]!.project, shader[1]!.project?.id, shader[0]!.providerId, shader[0]!.key], [null, "p1", "claude-code", "claude-code:c2"]);
  assert.deepEqual(ids(searchClaudeCodeConversations(all, null, listed, ["stride"])), ["c5", "c1"], "by the name of the project");
  assert.deepEqual(ids(searchClaudeCodeConversations(all, null, listed, ["gal"])), ["c5"], "by where it started");
  assert.deepEqual(ids(searchClaudeCodeConversations(all, null, listed, ["users"])), ["c2"]);
  assert.deepEqual(ids(searchClaudeCodeConversations(all, null, listed, ["shader"], "p1")), ["c1"], "the search of one project");
  assert.deepEqual(searchClaudeCodeConversations(all, null, listed, ["shader"], null), [], "the search of the chats");
});
