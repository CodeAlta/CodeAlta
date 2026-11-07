import { Button, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { chosenCount, toggledFacet, type FacetValues } from "./tableRows";

/** A facet a table filters by, with its values, what each is called, and how many rows have it. */
export type FilterFacet = Readonly<{ id: string; label: MessageKey; icon: IconName; options: readonly Readonly<{ value: string; label: string; count: number }>[] }>;

/**
 * What a table of the Fleet console shows: a search over what its rows say, and a menu per facet (state, system,
 * kind...) whose values add up within it and narrow across them. The count says how many rows are shown of how many.
 */
export function FilterBar({ label, query, onQuery, facets, values, onValues, shown, total, actions }: {
  label: string;
  query: string; onQuery: (query: string) => void;
  facets: readonly FilterFacet[];
  values: FacetValues; onValues: (values: FacetValues) => void;
  shown: number; total: number;
  /** What the person does with the list (Add…), at its end. */
  actions?: React.ReactNode;
}) {
  const { t } = useShellLanguage();
  const chosen = chosenCount(values);
  return <div className="fleet-filters" role="search" aria-label={label}>
    <InputGroup className="fleet-filters-search" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
      placeholder={t("Filter")} aria-label={t("Filter")} value={query} onValueChange={onQuery}
      onKeyDown={event => { if (event.key === "Escape" && query) { event.preventDefault(); event.stopPropagation(); onQuery(""); } }} />
    {facets.filter(facet => facet.options.length > 0).map(facet => {
      const picked = values[facet.id] ?? [];
      return <PopoverNext key={facet.id} placement="bottom-start" content={<Menu className="fleet-filters-menu" role="listbox" aria-multiselectable aria-label={t(facet.label)}>
        <MenuDivider title={t(facet.label)} />
        {facet.options.map(option => <MenuItem key={option.value} roleStructure="listoption" shouldDismissPopover={false} selected={picked.includes(option.value)}
          text={option.label} labelElement={<span className="muted-text">{option.count}</span>} onClick={() => onValues(toggledFacet(values, facet.id, option.value))} />)}
      </Menu>}>
        <Button size="small" variant={picked.length > 0 ? "outlined" : "minimal"} intent={picked.length > 0 ? "primary" : "none"} icon={<AppIcon name={facet.icon} size={13} />}
          endIcon={<AppIcon name="chevronDown" size={12} />} className="fleet-filters-facet">
          {t(facet.label)}{picked.length > 0 && <Tag minimal round className="fleet-filters-count">{picked.length}</Tag>}
        </Button>
      </PopoverNext>;
    })}
    {(chosen > 0 || query) && <Button size="small" variant="minimal" icon={<AppIcon name="close" size={13} />} text={t("Clear")}
      onClick={() => { onValues({}); onQuery(""); }} />}
    <span className="fleet-filters-total muted-text" role="status">{shown === total ? t("{count} in all", { count: total }) : t("{shown} of {total}", { shown, total })}</span>
    {actions && <span className="fleet-filters-actions">{actions}</span>}
  </div>;
}
