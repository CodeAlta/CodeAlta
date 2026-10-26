import type { LabelItem } from "#neoastra";
import { brandColor, type Brand } from "../brands";
import { spaceBrand } from "../spaces/spaces";

/**
 * A label: a name the user gives to projects to find them together, shown as a badge of its icon and color. A label
 * can be under another one, and a project can have several. Only a top-level label has a space; its sub-labels are in
 * the same one.
 */
export type Label = Readonly<{
  /** Its identifier, worked out from its first name and kept when it is renamed or moved. */
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  /** Its place among the labels of its parent, from 1. */
  order: number;
  /** The label it is under; null for a top-level label. */
  parent: string | null;
  favorite: boolean;
  /** Its space; null for every space. */
  space: string | null;
  /** The projects whose file names it, archived ones included. */
  projectIds: readonly string[];
}>;

/** The longest name of a label. */
export const maximumLabelName = 64;
/** The longest description of a label. */
export const maximumLabelDescription = 2000;
/** The most levels of labels: a top-level label is at level 1. */
export const maximumLabelDepth = 4;
/** What separates the names of a label and of the labels above it, in its path. */
export const labelPathSeparator = " › ";

const text = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length > 0 && value.length <= maximum;
const identifier = (value: unknown): value is string => typeof value === "string" && /^[a-z0-9][a-z0-9\-_.]{1,63}$/.test(value);

/** The labels as the host lists them, in the order of their tree. What is not a label is left out, and a parent that is not listed is read as none. */
export function readLabels(items: readonly LabelItem[] | null | undefined): readonly Label[] {
  const labels: Label[] = [];
  const seen = new Set<string>();
  for (const item of Array.isArray(items) ? items.slice(0, 512) : []) {
    if (!item || !identifier(item.id) || seen.has(item.id) || !text(item.name, 256)) continue;
    seen.add(item.id);
    labels.push({ id: item.id, name: item.name, description: text(item.description, 4096) ? item.description : null,
      icon: text(item.icon, 64) ? item.icon : null, color: brandColor(item.color) ?? null,
      order: typeof item.order === "number" && Number.isFinite(item.order) ? item.order : 0,
      parent: identifier(item.parent) ? item.parent : null, favorite: item.favorite === true, space: identifier(item.space) ? item.space : null,
      projectIds: Array.isArray(item.projectIds) ? item.projectIds.filter((id: unknown) => text(id, 256)) : [] });
  }
  return labels.map(label => label.parent && !seen.has(label.parent) ? { ...label, parent: null } : label);
}

/** Whether two readings list the same labels: a reading that changes nothing is not shown again. */
export function sameLabels(left: readonly Label[], right: readonly Label[]): boolean {
  return left === right || left.length === right.length && left.every((label, index) => {
    const other = right[index];
    return label.id === other.id && label.name === other.name && label.description === other.description && label.icon === other.icon
      && label.color === other.color && label.order === other.order && label.parent === other.parent && label.favorite === other.favorite
      && label.space === other.space && label.projectIds.length === other.projectIds.length && label.projectIds.every((id, at) => id === other.projectIds[at]);
  });
}

/** The labels a space shows: those of that space and those of every space, in the order of their tree. */
export function labelsOfSpace(labels: readonly Label[], spaceId: string): readonly Label[] {
  return labels.filter(label => label.space === null || label.space === spaceId);
}

/** The labels above a label, from the top one to its parent. */
export function labelAncestors(label: Label, labels: readonly Label[]): readonly Label[] {
  const above: Label[] = [];
  for (let parent = label.parent; parent && above.length < maximumLabelDepth; ) {
    const found = labels.find(value => value.id === parent);
    if (!found || above.includes(found)) break;
    above.unshift(found);
    parent = found.parent;
  }
  return above;
}

/** The path of a label: the names of the labels above it and its own, such as `Infra › Helm charts`. */
export function labelPath(label: Label, labels: readonly Label[]): string {
  return [...labelAncestors(label, labels).map(value => value.name), label.name].join(labelPathSeparator);
}

/** The identifiers of a label and of every label under it. */
export function labelSubtree(id: string, labels: readonly Label[]): readonly string[] {
  const ids = [id];
  for (let index = 0; index < ids.length && ids.length <= labels.length; index++)
    for (const label of labels) if (label.parent === ids[index] && !ids.includes(label.id)) ids.push(label.id);
  return ids;
}

/** The level of a label: 1 at the top. */
export const labelDepth = (label: Label, labels: readonly Label[]): number => labelAncestors(label, labels).length + 1;

/** The levels a label and its sub-labels take: 1 for a label without any. */
export function labelHeight(id: string, labels: readonly Label[], depth = 0): number {
  if (depth > maximumLabelDepth) return 1;
  return 1 + Math.max(0, ...labels.filter(label => label.parent === id).map(child => labelHeight(child.id, labels, depth + 1)));
}

/** The labels of a project a space shows, in the order of their tree. */
export function projectLabels(projectId: string, labels: readonly Label[]): readonly Label[] {
  return labels.filter(label => label.projectIds.includes(projectId));
}

/** What a name cannot be: blank, too long, or the name of another label beside it. Null for a name that can be given. */
export function labelNameProblem(name: string, labels: readonly Label[], parent: string | null, ownId: string | null): "empty" | "long" | "taken" | null {
  const wanted = name.trim();
  if (!wanted) return "empty";
  if (wanted.length > maximumLabelName) return "long";
  return labels.some(label => label.id !== ownId && label.parent === parent && label.name.toLowerCase() === wanted.toLowerCase()) ? "taken" : null;
}

/** Whether a label can be moved under another one (null: to the top): not under itself or its own sub-labels, and not deeper than the labels go. */
export function canMoveLabel(id: string, parent: string | null, labels: readonly Label[]): boolean {
  if (parent === null) return true;
  const above = labels.find(label => label.id === parent);
  return !!above && !labelSubtree(id, labels).includes(parent) && labelDepth(above, labels) + labelHeight(id, labels) <= maximumLabelDepth;
}

/** The brand that draws the icon of a label in its color. */
export const labelBrand = (label: Readonly<{ icon: string | null; color: string | null }>): Brand => spaceBrand(label);
