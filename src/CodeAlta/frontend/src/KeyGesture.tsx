/** Renders a gesture such as "Ctrl+G Ctrl+T" as key caps, chord strokes separated by a thin gap. */
export function KeyGesture({ gesture }: { gesture: string }) {
  return <span className="key-gesture">{gesture.split(" ").map((stroke, index) =>
    <span key={index} className="key-stroke">{stroke.split("+").map((part, at) => <kbd key={at}>{part}</kbd>)}</span>)}</span>;
}
