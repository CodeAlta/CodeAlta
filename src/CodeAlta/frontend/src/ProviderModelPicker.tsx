import { useEffect, useRef, useState } from "react";
import { Button, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { ModelCatalogModelsRequest, ModelCatalogModelsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { inventoryNotice, type InventoryNotice } from "./inventoryNotice";
import { isSignedOut } from "./providerForm";
import { useShellLanguage } from "./shellLanguage";

/**
 * Reads the selected provider's inventory only when its default-model picker opens. Selection edits the form, never the saved configuration.
 * A default model (`value`, or `fallback` while the field is left to it) that a complete inventory does not list is named above the list.
 */
export function ProviderModelPicker({ epoch, providerId, disabled, value, fallback = null, readModels, onChange }: {
  epoch: string; providerId: string | null; disabled: boolean; value: string; fallback?: string | null;
  readModels: (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;
  onChange: (id: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [open, setOpen] = useState(false), [query, setQuery] = useState("");
  const [page, setPage] = useState<ModelCatalogModelsResponse | null>(null);
  const [error, setError] = useState<InventoryNotice>(""), [retry, setRetry] = useState(0);
  const content = useRef<HTMLDivElement>(null);
  useEffect(() => {
    setPage(null); setError(""); setQuery("");
    if (!open || disabled || !providerId) return;
    const controller = new AbortController();
    void readModels({ expectedEpoch: epoch, providerId }, { signal: controller.signal, timeoutMilliseconds: 15_000 }).then(reply => {
      if (controller.signal.aborted) return;
      if (reply.epoch !== epoch || reply.status === "stale_epoch") { setError("Host identity changed. Reload required."); return; }
      if (reply.status !== "ok") {
        setError({ key: "Model inventory unavailable ({status}).", status: isSignedOut(reply.reason) ? t("Signed out") : reply.availability === "Unknown" ? reply.status : reply.availability }); return;
      }
      if (reply.providerId?.toLowerCase() !== providerId.toLowerCase() || !Array.isArray(reply.models) || reply.models.length > 128
        || reply.models.some(model => typeof model.id !== "string" || !model.id || model.id.length > 256 || typeof model.name !== "string")) {
        setError("Invalid model inventory. Reload required."); return;
      }
      setPage(reply);
    }).catch(() => { if (!controller.signal.aborted) setError("Model inventory could not be read."); });
    return () => controller.abort();
  }, [open, disabled, epoch, providerId, readModels, retry]);
  const terms = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  const shown = (page?.models ?? []).filter(model => terms.every(term => `${model.id} ${model.name}`.toLocaleLowerCase().includes(term)));
  // A bounded inventory may leave it out; an empty one says so on its own.
  const chosen = value.trim() || fallback?.trim() || "";
  const missing = !!page && !page.truncated && page.models.length > 0 && !!chosen && !page.models.some(model => model.id === chosen);
  return <PopoverNext placement="bottom-end" isOpen={open && !disabled && !!providerId} onInteraction={setOpen}
    content={<div ref={content} className="provider-model-picker">
      <InputGroup type="search" autoFocus aria-label={t("Search models")} placeholder={t("Search models")} value={query}
        onChange={event => setQuery(event.target.value)} onKeyDown={event => {
          if (event.key === "ArrowDown") { event.preventDefault(); content.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus(); }
        }} />
      {!page && !error && <p role="status">{t("Loading models…")}</p>}
      {error && <div role="alert"><p>{inventoryNotice(locale, error)}</p><Button size="small" onClick={() => setRetry(current => current + 1)}>{t("Try again")}</Button></div>}
      {page && <>
        {missing && <p role="status" className="provider-model-missing">{t("This provider does not list {model}. Choose one of its models.", { model: chosen })}</p>}
        <Menu aria-label={t("Models")}>{shown.map(model => <MenuItem key={model.id} data-model-id={model.id}
          text={model.name} label={model.name !== model.id ? model.id : undefined} selected={value === model.id}
          onClick={() => { onChange(model.id); setOpen(false); }} />)}</Menu>
        {shown.length === 0 && <p role="status">{t(page.models.length ? "No models match this search." : "This provider reported no models. A configured default is not inventory.")}</p>}
        {page.truncated && <p role="status">{t("Some provider inventories are bounded; more models may exist.")}</p>}
      </>}
    </div>}>
    <Button disabled={disabled || !providerId} aria-label={t("Choose a model")} aria-haspopup="menu"
      title={providerId ? t("Choose a model") : t("Save provider connection changes before listing models.")}
      icon={<AppIcon name="chevronDown" size={14} />}>{t("Models")}</Button>
  </PopoverNext>;
}
