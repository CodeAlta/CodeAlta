import { useEffect, useId, useState } from "react";
import { Button, Collapse, HTMLSelect, InputGroup, Tag } from "@blueprintjs/core";
import { modelCatalog } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";
import { formatModelRules, type ModelRuleDraft, type ModelRuleMatch } from "./modelVisibility";

type ModelEntry = Readonly<{ id: string; name: string }>;
// What the host says the rules of the form hide: how many among how many, the first of them, and whether it read them all; or
// why the rules do not read, as .NET says it.
type Preview = Readonly<{ hidden: readonly ModelEntry[]; count: number; total: number; complete: boolean }> | Readonly<{ problem: string }>;

const matchLabels: readonly (readonly [ModelRuleMatch, MessageKey])[] = [["any-case", "Model id, any case"], ["exact", "Exact id"], ["regex", "Regular expression"]];
// The most models the preview names; the others are counted.
const previewLimit = 24;

// One list of rules: a row each, how it matches, what it matches, and a button that removes it.
function ModelRuleList({ label, rules, models, disabled, onChange }: {
  label: MessageKey; rules: readonly ModelRuleDraft[]; models: string; disabled: boolean; onChange: (rules: readonly ModelRuleDraft[]) => void;
}) {
  const { t } = useShellLanguage();
  const change = (index: number, rule: ModelRuleDraft) => onChange(rules.map((current, at) => at === index ? rule : current));
  return <div className="provider-model-rules" role="group" aria-label={t(label)}>
    <span className="provider-model-rules-label">{t(label)}</span>
    {rules.map((rule, index) => <div key={index} className="provider-model-rule">
      <HTMLSelect value={rule.match} disabled={disabled} aria-label={t("How the rule matches")} onChange={event => change(index, { ...rule, match: event.target.value as ModelRuleMatch })}>
        {matchLabels.map(([match, text]) => <option key={match} value={match}>{t(text)}</option>)}
      </HTMLSelect>
      <InputGroup value={rule.pattern} disabled={disabled} spellCheck={false} aria-label={t("Rule")} list={rule.match === "regex" ? undefined : models}
        placeholder={rule.match === "regex" ? "^claude-" : "gpt-5.4"} onChange={event => change(index, { ...rule, pattern: event.target.value })} />
      <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="close" size={14} />} aria-label={t("Remove the rule")} title={t("Remove the rule")}
        onClick={() => onChange(rules.filter((_, at) => at !== index))} />
    </div>)}
    <Button variant="minimal" size="small" className="provider-model-rules-add" disabled={disabled} icon={<AppIcon name="plus" size={14} />} text={t("Add a rule")}
      onClick={() => onChange([...rules, { match: "any-case", pattern: "" }])} />
  </div>;
}

/**
 * The models a provider hides from the lists where a model is chosen: the rules that hide some, the rules that show some
 * again, and the models they hide. It is folded under a line that says how many it hides: few people need it.
 * `providerId` is the saved provider whose models are read for the preview, or null while it is not ready.
 */
export function ProviderModelVisibility({ epoch, revision, providerId, hide, show, disabled, readModels, preview: readPreview = modelCatalog.visibility, onChange }: {
  /** The revision of the configuration: a save reads the models again, with what the saved rules hide. */
  epoch: string; revision: string | null; providerId: string | null; hide: readonly ModelRuleDraft[]; show: readonly ModelRuleDraft[]; disabled: boolean;
  readModels: typeof modelCatalog.models;
  /** Matches the rules of the form on the host, as the provider will: the host by default. */
  preview?: typeof modelCatalog.visibility;
  onChange: (change: { modelsHide?: readonly ModelRuleDraft[]; modelsShow?: readonly ModelRuleDraft[] }) => void;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [models, setModels] = useState<readonly ModelEntry[] | null>(null);
  const datalist = useId();
  const body = useId();
  useEffect(() => {
    setModels(null);
    if (!providerId) return;
    const controller = new AbortController();
    // Shown and hidden alike: the preview applies the rules of the form, saved or not.
    void readModels({ expectedEpoch: epoch, providerId }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
      .then(reply => {
        if (controller.signal.aborted || reply.status !== "ok") return;
        setModels([...reply.models.map(model => ({ id: model.id, name: model.name })), ...(reply.hidden ?? [])]);
      }).catch(() => { /* No preview: the rules still apply once saved. */ });
    return () => controller.abort();
  }, [epoch, revision, providerId, readModels]);
  // The rules as they are saved, which the host matches once the provider listed its models, a moment after the last key.
  const hideRules = formatModelRules(hide), showRules = formatModelRules(show);
  const rulesKey = JSON.stringify([hideRules, showRules]);
  const [preview, setPreview] = useState<Preview | null>(null);
  useEffect(() => {
    if (!providerId || !models) { setPreview(null); return; }
    if (!hideRules.length) { setPreview({ hidden: [], count: 0, total: models.length, complete: true }); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void readPreview({ expectedEpoch: epoch, providerId, hide: hideRules, show: showRules }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
        .then(reply => {
          if (controller.signal.aborted) return;
          if (reply.status === "invalid") setPreview({ problem: reply.problem ?? "" });
          else if (reply.status === "ok") setPreview({ hidden: reply.hidden, count: reply.hiddenCount, total: reply.total, complete: reply.complete });
          else setPreview(null);
        }).catch(() => { /* No preview: the rules still apply once saved. */ });
    }, 250);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [epoch, providerId, models, rulesKey, readPreview]);
  const rules = hideRules.length + showRules.length;
  const counted = preview && "count" in preview ? preview : null;
  const problem = preview && "problem" in preview ? preview.problem : null;
  const summary = counted ? (counted.count ? t(counted.complete ? "{hidden} of {total} models hidden" : "At least {hidden} of {total} models hidden", { hidden: counted.count, total: counted.total })
      : counted.complete ? t("All models are shown") : t("Rules: {count}", { count: rules }))
    : rules ? t("Rules: {count}", { count: rules }) : t("All models are shown");
  return <section className="provider-model-visibility provider-settings-wide" aria-label={t("Hidden models")}>
    <Button variant="minimal" fill alignText="start" className="provider-model-visibility-toggle" aria-expanded={open} aria-controls={body}
      icon={<AppIcon name={open ? "chevronDown" : "chevronRight"} size={14} />} onClick={() => setOpen(value => !value)}>
      <span className="provider-model-visibility-title">{t("Hidden models")}</span>
      <Tag minimal round intent={problem !== null ? "warning" : counted?.count ? "primary" : "none"}>{summary}</Tag>
    </Button>
    <Collapse isOpen={open} keepChildrenMounted>
      <div id={body} className="provider-model-visibility-body">
        <p className="provider-model-visibility-help">{t("Hide models of this provider from the lists where a model is chosen. A rule is a model id, whatever its case, an exact id, or a regular expression. To offer only a few models, hide them all with the regular expression .* and show those few.")}</p>
        <ModelRuleList label="Hide" rules={hide} models={datalist} disabled={disabled} onChange={modelsHide => onChange({ modelsHide })} />
        <ModelRuleList label="Show anyway" rules={show} models={datalist} disabled={disabled} onChange={modelsShow => onChange({ modelsShow })} />
        {models && <datalist id={datalist}>{models.map(model => <option key={model.id} value={model.id}>{model.name !== model.id ? model.name : undefined}</option>)}</datalist>}
        <div className="provider-model-visibility-preview" aria-live="polite">
          {problem !== null ? <span className="provider-model-visibility-problem" role="alert">{problem || t("A rule does not read.")}</span>
            : !counted ? <span className="bp6-text-muted">{t("Test the provider to see the models these rules hide.")}</span>
            : !counted.count ? <span className="bp6-text-muted">{t(counted.complete ? "No model is hidden." : "The rules took too long to match: try a simpler regular expression.")}</span>
            : <><span className="provider-model-visibility-preview-label">{t("Hidden by these rules:")}</span>{counted.hidden.slice(0, previewLimit).map(model => <Tag key={model.id} minimal title={model.name !== model.id ? model.name : undefined}>{model.id}</Tag>)}
              {counted.count > previewLimit && <span className="bp6-text-muted">{t("and {count} more", { count: counted.count - Math.min(previewLimit, counted.hidden.length) })}</span>}
              {!counted.complete && <span className="bp6-text-muted">{t("The rules took too long to match: try a simpler regular expression.")}</span>}</>}
        </div>
      </div>
    </Collapse>
  </section>;
}
