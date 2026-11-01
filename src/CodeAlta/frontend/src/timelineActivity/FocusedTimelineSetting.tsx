import { Switch } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { useTimelineEmphasis } from "../timelineEmphasis/useTimelineEmphasis";

/**
 * The rows of Settings → Appearance that fold the work between two messages into one line, and show the images the
 * model read under a folded line. Off, the same lines are open, and each one can still be closed.
 */
export function FocusedTimelineSetting() {
  const { t } = useShellLanguage();
  const [emphasis, setEmphasis] = useTimelineEmphasis();
  return <Field setting="appearance.focused_timeline" label={t("Focused timeline")} htmlFor="settings-focused-timeline"
    notice={<p className="settings-field-help">{t("Fold reasoning, tool calls and background jobs between two messages into one line. Click a line to see its steps.")}</p>}>
    <Switch id="settings-focused-timeline" className="settings-checkbox" checked={emphasis.focused}
      onChange={event => setEmphasis({ ...emphasis, focused: event.currentTarget.checked })} />
  </Field>;
}

/** The row of Settings → Appearance that shows, under a folded line, the images the model read during its work. */
export function FoldedImagesSetting() {
  const { t } = useShellLanguage();
  const [emphasis, setEmphasis] = useTimelineEmphasis();
  return <Field setting="appearance.folded_images" label={t("Images under folded lines")} htmlFor="settings-folded-images"
    notice={<p className="settings-field-help">{t("Show the images the model read, such as screenshots and image files, under the folded line of its work. Click an image to view it.")}</p>}>
    <Switch id="settings-folded-images" className="settings-checkbox" checked={emphasis.foldedImages} disabled={!emphasis.focused}
      onChange={event => setEmphasis({ ...emphasis, foldedImages: event.currentTarget.checked })} />
  </Field>;
}
