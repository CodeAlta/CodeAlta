import type { ReactNode } from "react";
import { Button, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { chosenCount, toggledFacet, type FacetValues } from "./tableRows";

/**
 * A facet a table filters by, with its values, what each is called, and how many rows have it (none: a facet the server
 * answers, whose counts are not known here). A single facet holds one value at most: choosing another replaces it, and the
 * button says it.
 */
export type FilterFacet = Readonly<{
  id: string; label: MessageKey; icon: IconName; single?: boolean;
  options: readonly Readonly<{ value: string; label: string; count?: number }>[];
}>;

/**
 * What a table of the Fleet console shows: a search over what its rows say, and a menu per facet (state, system,
 * kind...) whose values add up within it and narrow across them. The count says how many rows are shown of how many.
 */
export function FilterBar({ label, query, onQuery, placeholder, facets, values, onValues, shown, total, status, actions, children }: {
  label: string;
  query: string; onQuery: (query: string) => void;
  /** What the search field says while empty; "Filter" by default. */
  placeholder?: MessageKey;
  facets: readonly FilterFacet[];
  values: FacetValues; onValues: (values: FacetValues) => void;
  shown?: number; total?: number;
  /** What the bar says of the rows instead of the count, such as how many a server gave of a log. */
  status?: ReactNode;
  /** What the person does with the list (Add…), at its end. */
  actions?: ReactNode;
  /** What a facet asks once chosen (the days of a period...), after the facets. */
  children?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const chosen = chosenCount(values);
  const said = status ?? (shown === undefined || total === undefined ? null
    : shown === total ? t("{count} in all", { count: total }) : t("{shown} of {total}", { shown, total }));
  return <div className="fleet-filters" role="search" aria-label={label}>
    <InputGroup className="fleet-filters-search" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
      placeholder={t(placeholder ?? "Filter")} aria-label={t(placeholder ?? "Filter")} value={query} onValueChange={onQuery}
      onKeyDown={event => { if (event.key === "Escape" && query) { event.preventDefault(); event.stopPropagation(); onQuery(""); } }} />
    {facets.filter(facet => facet.options.length > 0).map(facet => {
      const picked = values[facet.id] ?? [];
      const choice = facet.single && picked.length === 1 ? facet.options.find(option => option.value === picked[0]) : undefined;
      const pick = (value: string) => onValues(facet.single
        ? { ...values, [facet.id]: picked.includes(value) ? [] : [value] }
        : toggledFacet(values, facet.id, value));
      return <PopoverNext key={facet.id} placement="bottom-start" content={<Menu className="fleet-filters-menu" role="listbox" aria-multiselectable={!facet.single} aria-label={t(facet.label)}>
        <MenuDivider title={t(facet.label)} />
        {facet.options.map(option => <MenuItem key={option.value} roleStructure="listoption" shouldDismissPopover={!!facet.single} selected={picked.includes(option.value)}
          text={option.label} labelElement={option.count === undefined ? undefined : <span className="muted-text">{option.count}</span>} onClick={() => pick(option.value)} />)}
      </Menu>}>
        <Button size="small" variant={picked.length > 0 ? "outlined" : "minimal"} intent={picked.length > 0 ? "primary" : "none"} icon={<AppIcon name={facet.icon} size={13} />}
          endIcon={<AppIcon name="chevronDown" size={12} />} className="fleet-filters-facet" data-facet={facet.id}>
          {choice ? <><span className="fleet-filters-facet-label">{t(facet.label)}:</span> {choice.label}</>
            : <>{t(facet.label)}{picked.length > 0 && <Tag minimal round className="fleet-filters-count">{picked.length}</Tag>}</>}
        </Button>
      </PopoverNext>;
    })}
    {children}
    {(chosen > 0 || query) && <Button size="small" variant="minimal" icon={<AppIcon name="close" size={13} />} text={t("Clear")}
      onClick={() => { onValues({}); onQuery(""); }} />}
    {said !== null && <span className="fleet-filters-total muted-text" role="status">{said}</span>}
    {actions && <span className="fleet-filters-actions">{actions}</span>}
  </div>;
}
