import { useState } from "react";
import { machines } from "#neoastra";
import { useShellLanguage } from "../shellLanguage";

/** What opens the page of a sign-in in the browser of this computer. */
export type OpenSignInPage = (url: string) => Promise<boolean>;

/** Opens a page of a sign-in through the host: the window never navigates, and opens no window of its own. */
export function signInPageOpener(epoch: string, api: Pick<typeof machines, "openSignInPage"> = machines): OpenSignInPage {
  return url => api.openSignInPage({ expectedEpoch: epoch, url }, { timeoutMilliseconds: 15_000 }).then(result => result.status === "ok", () => false);
}

/**
 * The link to the page of a sign-in of a machine: a click opens it in the browser of this computer (the host opens it), and says
 * so when the browser did not open, so that the person copies the address instead.
 */
export function SignInPageLink({ url, label, open }: { url: string; label?: string; open: OpenSignInPage }) {
  const { t } = useShellLanguage();
  const [failed, setFailed] = useState(false);
  return <div className="machine-provider-code-row">
    <a href={url} rel="noreferrer" onClick={event => { event.preventDefault(); void open(url).then(opened => setFailed(!opened)); }}>{label ?? url}</a>
    {failed && <small className="machine-provider-problem" role="alert">{t("The browser of this computer did not open it: copy the address into your browser.")}</small>}
  </div>;
}
