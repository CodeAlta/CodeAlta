---
title: Bookmarks
---

# Bookmarks

A bookmark marks something you want to find again: one message of a session, or a whole session. CodeAlta Desktop shows the bookmarks of a session beside its timeline, and lists all of them in one tab.

## Bookmark a message

Hover one of your messages or an answer of the assistant, and click the **bookmark** button among its actions. The button stays on the message, filled, while it is bookmarked; click it again to remove the bookmark.

Only complete messages can be bookmarked: a message still being written, a tool call or a status line has no bookmark button.

## Bookmark a session

Choose **Bookmark session** in the menu of a session in the Explorer (its `...` button, or a right click), or run `/bookmark` for the session in front. The row of the session then shows a small bookmark. **Remove session bookmark**, in the same menu, removes it.

## Go to a bookmark

A session with bookmarked messages has a rail at the right edge of its timeline, with one mark per bookmark, in the order of the conversation:

- hover a mark to see whose message it is, its name and its date;
- click a mark to go to its message, also far back in a long session;
- the marks of the messages on screen are highlighted.

With the keyboard, `Shift+F3` goes to the previous bookmark and `Shift+F4` to the next one.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-bookmark-rail.webp" alt="A session of CodeAlta Desktop with a filled bookmark on a message and the rail of marks at the right edge of its timeline" loading="lazy">
  <figcaption class="small text-secondary mt-2">A bookmarked message keeps a filled bookmark. The rail at the right edge has one mark for each bookmark of the session.</figcaption>
</figure>

## The Bookmarks tab

Open it with **Bookmarks** in the title bar (in its **…** menu when the window is narrow), `Ctrl+G Ctrl+F` or `/bookmarks`. It is restored with your other tabs.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-bookmarks.webp" alt="The Bookmarks tab of CodeAlta Desktop: the bookmarks of three projects, with the messages and the session that were marked" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Bookmarks tab lists the messages and the sessions you marked, project by project.</figcaption>
</figure>

The tab lists the bookmarks of every project, by project then by session, the most recent first; chats have a group of their own. The messages of a session are in conversation order, with their date and the start of their text, and a bookmarked session is marked **Session bookmarked**. Click a bookmark to open its session at that message.

{.table}
| To | Do this |
|---|---|
| Find a bookmark | Type in **Search bookmarks**: the names, the messages, the sessions and the projects are searched. |
| See only messages or sessions | Choose **Messages** or **Sessions** beside the search. |
| Name a bookmark | Click the pencil of its row, type a name and press `Enter`. An empty name gives it back the beginning of its message. |
| Remove a bookmark | Click the bookmark button of its row. |

- If you rewind a session past a bookmarked message, the bookmark says its message is no longer in the session.
- Deleting a session removes its bookmarks.
- A session with a bookmark, on itself or on one of its messages, is never removed by **Delete inactive sessions** (see [Storage](workspace/settings.md#storage)).

## Bookmarks and agents

Agents read bookmarks with `alta bookmark`, so you can refer to them in a prompt:

```text
Look at my bookmarks of this project and summarize the decisions they hold.
```

An agent adds a bookmark only when you ask, such as "bookmark the answer of the second turn", and it appears in the tab at once.
