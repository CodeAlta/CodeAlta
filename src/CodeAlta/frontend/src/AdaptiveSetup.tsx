import { Alert } from "@blueprintjs/core";
import { settingsNavigation } from "./settingsNavigation";
import { useShellLanguage } from "./shellLanguage";

/**
 * Says that Adaptive is not set up when it is chosen in the model list, and offers its settings: a session is never put
 * on Adaptive before the user chose the models of its levels.
 */
export function AdaptiveSetupDialog({ isOpen, onClose, openSettings = () => settingsNavigation.open("models") }: {
  isOpen: boolean; onClose: () => void; openSettings?: () => void;
}) {
  const { t } = useShellLanguage();
  return <Alert isOpen={isOpen} className="adaptive-setup" intent="primary"
    confirmButtonText={t("Open Settings")} cancelButtonText={t("Cancel")} canEscapeKeyCancel canOutsideClickCancel
    onConfirm={() => { onClose(); openSettings(); }} onCancel={onClose}>
    <h3 className="adaptive-setup-title">{t("Adaptive is not set up")}</h3>
    <p>{t("With Adaptive, CodeAlta runs each turn on the model of a level and has models of another family review each change of code. Choose the models of its levels in Settings > Models.")}</p>
  </Alert>;
}
