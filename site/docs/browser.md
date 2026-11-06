---
title: Browser tools
---

# Browser tools

An agent can drive a real web browser: open a page, read it, click, fill a form, take a screenshot, read the console and the network requests. It works with Chrome, Edge and the other Chromium browsers, Firefox, and Safari 27 or later, with the tools of [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp), which agents already know. Nothing else needs to be installed: no Node.js, no driver.

The browser tools come with CodeAlta Desktop and CodeAlta CLI (`altacli`, where the browser runs without a window).

## Let an agent use a browser

Ask in your own words, in any session:

```text
Open http://localhost:5173, sign in with test@example.com and tell me what the dashboard shows.
```

```text
Check the page in Firefox and take a screenshot.
```

The agent turns the tools on for its session with `alta browser activate`, then uses them. The browser opens at its first call, in a window you see, and the screenshots appear in the timeline. The session sees only the tabs it opened, never the tabs of another session.

<figure class="alta-figure my-4" style="max-width: 48rem;">
  <img src="{{site.basepath}}/img/alta-desktop-browser-session.webp" alt="A CodeAlta session whose agent ran alta browser activate, navigate_page to codealta.io and take_screenshot: the screenshot in the timeline, and the Browser Edge chip in the composer" loading="lazy">
  <figcaption class="small text-secondary mt-2">An agent opened the website of CodeAlta in Edge and took a screenshot, shown in the timeline; the chip of the prompt names its browser.</figcaption>
</figure>

The browser tools and the [UI tools](ui-tools.md) have the same names: a session has one set or the other, and turning one on gives the other back.

## Open the browser of a session yourself

Each session has one browser of its own. In CodeAlta Desktop, choose **Open the browser of this session** in the menu of the session to open it yourself, without asking the agent. It opens on a start page that lists the servers running in the folder of the project and its HTML files, with a field for any address.

Browse wherever you like: the agent sees the tabs of this browser and knows which one is selected, so you can ask, for example (when the tabs changed, your next prompt shows what the agent is told of them):

```text
Look at this page: why is the menu cut off?
```

Once open, the **Browser** item under the prompt shows its browser and tabs and brings its window to the front, and **Close the browser tabs of this session** in the menu of the session closes it. A browser you opened stays open until you close it, and an agent of the session uses it rather than starting another one, unless it names another browser or profile.

## Annotate the pages of your project

Show the agent what you mean on the page itself. Ask in any session:

```text
Start the app and open it in the browser: I want to mark what should change.
```

```text
Open docs/mockups/home.html in the browser.
```

The agent starts what the app needs as usual, then opens it with `alta browser open`. A file of the project is served by CodeAlta. An address of this computer is a page of the project when the program that listens on its port runs in the folder of the project, such as a dev server the agent started there. On Windows, where CodeAlta cannot read the folder a program runs in, it is the program whose command line names a file or folder of the project, such as Vite or Next.js started from the project. In a browser the agent opened, other pages get no toolbar; in a browser you [opened yourself](#open-the-browser-of-a-session-yourself), every page has it, but CodeAlta looks for the source of its elements only on the pages of the project.

In Chrome and Edge, such a page shows a small **CodeAlta** toolbar at its bottom right:

1. Click **Annotate**. The element under the pointer is outlined, with its tag, role, text and size, and what you click or drag does not reach the page. The toolbar stays above the dialogs and the full-screen elements of the page, so their elements can be marked too.
2. Choose what the note is about:
   - **One element:** click it.
   - **Several elements:** `Shift`+click each one, then click the last one, press `Enter` or click **Note for**. Use this for elements side by side that no single element holds alone.
   - **An area:** drag a rectangle over it. The note gets a picture of the area and the elements wholly inside it.
3. Write what should change, then **Save** (`Ctrl+Enter`, `Cmd+Enter` on macOS). A numbered mark stays on the page. `Esc` ends the mode.
   - **Change** or **Question**: a question is answered, and changes no code. Its mark shows a **?**.
   - Paste or drop images into the note, such as a mockup of how it should look: up to 4, large ones are made smaller.
4. Send the annotations: click **Send** in the toolbar and add a message if you like, or write your prompt in CodeAlta, where the annotations wait above the prompt of the session.

Until they are sent, the number in the toolbar opens the list of the annotations of the session, from every page: **Edit** changes a note, **✕** removes an annotation, and pointing at one shows it on the page. Clicking the number on a mark does the same. In CodeAlta, **Discard** drops them all.

When the toolbar covers what you want to mark, drag it by **CodeAlta** to another place. Click **CodeAlta** to collapse or expand the toolbar, and double-click it to put the toolbar back at the bottom right. The sun or moon button at the end switches the toolbar between a dark and a light look, so it stays readable on any page. CodeAlta keeps the place and the look for every page; the page itself does not change.

The prompt carries each annotation: its elements, its page and your note, and where each element is written as far as the browser and a search tell: the file CodeAlta serves, the stylesheet of its rules, the script that created it, or the file where its text, id and classes are. No plugin of your build is needed. A note covers at most 12 elements, and the source is looked for of the first 6. The agent sees the elements, the area and your images with `view_annotation`. Once the session has the prompt, the marks leave the page.

The prompt is short by default. Check **Include page details** when you send from the toolbar to add the HTML of the elements, their computed styles, the elements around them and the layout; CodeAlta keeps the choice for every page, also for the prompts you write in CodeAlta. What looks like a secret, such as a token in an address or in an attribute, is left out (`[redacted]`), and addresses go without their query.

**Send** in the toolbar needs the session open in one window of CodeAlta: while a second window of CodeAlta is open, write your prompt in CodeAlta instead.

On a page that is not of your project, such as a site you like, annotate to ask about it: the prompt carries the computed styles of the elements, and the agent answers what the note asks (the font and its size, the colors, the spacing, how an element is built) and changes nothing unless the note asks for it.

## Draw on the page

When an element is not enough, draw what you mean. Click **Draw** in the CodeAlta toolbar: a second row shows the pen, the highlighter, the arrow, the rectangle, the ellipse, text, the eraser, five colors, **Undo**, **Clear** and **Save**. The page cannot be clicked while you draw.

1. Draw with the pointer. For text, choose **T**, click where it goes, type, then press `Enter`.
2. Click **Save** or press `Enter`, write the note (**Change** or **Question**, images as for an annotation), then save it with `Ctrl+Enter` (`Cmd+Enter` on macOS).
3. The drawing leaves the page and a numbered box marks where it was. It is sent like the annotations, with a picture of the page and your drawing, which the agent sees with `view_annotation`. The picture keeps the numbered marks of your other annotations, and the prompt names the elements your shapes are about: what a rectangle, an ellipse or a stroke surrounds, what an arrow points at, what a text is written on, with where each is written.

Keys: `P` pen, `H` highlighter, `A` arrow, `R` rectangle, `O` ellipse, `T` text, `E` eraser, `1` to `5` colors, `Ctrl+Z` undo, `Ctrl+Shift+Z` or `Ctrl+Y` redo, `Esc` steps back and leaves Draw. The picture covers what is on screen, at most 1200×900 pixels of the page.

## Which browser

The first of these that names a browser wins:

1. **You name one.** "Check it in Firefox" makes the agent ask for Firefox.
2. **The preferred browser** of **Settings**, **Extensions**, **Browser**.
3. **Automatic**, the default: the browser you use on this computer. CodeAlta reads when each browser last wrote the files it keeps while you browse, and which browser is the default one of the system. The default browser wins when it was used within a day of the last one.

Automatic reads file times only, never a history, a cookie or a page, and sends nothing anywhere. `alta browser list` shows the browsers found, their evidence and the choice, and the Browser page of Settings shows the same.

<figure class="alta-figure my-4" style="max-width: 48rem;">
  <img src="{{site.basepath}}/img/alta-desktop-browser-settings.webp" alt="Settings, Extensions, Browser: the preferred browser Automatic (Microsoft Edge), the Isolated profile of the sessions, Without a window, Scripts in pages, and the persistent profiles" loading="lazy">
  <figcaption class="small text-secondary mt-2"><strong>Settings › Extensions › Browser</strong>: the browser and the profile of the sessions, and the persistent profiles.</figcaption>
</figure>

## Profiles

{.table}
| Profile | What it is |
| --- | --- |
| Isolated (the default) | A new private profile for each session, deleted when its browser closes. Nothing of yours is reachable. |
| Persistent | A profile CodeAlta keeps for agents, under `~/.alta/browser/profiles`. Sign in once to the sites agents use, and sessions keep those sign-ins. |
| Your own browser | Your own browser, profile and sign-ins, once you agree. Chrome, Edge 144 or later, and Firefox. |

Safari always runs its automation in a private window of its own: it has the isolated profile only.

## Sign in once for agents

1. Open **Settings**, **Extensions**, **Browser**.
2. Under **Persistent profiles**, click **Open to sign in** beside a profile, or type a new name and click **Open to sign in**.
3. Sign in to the sites in the window that opens, then close it.
4. Choose that profile in **Profile of the sessions**, or ask an agent to use it (`alta browser activate --profile persistent:work`).

## Your own browser

The agent then acts with your sign-ins. CodeAlta asks you three times:

1. Turn on **Let sessions ask for your browser** in **Settings**, **Extensions**, **Browser**. It is off until you do.
2. When a session asks, its card asks you to let it use your browser, for that session.
3. For each new site the session opens there, its card asks again: **Always allow this site**, **Allow for this session** or **Deny**. A link to another site waits for your answer too.

No permission mode answers these questions for you. The sites you always allow and those you block are listed on the Browser page, where you can change them.

<figure class="alta-figure my-4" style="max-width: 48rem;">
  <img src="{{site.basepath}}/img/alta-desktop-browser-settings-own.webp" alt="The Browser page of Settings scrolled to Your own browser, with its switch off and the allowed and blocked sites, and to Browsers found, with Edge marked default and Firefox" loading="lazy">
  <figcaption class="small text-secondary mt-2">Your own browser, off until you turn it on, its sites, and the browsers found on this computer.</figcaption>
</figure>

- **Chrome and Edge** need remote debugging turned on once: open `chrome://inspect/#remote-debugging` (`edge://inspect/#remote-debugging`) and allow it. The browser then asks you to allow the connection. The agent's tabs open in a window of their own, and the agent sees only those.
- **Firefox** lets CodeAlta drive it only when CodeAlta starts it: close Firefox when the agent asks, and CodeAlta starts it again on your profile.

CodeAlta never closes your own browser: **Stop** closes the agent's tabs only.

> [!WARNING]
> A page can try to give an agent instructions. CodeAlta tells agents that what a page shows is data, never instructions, but no one has fully solved this. Allow your own browser only for sites you trust, and watch what the agent does.

## Safari

On a Mac with Safari 27 or later, Safari's own MCP server (`safaridriver --mcp`) is listed among the [MCP servers](plugins/mcp.md) as `safari`. An agent that asks for Safari activates it, and its tools have Apple's names (`navigate_to_url`, `get_page_content`…). When Safari refuses, turn on **Allow remote automation and external agents** in Safari's **Settings**, **Developer**.

## See and stop what the agent does

- The browser marks a window it drives: Chrome's automation bar, Firefox's robot icon, Safari's banner.
- In Chrome and Edge with a window, a violet pointer moves to where the agent clicks, points or drags, and the page glows with **CodeAlta agent is using this page** while it acts, until about 15 seconds after its last action. The page cannot reach them, and the snapshots and screenshots of the agent never show them.
- The status line of the session shows its browser, profile and tabs, such as **Edge · isolated · 2 tabs**. Click it to bring the browser to the front; **Close the browser tabs of this session** in the menu of the session closes them.
- The tabs close by themselves after 15 minutes without a call, and with CodeAlta; the next call opens them again. A browser you opened yourself, or one that shows the CodeAlta toolbar, stays open until you close it.

## Tools

{.table}
| Tool | What it does |
| --- | --- |
| `navigate_page`, `new_page`, `list_pages`, `select_page`, `close_page` | Open, list and close the session's tabs, go back, forward or reload. |
| `take_snapshot` | Lists what the page shows, as text. Each element has an identifier that the other tools take. |
| `take_screenshot` | Takes a picture of the page or of one element. `filePath` saves it to a file. |
| `click`, `click_at`, `hover`, `drag` | Use the pointer. |
| `fill`, `fill_form`, `type_text`, `press_key`, `upload_file` | Write text, choose options, press keys, give a file to a file input. |
| `handle_dialog`, `wait_for` | Answer an alert or a confirmation, wait for a text. |
| `evaluate_script` | Runs JavaScript in the page. It can be turned off in Settings. |
| `list_console_messages`, `get_console_message`, `list_network_requests`, `get_network_request` | Read the console and the requests of the page, with their bodies. |
| `resize_page`, `emulate` | Size the page, emulate a device, a color scheme, a location, a slow network or CPU. |
| `performance_start_trace`, `performance_stop_trace`, `take_heapsnapshot` | Chromium only: a trace for the DevTools Performance panel with a summary (paints, layout shifts, long tasks), and a heap snapshot for its Memory panel. |

## Sandboxed sessions

A [sandboxed session](sessions/sandbox.md) browses in the isolated profile only. Its browser reaches the network of its sandbox profile, through the same proxy as its commands, and the folders of its sandbox for uploads and screenshots. With a profile without network, it reaches only the local ports the profile lists. It opens no `file:` page: serve the folder with a local server on a port the profile lists.

## Good to know

- `file:` pages open only from the folders of the session, and never in a sandboxed session. A page never gets a file that looks like it holds secrets (`.env`, keys, `.ssh`).
- A page never reaches the ports of CodeAlta itself: its MCP server and the developer instance.
- The CodeAlta toolbar runs apart from the page: the page can neither see it nor report an annotation. Firefox and Safari have no toolbar yet.
- Downloads go to `~/.alta/browser/downloads` and are never opened; in your own browser, they go where your browser saves them. A sandboxed session downloads nothing.
- A Firefox CodeAlta starts never checks for or installs an update of your Firefox.
- To keep sessions from using a browser, turn off the **Browser** plugin in **Settings**, **Plugins**.
