// The two places of the code editor that say where its files are, laid out as the editor lays them out: the header
// of its side, at the width the side starts with, and its status bar.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ShellLanguageContext } from "../shellLanguage";
import { EditorRootPath, EditorStatusPath } from "./EditorPaths";
import { absoluteTreePath } from "./fileTree";

const root = "C:\\Users\\someone\\source\\repositories\\a-project-with-a-long-name";
const copied: string[] = [];
Object.assign(window, { editorPaths: { root, copied } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <section className="project-editor" style={{ width: 900, height: 200 }}>
      <aside className="editor-side" style={{ width: 264 }}>
        <header className="editor-side-header">
          <span className="editor-side-names">
            <strong className="editor-side-title">a-project-with-a-long-name</strong>
            <EditorRootPath path={root} />
          </span>
          <span className="editor-side-actions">
            <Button variant="minimal" size="small" icon={<AppIcon name="newFile" size={15} />} />
            <Button variant="minimal" size="small" icon={<AppIcon name="newFolder" size={15} />} />
            <Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} />
          </span>
        </header>
      </aside>
      <div className="editor-main">
        <div className="editor-stage" />
        <footer className="editor-status">
          <span className="file-editor-status" data-state="clean" role="status">Saved</span>
          <EditorStatusPath icon="fileCode" tone="blue" text="src/app/main.ts" fullPath={absoluteTreePath(root, "src/app/main.ts")} copy={async text => { copied.push(text); }} />
          <button type="button" className="editor-status-item">Ln 1, Col 1</button>
          <span className="editor-status-item">UTF-8</span>
        </footer>
      </div>
    </section>
  </ShellLanguageContext.Provider>
</StrictMode>);
