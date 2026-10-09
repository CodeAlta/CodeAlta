import { useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { CommandPermissionPanel } from "./CommandPermissionPanel";
import { UserInputPanel } from "./UserInputPanel";
import { createPermissionReviewer } from "./sessionPermissions";
import { createUserInputReviewer } from "./sessionUserInput";
import { createMutationCapability } from "./sessionOperations";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

const epoch = "11111111-1111-4111-8111-111111111111";
function transport<Request, Response>() {
  const calls: { request: Request; resolve: (value: Response) => void; reject: (error: Error) => void }[] = [];
  return { calls, invoke: (request: Request) => new Promise<Response>((resolve, reject) => calls.push({ request, resolve, reject })) };
}
const permissions = transport<Parameters<Parameters<typeof createPermissionReviewer>[0]>[0], Awaited<ReturnType<Parameters<typeof createPermissionReviewer>[0]>>>();
const decisions = transport<Parameters<Parameters<typeof createPermissionReviewer>[1]>[0], Awaited<ReturnType<Parameters<typeof createPermissionReviewer>[1]>>>();
const inputs = transport<Parameters<Parameters<typeof createUserInputReviewer>[0]>[0], unknown>();
const answers = transport<Parameters<Parameters<typeof createUserInputReviewer>[1]>[0], unknown>();
const cancels = transport<Parameters<Parameters<typeof createUserInputReviewer>[2]>[0], unknown>();
const permission = createPermissionReviewer(permissions.invoke, decisions.invoke);
const input = createUserInputReviewer(inputs.invoke, answers.invoke, cancels.invoke);
const capability = createMutationCapability(epoch);
const handle = { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", sessionId: "Settings",
  runId: null, interactionId: "Streaming", attemptId: epoch };
const inputPage = { status: "ok", hostEpoch: epoch, sessionId: "Settings", hasMore: false, entries: [{ handle, providerId: "Deny", prompts: [
  { id: "choice", header: "Cancel", question: "Allow once", options: [{ label: "Settings", description: "Complete" }], allowFreeform: false },
  { id: "free", header: null, question: "Copy failed", options: [], allowFreeform: true },
] }] };
const permissionPage = { status: "ok", hostEpoch: epoch, sessionId: "Settings", hasMore: false, entries: [{ handle, providerId: "Settings",
  kind: "commandExecution", grantRoot: null,
  command: "  echo 'Allow once'\n# 日本語 <script>literal</script>  " + "x".repeat(3000), workingDirectory: "Q:\\fixture\\Settings", reason: "Deny" }] };
function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [sessionId, select] = useState("Settings");
  const [running, run] = useState(true);
  useLayoutEffect(() => { Object.assign(window, { providerFixture: { language, select, run, epoch, handle, inputPage, permissionPage,
    permissions: permissions.calls, decisions: decisions.calls, inputs: inputs.calls, answers: answers.calls, cancels: cancels.calls,
    permission, input, capability } }); }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => {} }}>
    <main className="configuration-page">
      <CommandPermissionPanel reviewer={permission} epoch={epoch} sessionId={sessionId} canReview={() => capability.canMutate()} running={running} />
      <UserInputPanel reviewer={input} capability={capability} epoch={epoch} sessionId={sessionId} />
    </main>
  </ShellLanguageContext>;
}
createRoot(document.getElementById("app")!).render(<Fixture />);
