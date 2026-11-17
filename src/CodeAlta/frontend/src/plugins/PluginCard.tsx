import type { CSSProperties, ReactNode } from "react";
import { Card } from "@blueprintjs/core";

/**
 * The card of a plugin: a picture at its top, its icon and its name, who made it, what it does, then what the page
 * says of it and its controls at its foot. It draws what it is given and knows nothing of where the plugin comes from:
 * the Plugins page of Settings gives it an installed plugin with its switch, and a list of plugins to install can give
 * it one with a button.
 */
export function PluginCard({ name, byline, description, accent, icon, cover, plainCover, showCover, off = false, kind, status, children, footStart, actions, overlay, id }: {
  /** The name of the plugin, and the line under it: its author and its version, or its id. */
  name: string; byline?: string | null; description?: string | null;
  /** The color of the plugin: its icon and the picture drawn for a plugin that has none are in it. */
  accent: string;
  /** The icon of the plugin, drawn in its badge. */
  icon: ReactNode;
  /** The address of its picture; without one, the card draws the picture of plugins (`plainCover`) in the color of the plugin. */
  cover?: string | null; plainCover: string;
  /** Whether the card has its picture; without it the card is compact. */
  showCover: boolean;
  /** Whether the plugin is turned off: the card is drawn quieter. */
  off?: boolean;
  /** What tells where the plugin comes from, beside its name, and what the application did with it, under its description. */
  kind?: ReactNode; status?: ReactNode;
  /** What else the card lists: canvases, errors, a folder. */
  children?: ReactNode;
  /** The foot of the card: what stands at its start, then its buttons. */
  footStart?: ReactNode; actions?: ReactNode;
  /** A question about the plugin, shown over its card. */
  overlay?: ReactNode;
  /** The id of the plugin, for what looks for its card. */
  id?: string;
}) {
  return <Card className="plugin-card" role="listitem" data-plugin={id} data-off={off ? "" : undefined} data-compact={showCover ? undefined : ""}
    style={{ "--plugin-accent": accent } as CSSProperties}>
    {overlay}
    {showCover && <div className="plugin-card-cover" aria-hidden="true">
      <img src={cover ?? plainCover} alt="" loading="lazy" decoding="async" draggable={false} />
      {!cover && <span className="plugin-card-tint" />}
    </div>}
    <div className="plugin-card-body">
      <header className="plugin-card-head">
        <span className="plugin-card-icon" aria-hidden="true">{icon}</span>
        <span className="plugin-card-title"><strong>{name}</strong>{byline && <small>{byline}</small>}</span>
        {kind && <span className="plugin-card-kind">{kind}</span>}
      </header>
      {description && <p className="plugin-card-description" title={description}>{description}</p>}
      {status}
      {children}
    </div>
    <footer className="plugin-card-foot">
      <span className="plugin-card-foot-start">{footStart}</span>
      <span className="plugin-card-actions">{actions}</span>
    </footer>
  </Card>;
}
