// Disposable page; a real code editor of a project, over files in memory, opened on the preview of a Markdown file.
// The page reads what the editor did through `editorKeyboardFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FileTab } from "../fileTabs";
import { ShellLanguageContext } from "../shellLanguage";
import { createFileEditors } from "./fileEditors";
import { ProjectEditor } from "./ProjectEditor";

const path = "docs/readme.md";
const text = `# A page\n\n${"A line of the page.\n\n".repeat(40)}`;
const tab: FileTab = { projectId: "p", projectPath: "/work/project", view: "editor" };
const refused = async () => ({ status: "unavailable" }) as never;
const api = {
  read: async () => ({ status: "ok", content: text, revision: "1", readOnly: false, stamp: "1", encoding: "utf-8", length: text.length, path }),
  stat: async () => ({ status: "ok", files: [] }) as never,
  write: refused, list: refused, create: refused, rename: refused, delete: refused, image: refused, reveal: refused, search: refused,
};
const git = { changes: refused };
const request = { path, line: null, column: null, explorer: false, view: "preview" } as const;

localStorage.clear();
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <div style={{ width: 1000, height: 700, display: "flex" }}>
      <ProjectEditor tab={tab} projectName="project" epoch="epoch" visible active platform="windows" editors={createFileEditors()} request={request}
        onActivate={() => { }} api={api as never} git={git as never} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
Object.assign(window, { editorKeyboardFixture: { path } });
