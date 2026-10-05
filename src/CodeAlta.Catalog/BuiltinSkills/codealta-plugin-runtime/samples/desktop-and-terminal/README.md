# Desktop and terminal sample

One plugin that works in CodeAlta Desktop and in CodeAlta TUI. It keeps short notes in memory.

- `/note-add` (F9) asks for a note with a portable input dialog.
- `/notes` shows the notes: an HTML fragment with Add and Remove actions on the desktop, native controls in the terminal.
- A status item shows the count; on the desktop a click opens `/notes`.
- Typing `!` in the prompt opens a picker that inserts a note.

Copying the folder into a plugin root runs its code inside CodeAlta the next time CodeAlta starts.
