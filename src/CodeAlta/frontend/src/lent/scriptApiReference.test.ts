import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import test from "node:test";
import ts from "typescript";
import { altaInterfaceVersion } from "../pluginScript/versions";
import { lentLibraries } from "./libraries";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const referenceRoot = "../../../../CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/references/";
const api = read(`${referenceRoot}script-api.md`);
const libraries = read(`${referenceRoot}script-libraries.md`);

test("the library reference is discoverable from the skill and the script API", () => {
  assert.ok(read(`${referenceRoot}../SKILL.md`).includes("](references/script-libraries.md)"));
  assert.ok(api.includes("](script-libraries.md)"));
});

test("the script library index names every import and the exact locked versions", () => {
  const tables = libraries.split("\n").filter(line => line.startsWith("| `"))
    .map(line => line.split("|").map(cell => cell.trim()));
  const versions = new Map(tables.map(cells => [cells[1].slice(1, -1), cells[2]]));
  const lock = JSON.parse(read("../../package-lock.json")) as { packages: Record<string, { version: string }> };
  const imports = [...libraries.split("## React")[0].matchAll(/^\| `([^`]+)` \|/gmu)].map(match => match[1]);
  assert.deepEqual(imports.sort(), lentLibraries.map(library => library.name).sort());
  for (const [name, version] of versions) {
    const packageName = name === "react/jsx-runtime" ? "react" : name === "react-dom/client" ? "react-dom" : name;
    assert.equal(version, name === "codealta" ? String(altaInterfaceVersion) : lock.packages[`node_modules/${packageName}`]?.version, name);
  }
  assert.ok(api.includes(`\`interfaceVersion\` is **${altaInterfaceVersion}**`));
  for (const match of libraries.matchAll(/https:\/\/unpkg\.com\/((?:@[^/]+\/)?[^@]+)@([^/]+)\//gu)) {
    assert.equal(match[2], versions.get(match[1]), `pinned declarations for ${match[1]}`);
  }
});

test("the script references inventory every named runtime export of the lent wrappers", () => {
  const require = createRequire(import.meta.url);
  const inventories = new Map([...`${api}\n${libraries}`.matchAll(/<!-- exports: (.+) -->\s*([\s\S]*?)<!-- \/exports -->/gu)]
    .map(match => [match[1], [...match[2].matchAll(/`([^`]+)`/gu)].map(name => name[1])]));
  assert.deepEqual([...inventories.keys()].sort(), lentLibraries.map(library => library.name).filter(name => name !== "lucide-react").sort());
  for (const library of lentLibraries) {
    const entry = ts.createSourceFile(library.entry, read(`../../${library.entry}`), ts.ScriptTarget.Latest, true);
    const names: string[] = [];
    for (const statement of entry.statements) {
      if (!ts.isExportDeclaration(statement) || statement.isTypeOnly) continue;
      if (statement.exportClause && ts.isNamedExports(statement.exportClause)) {
        names.push(...statement.exportClause.elements.filter(element => !element.isTypeOnly).map(element => element.name.text));
      } else if (!statement.exportClause) {
        assert.ok(statement.moduleSpecifier && ts.isStringLiteral(statement.moduleSpecifier));
        assert.equal(statement.moduleSpecifier.text, library.name, "star exports are the root of the documented package");
        names.push(...Object.keys(require(library.name)).filter(name => name !== "default" && name !== "__esModule"));
      }
    }
    if (library.name === "lucide-react") {
      // Its thousands of icon aliases have a pinned upstream inventory, not a duplicated list in the skill.
      for (const name of ["ChartColumn", "ChartColumnIcon", "LucideChartColumn", "Icon", "createLucideIcon", "icons"]) assert.ok(names.includes(name), name);
      assert.ok(!names.includes("LucideIcon"), "a type, not a runtime export");
    } else {
      assert.deepEqual(inventories.get(library.name)!.sort(), names.sort(), library.name);
    }
  }
});
