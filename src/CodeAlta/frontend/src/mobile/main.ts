// The entry of the phone page (mobile.html), served by the Mobile plugin to a phone of the tailnet. It has no NeoAstra
// bridge: it talks to the plugin's HTTP API only, and imports nothing that reaches `#neoastra`.
// KaTeX's styles: its fonts are fetched only when a formula is drawn, and its code when a message holds one.
import "katex/dist/katex.min.css";
import "./mobile.css";
import { startMobile } from "./app";

startMobile(window);
