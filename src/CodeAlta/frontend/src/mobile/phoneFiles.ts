import { isImageAttachment } from "../promptImages";
import { addAttachments, createHeldFiles, hasImages, type PhoneAttachment } from "./attachments";
import { el, icon } from "./dom";

/** The key of the files of the new-session screen, beside those of each session. */
export const newSessionFiles = "\u0000new";

/** Where a strip of held files shows: the chips above the prompt, and the note that says what was refused. */
export type FilesPlace = Readonly<{ strip: HTMLElement; note: HTMLElement }>;

/**
 * The files and photos the phone holds with a prompt, as the desktop composer holds them: for each session and for the
 * new-session screen, until they leave with a Send, a Queue or a new session. Images need a model that reads them.
 */
export function createPhoneFiles(options: Readonly<{
  document: Document;
  input: HTMLInputElement;
  session: FilesPlace;
  newSession: FilesPlace;
  /** The session whose composer shows, or null. */
  current: () => string | null;
  /** Whether the model of a session (or of the new session) reads images; null while it is not known. */
  readsImages: (key: string) => boolean | null;
  /** The files of a key changed: the composer shows what it can do with them. */
  changed: (key: string) => void;
}>) {
  const { document, input } = options;
  const held = createHeldFiles();
  // What the picker adds to: the one whose button opened it, else the composer that shows.
  let target: string | null = null;

  const placeOf = (key: string) => key === newSessionFiles ? options.newSession : options.session;
  const shown = (key: string) => key === newSessionFiles || key === options.current();
  // The screens the user is on keep their files; another session's go first when the phone holds too much.
  const keep = () => [newSessionFiles, ...(options.current() ? [options.current()!] : [])];

  function set(key: string, files: readonly PhoneAttachment[]) {
    for (const gone of held.set(key, files, keep())) options.changed(gone);
    if (shown(key)) draw(key);
    options.changed(key);
  }

  function draw(key: string) {
    const place = placeOf(key);
    const files = held.get(key);
    place.strip.replaceChildren(...files.map(file => {
      const chip = el(document, "span", "attach-chip");
      const remove = el(document, "button");
      remove.type = "button";
      remove.setAttribute("aria-label", `Remove ${file.title}`);
      remove.append(icon(document, "close"));
      remove.addEventListener("click", () => set(key, held.get(key).filter(item => item !== file)));
      chip.append(el(document, "span", null, file.title), remove);
      return chip;
    }));
    place.strip.hidden = files.length === 0;
  }

  function note(key: string, text: string | null) {
    if (!shown(key)) return;
    const place = placeOf(key);
    place.note.textContent = text ?? "";
    place.note.hidden = !text;
  }

  async function take(list: FileList | null) {
    const chosen = list ? Array.from(list) : [];
    const key = target ?? options.current() ?? newSessionFiles;
    target = null;
    input.value = "";
    if (!chosen.length) return;
    const added = await addAttachments(held.get(key), chosen, options.readsImages(key) !== false);
    set(key, added.files);
    note(key, added.notice);
  }
  input.addEventListener("change", () => { void take(input.files); });

  return {
    /** Opens the picker for the files of a session's composer, or of the new session. */
    choose(key: string) { target = key; input.click(); },
    get: (key: string): readonly PhoneAttachment[] => held.get(key),
    /** The files of a key, which leave with its prompt: no longer held. */
    take(key: string): readonly PhoneAttachment[] {
      const files = held.take(key);
      if (shown(key)) draw(key);
      options.changed(key);
      return files;
    },
    /** Gives back the files of a prompt that was not sent, unless others were chosen since. */
    giveBack(key: string, files: readonly PhoneAttachment[]) {
      if (files.length && !held.get(key).length) set(key, files);
    },
    note,
    /** The composer shows a session: its files, and why it has none when they were let go. */
    show(key: string | null) {
      if (!key) {
        options.session.strip.replaceChildren();
        options.session.strip.hidden = true;
        return;
      }
      draw(key);
      note(key, held.wasDropped(key) ? "The files held for this session were let go: the phone held too many. Attach them again." : null);
    },
    /** Lets the images of a key go once its model is known not to read them. */
    reconcile(key: string) {
      const files = held.get(key);
      if (options.readsImages(key) !== false || !hasImages(files)) return;
      const kept = files.filter(file => !isImageAttachment(file));
      set(key, kept);
      note(key, kept.length ? "This model does not read images. The other files were attached." : "This model does not read images.");
    },
  };
}

export type PhoneFiles = ReturnType<typeof createPhoneFiles>;
