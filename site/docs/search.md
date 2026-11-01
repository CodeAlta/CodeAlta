---
title: Search
---

# Search

CodeAlta Desktop keeps a search index of your work on your computer: the prompts and final answers of your sessions, their notes, the plans and tasks of your projects, this user guide, the skills, the tools and the plugins. The search window finds them as you type, and agents find them with `alta search`. Nothing is sent anywhere.

The index is on by default and light: a few megabytes, built in the background after start, and a query takes a few milliseconds.

## Find a conversation

Press `Ctrl+P` and type a few words. Besides sessions, projects, files, commands and settings, the search window has three groups from the index:

- **Conversations**: prompts and final answers, session notes, and project plans and tasks. A hit shows the session, **Prompt** or **Answer**, the date and the words found.
- **Docs**: the sections of this guide, plugin pages included.
- **Tools**: agent tools, `alta` commands, MCP servers and plugins.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-search-window.webp" alt="The search window with a question about the scroll position: the Conversations group finds the prompt and the answers of a session, a plan and the sections of the guide, and the Docs and Tools groups follow" loading="lazy">
  <figcaption class="small text-secondary mt-2">The search window finds the prompts and answers of your sessions, your plans, the guide and the tools. <strong>≈</strong> marks a hit found by its meaning only.</figcaption>
</figure>

`Tab` moves between groups, and `Enter` opens the selection:

| You open | What happens |
| --- | --- |
| A message | Its session opens at that message, as a [bookmark](bookmarks.md) does. |
| A note | Its session opens. |
| A plan or a task | Its file opens in the code editor. |
| A section of the guide | The Documentation tab opens at that section. |
| An MCP server or a plugin | Its Settings page opens. |
| A setting, or a page of Settings | Settings opens on its page, and the setting glows. |
| A tool | Its description is shown. |

**Search sessions…** in a project's menu searches that project's conversations.

- Only the prompt that starts a turn and the turn's final answer are indexed: messages between tool calls, steering, tool output and reasoning are left out. An agent reads them around a hit with `alta search show --full`.
- A query matches words and parts of identifiers: `fingerprint journal` finds `JournalFingerprint`, `read file` finds `read_file`. The last word matches the start of a word.

## Search by meaning

**Search by meaning** also finds what is about the same thing in other words: "where did we fix the scroll jump?" finds "the timeline jumped while scrolling", in another language too.

It needs a model that runs on your computer, so it is off until you turn it on. CodeAlta offers it once there is enough to search (ten sessions, or three days of work), at the foot of the search window when a sentence finds little, and in **Settings › Search**. **Not now** asks again in a month; **Don't ask again** never does.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-search-meaning.webp" alt="The Search by meaning dialog: what it finds, that the model runs on this computer, and what it takes: the download, the disk and the time of the first indexing" loading="lazy">
  <figcaption class="small text-secondary mt-2">The offer of search by meaning: what it takes is said before anything is downloaded.</figcaption>
</figure>

What it takes, said before anything is downloaded:

- **Download**: the model once, and the ONNX Runtime (about 10 MB) unless [speech to prompt](speech.md) already brought it.
- **Disk**: about 800 bytes per indexed chunk.
- **Time**: the first indexing runs in the background, newest work first. It pauses while you dictate; **Pause** stops it.

| Model | Size | Languages | Strong at |
| --- | --- | --- | --- |
| EmbeddingGemma 2 (Google, Apache-2.0) | 304 MB | 100+ | code and prose; the default |
| Granite 97M multilingual (IBM, Apache-2.0) | 117 MB | 200+ | speed: about four times faster |

- A notification follows the download and indexing; the progress stays in Settings › Search.
- Search works during indexing: what is not embedded yet is found by its words. A hit found by meaning only is marked **≈**.
- After start, search by meaning waits about half a minute before loading, unless a search needs it.
- Models come from [Hugging Face](https://huggingface.co/) at a pinned commit and the runtime from nuget.org; every file is checked against its SHA-256.
- The model runs in its own processes, never in the window. **Batches at once** (2 by default, up to half your cores) sets how many: each takes about 1 GB with EmbeddingGemma 2; two index about 1.6 times as fast as one. Idle processes end and give their memory back.

## The Search canvas

The **Search** canvas shows what the index holds, lets you try a query and see why each hit ranks where it does, and shows how search is used. Open it with **Open Search** in Settings › Search, **Search index** in the search window (`/search_index`), the **Search** card of the welcome page, or `alta canvas open search`. Each [space](spaces.md) has its own, showing what that space can see.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-search-canvas.webp" alt="The Overview page of the Search canvas: the items, the sessions, the share searchable by meaning and the size of the index, the turns indexed per day, the items by kind and the projects as a treemap" loading="lazy">
  <figcaption class="small text-secondary mt-2">Overview: what the index holds for the space.</figcaption>
</figure>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-search-explore.webp" alt="The Explore page of the Search canvas: a question in English, the switch Words, Meaning and Both, and the hits with their rank by words and by meaning, a conversation in French found first by its meaning" loading="lazy">
  <figcaption class="small text-secondary mt-2">Explore: a question in English finds a conversation in French by its meaning (<code>meaning #1</code>), which its words alone rank far lower.</figcaption>
</figure>

- **Overview**: items indexed, sessions and projects kept out, the share searchable by meaning, index size; turns indexed per day over the last year; items by kind and project; indexer progress. Click a kind or project to list its items in Explore.
- **Explore**: type a query, choose a kind and project, and compare **Words**, **Meaning** or **Both**. Each hit shows its rank and origin, such as `words #3 · meaning #1` or `≈ meaning #1`. `Up`/`Down` move, `Enter` opens. With no query, it lists kinds and their items.
- **Activity**: the last 90 days of searches, the share that found nothing, median times, tokens tool search saved, and who searched (you, agents, tool search). Search words are never kept.

The canvas reads the index only while its tab is shown.

## Settings

**Settings › Personalization › Search**:

| Setting | What it does |
| --- | --- |
| **Search index** | Off: nothing is indexed, the search window has no Conversations, Docs or Tools, and `alta search` answers `unavailable`. |
| **Conversations** | Off: sessions are left out; the guide, plans, skills and tools stay. |
| **Check now** | Reads every source again now. |
| **Search by meaning** | On or off, the model, download and indexing progress, **Pause**, and **Delete** for an unused model. Off keeps the model and vectors, so turning it on again is immediate. |
| **Batches at once** | How many batches are embedded in parallel: faster, more memory. |
| **Kept out of search** | The projects and spaces kept out, each with **Include in search**. |

The choices are kept in `[search]` of `~/.alta/config.toml`:

```toml
[search]
enabled = true          # the index
sessions = true         # the prompts and final answers of sessions
semantic = false        # search by meaning
model = "embeddinggemma-2"
parallelism = 2         # batches embedded at once, up to half the cores
excluded_projects = []
excluded_spaces = []
```

## Exclude from search

**Exclude from search** in a project's menu, and **Search this space** in a space's settings, keep them out: nothing is indexed, what was indexed is removed, and no result shows them to you or to agents. Their sessions can still search the guide, your skills and the tools. **Include in search** lets them back in at the next pass.

A repository can keep itself out for everyone with `[search] enabled = false` in its `.alta/config.toml`.

## What agents get

Agents of CodeAlta Desktop use the same index through `alta`:

```text
alta search "why did we change the journal fingerprint" --kind message --project .
alta search "post a message on slack" --kind tool,mcp,skill
alta search "worktree cleanup" --kind doc
alta search "review a pull request" --kind script
alta search "stop renaming my sessions" --kind setting
alta search show msg:<session>/<message> --context 2
alta search load mcp:slack/post_message
alta search status
```

- Each hit gives its `ref`, a snippet, how it was found (`words`, `meaning` or `both`), and `next`: the command that uses it, such as `alta skill activate …` or `alta documentation read …`.
- `--kind setting` finds the settings of CodeAlta by what they do; `next` reads one with `alta settings get`, and `alta settings open` shows it to you in Settings.
- `--kind script` finds the [scripts of the library](sessions/code-mode.md#scripts-of-the-library) by their name, their description and when to use them.
- An agent sees what its space and project can see, never what is excluded, and not its own session unless it asks with `--include-current`.
- MCP tools are found once CodeAlta has listed them; search starts no server. `alta search reindex --kind mcp` lists every enabled server once.
- `alta search load` gives the session the MCP tools it found, without the rest of their server, for the same turn and later. It is refused while you review the session's commands.

## Where the files are

| What | Where |
| --- | --- |
| The index | `search.sqlite3` in `~/.alta/data/` (`~/.alta/dev/data/` for the developer instance) |
| The models | `~/.alta/cache/search/models/` |
| The ONNX Runtime | `~/.alta/cache/runtime/sherpa-onnx/`, shared with speech to prompt |

The index is derived from your sessions and files: deleting it loses nothing, and the next start rebuilds it.
