// The entry of the phone page (mobile.html), served by the Mobile plugin to a phone of the tailnet. It has no NeoAstra
// bridge: it talks to the plugin's HTTP API only, and imports nothing that reaches `#neoastra`.
import "./mobile.css";
import { startMobile } from "./app";

startMobile(window);
