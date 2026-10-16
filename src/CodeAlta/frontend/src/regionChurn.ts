/**
 * What NeoAstra adopts in the page on Windows: an element marked `data-neoastra-drag-region` moves the window, and the
 * controls inside it do not. A test page that shows the title bar outside the application adopts the same rules.
 */
export const dragRegionRules = "[data-neoastra-drag-region]{app-region:drag;user-select:none}"
  + "[data-neoastra-drag-region] :is(a,button,input,select,textarea,summary,label,[role=button],[contenteditable],[data-neoastra-no-drag]){app-region:no-drag}";

/**
 * A script for a test page, or for the page of a running window: over `milliseconds`, the frames in which a box of a
 * region of the window (`app-region: drag` or `no-drag`, inherited by everything inside) moved or changed size, with
 * its transform. Chromium sends every region to the window again after such a frame, and on Windows the host then
 * spends kernel time at each one (doc/development-guide.md): what animates there must change no box.
 */
export function regionChurnScript(milliseconds = 1000): string {
  return `(async () => {
    const regions = [...document.querySelectorAll('*')].filter(element => ['drag', 'no-drag'].includes(getComputedStyle(element).getPropertyValue('app-region')));
    const read = () => regions.map(element => { const box = element.getBoundingClientRect(); return [box.x, box.y, box.width, box.height].map(value => value.toFixed(2)).join(','); }).join('|');
    let last = read(), frames = 0, moved = 0;
    const end = performance.now() + ${milliseconds};
    while (performance.now() < end) { await new Promise(requestAnimationFrame); frames++; const now = read(); if (now !== last) moved++; last = now; }
    return { regions: regions.length, frames, moved };
  })()`;
}
