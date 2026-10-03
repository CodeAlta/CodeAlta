import { Actions, DockLocation, TabNode, TabSetNode, type Action } from "flexlayout-react";
import { sessionDraftNodeId } from "./sessionTabLayout";

export type SessionDropRect = { x: number; y: number; width: number; height: number };
export type SessionTabDrop = { action: Action; location: DockLocation; rect: SessionDropRect };

// Presentation geometry only. Authority is checked again by the action dispatcher
// at release; no model mutation or private FlexLayout drag/controller APIs here.
export function sessionTabDrop(source: TabNode, target: TabSetNode, rect: SessionDropRect,
  point: { x: number; y: number }, strip?: { index: number; rect: SessionDropRect }): SessionTabDrop | null {
  if (!source.isEnableDrag() || source.getId() === sessionDraftNodeId || source.getModel() !== target.getModel()
    || rect.width <= 0 || rect.height <= 0 || point.x < rect.x || point.x >= rect.x + rect.width
    || point.y < rect.y || point.y >= rect.y + rect.height) return null;
  const x = (point.x - rect.x) / rect.width, y = (point.y - rect.y) / rect.height;
  let location = DockLocation.CENTER;
  if (!strip) {
    const distance = Math.min(x, 1 - x, y, 1 - y);
    if (distance < 0.25) location = distance === x ? DockLocation.LEFT : distance === 1 - x ? DockLocation.RIGHT
      : distance === y ? DockLocation.TOP : DockLocation.BOTTOM;
  }
  if (location === DockLocation.CENTER ? !target.isEnableDrop() : !target.isEnableDivide()
    || source.getParent() === target && target.getTabNodes().length === 1) return null;
  const preview = { ...rect };
  if (location === DockLocation.LEFT || location === DockLocation.RIGHT) {
    preview.width /= 2;
    if (location === DockLocation.RIGHT) preview.x += preview.width;
  } else if (location === DockLocation.TOP || location === DockLocation.BOTTOM) {
    preview.height /= 2;
    if (location === DockLocation.BOTTOM) preview.y += preview.height;
  }
  return { action: Actions.moveNode(source.getId(), target.getId(), location, strip?.index ?? -1, true), location,
    rect: strip?.rect ?? preview };
}
