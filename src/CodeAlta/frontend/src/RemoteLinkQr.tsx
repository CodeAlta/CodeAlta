import { useMemo } from "react";
import { encode } from "uqr";

/**
 * The QR code of a text: its width in modules, and its dark modules as one SVG path, with the quiet zone of four
 * modules that readers expect around it.
 */
export function qrCode(text: string): { size: number; path: string } {
  const { data, size } = encode(text, { ecc: "M", border: 4 });
  let path = "";
  data.forEach((row, y) => row.forEach((dark, x) => { if (dark) path += `M${x} ${y}h1v1h-1z`; }));
  return { size, path };
}

/**
 * The link of a session on claude.ai as a QR code, to open it on a phone. It is dark on white in both themes: that
 * is what the camera of a phone reads best.
 */
export function RemoteLinkQr({ url, label }: { url: string; label: string }) {
  const code = useMemo(() => qrCode(url), [url]);
  return <svg className="remote-control-qr" data-remote-control-qr viewBox={`0 0 ${code.size} ${code.size}`} role="img" aria-label={label}
    shapeRendering="crispEdges">
    <rect width={code.size} height={code.size} fill="#fff" />
    <path d={code.path} fill="#000" />
  </svg>;
}
