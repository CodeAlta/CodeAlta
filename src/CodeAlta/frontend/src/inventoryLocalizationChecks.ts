import assert from "node:assert/strict";
import { locales, translate, type MessageKey } from "./localization";

export async function inventoryLanguages(evaluate: (expression: string) => Promise<unknown>, calls: string,
  title: MessageKey, notice?: MessageKey, literalSelector = "") {
  const paint = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
  await paint();
  await evaluate(`void(window.inventoryKept={main:document.querySelector('main'),focus:document.activeElement,calls:JSON.stringify(${calls}),
    controls:[...document.querySelectorAll('main input,main select,main button')].map(n=>({n,value:n.value,disabled:n.disabled,pressed:n.getAttribute('aria-pressed')})),
    literal:[...document.querySelectorAll(${JSON.stringify('.model-catalog-providers button strong,.model-catalog-list button strong,.model-catalog-detail h3' + (literalSelector ? `,${literalSelector}` : ""))})].map(n=>({n,text:n.textContent}))})`);
  for (const locale of locales) {
    await evaluate(`inventoryLanguage('${locale}')`); await paint();
    assert.equal(await evaluate(`document.querySelector('main').getAttribute('aria-label')===${JSON.stringify(translate(locale, title))}`), true);
    if (notice) assert.equal(await evaluate(`document.querySelector('main').textContent.includes(${JSON.stringify(translate(locale, notice))})`), true);
    assert.equal(await evaluate(`inventoryKept.main===document.querySelector('main') && inventoryKept.focus===document.activeElement && inventoryKept.calls===JSON.stringify(${calls}) &&
      inventoryKept.controls.every(v=>v.n.isConnected&&v.value===v.n.value&&v.disabled===v.n.disabled&&v.pressed===v.n.getAttribute('aria-pressed')) &&
      inventoryKept.literal.every(v=>v.n.isConnected&&v.text===v.n.textContent)`), true, `${title}/${locale}: retained owner, focus, inputs, selected metadata, literal source and calls`);
  }
  await evaluate("inventoryLanguage('en')"); await paint();
}

export async function inventoryNarrow(evaluate: (expression: string) => Promise<unknown>,
  command: (method: string, params?: object) => Promise<unknown>) {
  for (const locale of ["de", "ja"]) for (const theme of ["light", "dark"]) {
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
    await evaluate(`inventoryLanguage('${locale}');document.documentElement.dataset.theme='${theme}'`);
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    assert.equal(await evaluate("document.documentElement.scrollWidth<=innerWidth+2"), true, `${locale}/${theme}: inventory has no horizontal overflow`);
    assert.equal(await evaluate(`new Promise(async resolve=>{for(const control of document.querySelectorAll('main button,main input,main select')){
      control.scrollIntoView({block:'center'});await new Promise(frame=>requestAnimationFrame(frame));
      const r=control.getBoundingClientRect();if(r.width<=0||r.height<=0||r.left<0||r.right>innerWidth+2||r.top<0||r.bottom>innerHeight+2){resolve(false);return;}}
      resolve(true);})`), true, `${locale}/${theme}: every inventory control is scroll-reachable`);
    await evaluate("document.querySelector('main button:not(:disabled),main input')?.focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await evaluate("document.querySelector('main').contains(document.activeElement)"), true);
  }
  await evaluate("inventoryLanguage('en');document.documentElement.dataset.theme='dark'");
  await command("Emulation.clearDeviceMetricsOverride");
  await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
}
