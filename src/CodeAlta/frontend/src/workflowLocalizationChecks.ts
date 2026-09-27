import assert from "node:assert/strict";
import { locales, translate, type MessageKey } from "./localization";

export async function workflowLanguages(evaluate: (expression: string) => Promise<unknown>, calls: string,
  root: string, heading: string, title: MessageKey, literals = "code", identity = "null") {
  const paint = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
  await paint();
  await evaluate(`void(window.workflowKept={root:document.querySelector(${JSON.stringify(root)}),focus:document.activeElement,calls:JSON.stringify(${calls}),identity:${identity},
    controls:[...document.querySelector(${JSON.stringify(root)}).querySelectorAll('input,textarea,select,button')].map(n=>({n,value:n.value,checked:n.checked,disabled:n.disabled})),
    literal:[...document.querySelector(${JSON.stringify(root)}).querySelectorAll(${JSON.stringify(literals)})].map(n=>({n,text:n.textContent}))})`);
  for (const locale of locales) {
    await evaluate(`workflowLanguage('${locale}')`); await paint();
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(heading)})?.textContent===${JSON.stringify(translate(locale, title))}`), true, `${locale}: ${title}`);
    assert.equal(await evaluate(`workflowKept.root===document.querySelector(${JSON.stringify(root)})&&workflowKept.focus===document.activeElement&&workflowKept.calls===JSON.stringify(${calls})&&workflowKept.identity===(${identity})&&
      workflowKept.controls.every(v=>v.n.isConnected&&v.value===v.n.value&&v.checked===v.n.checked&&v.disabled===v.n.disabled)&&
      workflowKept.literal.every(v=>v.n.isConnected&&v.text===v.n.textContent)`), true, `${locale}: ${title} retains focus, controls, exact drafts/confirmation, literals and requests`);
  }
  await evaluate("workflowLanguage('en')"); await paint();
}

export async function workflowNarrow(evaluate: (expression: string) => Promise<unknown>,
  command: (method: string, params?: object) => Promise<unknown>, root: string, prepare?: () => Promise<void>) {
  const size = await evaluate("({width:innerWidth,height:innerHeight})") as { width: number; height: number };
  for (const locale of ["de", "ja"]) for (const theme of ["light", "dark"]) {
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
    await evaluate(`workflowLanguage('${locale}');document.documentElement.dataset.theme='${theme}'`);
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    await prepare?.();
    assert.equal(await evaluate(`(()=>{const d=document.querySelector(${JSON.stringify(root)}),r=d.getBoundingClientRect();return r.width>0&&r.height>0&&!d.closest('[hidden],[inert]')&&getComputedStyle(d).visibility==='visible'&&r.left>=0&&r.right<=innerWidth+1&&r.height<=innerHeight&&d.scrollWidth<=d.clientWidth+1})()`), true, `${locale}/${theme}: visible workflow fits`);
    assert.equal(await evaluate(`(async()=>{for(const control of document.querySelector(${JSON.stringify(root)}).querySelectorAll('button,input')){
      control.scrollIntoView({block:'center'});await new Promise(resolve=>requestAnimationFrame(resolve));const r=control.getBoundingClientRect();
      if(r.width>0&&(r.left<0||r.right>innerWidth+1||r.top<0||r.bottom>innerHeight+1)){
        const describe=n=>{const s=getComputedStyle(n);return {tag:n.tagName,id:n.id,class:n.className,ariaLabel:n.getAttribute('aria-label'),text:n.textContent,
          rect:n.getBoundingClientRect().toJSON(),clientWidth:n.clientWidth,clientHeight:n.clientHeight,scrollTop:n.scrollTop,scrollLeft:n.scrollLeft,
          scrollHeight:n.scrollHeight,scrollWidth:n.scrollWidth,overflowX:s.overflowX,overflowY:s.overflowY,position:s.position};};
        const ancestors=[];for(let n=control.parentElement;n;n=n.parentElement)ancestors.push(describe(n));
        return {control:describe(control),index:Array.from(document.querySelector(${JSON.stringify(root)}).querySelectorAll('button,input')).indexOf(control),
          root:describe(document.querySelector(${JSON.stringify(root)})),ancestors,viewport:{width:innerWidth,height:innerHeight},
          center:{x:r.x+r.width/2,y:r.y+r.height/2},hitStack:document.elementsFromPoint(r.x+r.width/2,r.y+r.height/2).map(describe)};
      }}return true;})()`), true, `${locale}/${theme}: controls are scroll-reachable`);
    await evaluate(`document.querySelector(${JSON.stringify(root)}).querySelector('button:not(:disabled),input:not(:disabled)')?.focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(root)}).contains(document.activeElement)`), true);
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}));document.activeElement.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(root)})`), true, "synthetic IME does not close the dialog");
  }
  await evaluate("workflowLanguage('en');document.documentElement.dataset.theme='dark'");
  await command("Emulation.setDeviceMetricsOverride", { ...size, deviceScaleFactor: 1, mobile: false });
  await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
  await prepare?.();
}
