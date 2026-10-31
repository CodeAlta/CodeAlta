import { Alert } from "@blueprintjs/core";
import { settingsNavigation } from "./settingsNavigation";
import { useShellLanguage } from "./shellLanguage";

/**
 * Says that Adaptive is not set up when it is chosen in the model list, and offers its settings: a session is never put
 * on Adaptive before the user chose the tiers it takes its models from.
 */
export function AdaptiveSetupDialog({ isOpen, onClose, openSettings = () => settingsNavigation.open("models") }: {
  isOpen: boolean; onClose: () => void; openSettings?: () => void;
}) {
  const { t } = useShellLanguage();
  return <Alert isOpen={isOpen} className="adaptive-setup" intent="primary"
    confirmButtonText={t("Open Settings")} cancelButtonText={t("Cancel")} canEscapeKeyCancel canOutsideClickCancel
    onConfirm={() => { onClose(); openSettings(); }} onCancel={onClose}>
    <h3 className="adaptive-setup-title">{t("Adaptive is not set up")}</h3>
    <p>{t("With Adaptive, CodeAlta chooses the model of each turn from your model tiers: a turn that changed code checks its change against the request, and a turn that ends in trouble continues on a stronger model. Choose the tiers it uses in Settings > Models.")}</p>
  </Alert>;
}
