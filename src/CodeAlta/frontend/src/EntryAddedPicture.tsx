// Where the picture puts the tiles of the other applications: the window of the Finder, then the Dock.
const folderTiles = [132, 202, 342] as const;
const dockTiles = [124, 164, 204, 284, 324, 364] as const;

/** The gesture: the application in the window of its folder, an arrow, and its place in the Dock. */
export function EntryAddedPicture({ logo, label, folder }: { /** The address of the application's mark. */ logo: string; label: string; folder: string }) {
  return <svg className="entry-added-picture" viewBox="0 0 520 252" role="img" aria-label={label}>
    <defs><marker id="entry-added-arrow" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0 0L10 5L0 10z" className="entry-added-accent-fill" /></marker></defs>
    <rect className="entry-added-window" x="100.5" y="10.5" width="320" height="150" rx="11" />
    <path className="entry-added-line" d="M100.5 40.5h320" />
    {[118, 134, 150].map(x => <circle key={x} className="entry-added-dot" cx={x} cy="25.5" r="4.5" />)}
    <text className="entry-added-title" x="260.5" y="30" textAnchor="middle">{folder}</text>
    {folderTiles.map(x => <g key={x}><rect className="entry-added-tile" x={x} y="62" width="46" height="46" rx="11" />
      <rect className="entry-added-name" x={x + 5} y="120" width="36" height="6" rx="3" /></g>)}
    <image href={logo} x="270" y="60" width="50" height="50" />
    <text className="entry-added-app" x="295" y="127" textAnchor="middle">CodeAlta</text>
    <path className="entry-added-move" d="M295 138C295 172 260 162 260 192" markerEnd="url(#entry-added-arrow)" />
    <rect className="entry-added-dock" x="110.5" y="198.5" width="300" height="44" rx="14" />
    {dockTiles.map(x => <rect key={x} className="entry-added-tile" x={x} y="205" width="31" height="31" rx="8" />)}
    <rect className="entry-added-place" x="241.5" y="202.5" width="36" height="36" rx="10" />
    <image href={logo} x="244" y="205" width="31" height="31" />
    <text className="entry-added-title" x="424" y="225" textAnchor="start">Dock</text>
  </svg>;
}
