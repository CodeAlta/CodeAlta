import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { check } from "./checks";
import fixture from "./probe.svg";
import "./visuals.css";

export async function mount(parent: HTMLElement, report: (message: string) => void): Promise<() => void> {
  check(/^app:\/\/codealta\/assets\/visuals-.*\.js$/.test(import.meta.url), "Dynamic module did not execute from a relative packaged chunk");
  parent.className = "visual-proof";
  const image = new Image();
  image.alt = "Local probe square";
  image.src = fixture;
  parent.append(image);
  await image.decode();
  check(image.naturalWidth === 32 && image.currentSrc.startsWith("app://codealta/assets/"), "Relative image failed to load");
  check(getComputedStyle(parent).borderTopWidth === "3px", "Dynamic CSS did not apply");
  check(Array.from(document.styleSheets).some(sheet => /^app:\/\/codealta\/assets\/visuals-.*\.css$/.test(sheet.href ?? "")), "Dynamic stylesheet was not loaded from packaged assets");
  report(`PASS relative dynamic JS, image and CSS (${import.meta.url})`);

  const editor = new EditorView({
    parent,
    state: EditorState.create({
      doc: "// CodeAlta fake document\nHello, desktop",
      extensions: [EditorView.theme({ "&": { backgroundColor: "#152535", color: "#ffffff" } }), EditorView.lineWrapping],
    }),
  });
  try {
    check(getComputedStyle(editor.dom).backgroundColor === "rgb(21, 37, 53)", "CodeMirror runtime stylesheet did not apply");
    check(editor.contentDOM.isContentEditable, "CodeMirror is not editable");
    editor.dispatch({ changes: { from: 0, insert: "// Edited\n" } });
    check(editor.state.doc.toString().startsWith("// Edited\n"), "CodeMirror editing transaction failed");
    report("PASS CodeMirror runtime styles and editable document transaction");

    const { default: mermaid } = await import("mermaid");
    mermaid.initialize({ startOnLoad: false, securityLevel: "strict", htmlLabels: false, flowchart: { htmlLabels: false }, maxTextSize: 32_768, maxEdges: 200 });
    const diagram = document.createElement("section");
    diagram.setAttribute("aria-label", "Fake session diagram");
    parent.append(diagram);
    const result = await mermaid.render("probe-mermaid", "flowchart LR\n  A[Fake prompt] --> B[C# RPC]\n  B --> C[Local desktop]");
    diagram.innerHTML = result.svg; // Trusted library SVG from its strict, sanitizing renderer; no source HTML.
    const svg = diagram.querySelector("svg");
    check(svg && svg.querySelectorAll(".node").length === 3 && svg.getBoundingClientRect().width > 0, "Direct Mermaid SVG failed to render");
    check(!svg.querySelector("script"), "Unexpected script in Mermaid SVG");
    report("PASS direct strict Mermaid SVG, three visible nodes");
    return () => { editor.destroy(); parent.replaceChildren(); };
  } catch (error) {
    editor.destroy();
    throw error;
  }
}
