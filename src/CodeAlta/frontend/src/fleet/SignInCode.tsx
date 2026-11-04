import { useState } from "react";
import { Button, Callout } from "@blueprintjs/core";
import { fleetServer } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import type { SignInCode as Code } from "./signIn";

/** The call that opens the page of a code. */
export type SignInCodeApi = Pick<typeof fleetServer, "openSignInPage">;

/**
 * The code of a GitHub sign-in while this computer waits for it: the code to enter at GitHub, and one button that copies
 * it and opens GitHub's page in the browser. The host opens only the page of a code that waits, named by its handle.
 */
export function SignInCode({ epoch, code, api = fleetServer, onCancel }: { epoch: string; code: Code; api?: SignInCodeApi; onCancel?: () => void }) {
  const { t } = useShellLanguage();
  const [opened, setOpened] = useState<boolean | null>(null);
  async function open() {
    await navigator.clipboard?.writeText(code.userCode).catch(() => { /* The code stays shown and selectable. */ });
    const result = await api.openSignInPage({ expectedEpoch: epoch, handle: code.handle }, { timeoutMilliseconds: 15_000 }).catch(() => null);
    setOpened(result?.status === "ok");
  }
  return <Callout compact intent="primary" role="status" className="fleet-sign-in-code" icon={<ActivitySpinner size={16} />}>
    <p>{t("Enter this code at GitHub to finish signing in:")}</p>
    <Copyable code value={code.userCode} label={t("Copy code")} />
    <div className="fleet-sign-in-code-actions">
      <Button size="small" intent="primary" icon={<AppIcon name="openExternal" size={14} />} text={t("Copy the code and open GitHub")} onClick={() => void open()} />
      {onCancel && <Button size="small" text={t("Cancel")} onClick={onCancel} />}
    </div>
    {opened === false && <div className="muted-text">{t("Open {url} in a browser and enter the code there.", { url: code.url })}</div>}
  </Callout>;
}
