import { Switch } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { useTimelineEmphasis } from "../timelineEmphasis/useTimelineEmphasis";

/**
 * The row of Settings → Appearance that folds the work between two messages into one line. Off, the same lines are
 * open, and each one can still be closed.
 */
export function FocusedTimelineSetting() {
  const { t } = useShellLanguage();
  const [emphasis, setEmphasis] = useTimelineEmphasis();
  return <Field label={t("Focused timeline")} htmlFor="settings-focused-timeline"
    notice={<p className="settings-field-help">{t("Fold reasoning, tool calls and background jobs between two messages into one line. Click a line to see its steps.")}</p>}>
    <Switch id="settings-focused-timeline" className="settings-checkbox" checked={emphasis.focused}
      onChange={event => setEmphasis({ ...emphasis, focused: event.currentTarget.checked })} />
  </Field>;
}
