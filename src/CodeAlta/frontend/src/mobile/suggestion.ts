import { promptSuggestionText, shownPromptSuggestion } from "../promptSuggestion";
import { isBusy, type Live } from "./contract";

/**
 * The suggestion the composer of a session offers as a chip, with the rules of the desktop's prompt: none while a turn
 * runs or something waits for the person (a permission request, an ask, a prompt being sent), none while the box holds
 * text or files, and not the one a prompt was sent after. It comes back when the box is emptied.
 */
export function phoneSuggestion(live: Live | null | undefined, state: { text: string; files: number; waiting: boolean; answered: string | null }): string | null {
  if (!live || isBusy(live.live)) return null;
  return shownPromptSuggestion(promptSuggestionText(live.suggestion), { text: state.text, images: state.files, busy: state.waiting, answered: state.answered });
}
