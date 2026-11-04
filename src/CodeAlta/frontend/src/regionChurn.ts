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
 * spends kernel time at each one (doc/development-guide.md): what animates there must change no box. A page whose frames
 * stall (a loaded machine) is measured on until it painted `minimumFrames` frames too, at most ten times as long.
 */
export function regionChurnScript(milliseconds = 1000, minimumFrames = 30): string {
  return `(async () => {
    const regions = [...document.querySelectorAll('*')].filter(element => ['drag', 'no-drag'].includes(getComputedStyle(element).getPropertyValue('app-region')));
    const read = () => regions.map(element => { const box = element.getBoundingClientRect(); return [box.x, box.y, box.width, box.height].map(value => value.toFixed(2)).join(','); });
    const name = element => element.tagName.toLowerCase() + (element.id ? '#' + element.id : '') + [...element.classList].slice(0, 3).map(value => '.' + value).join('');
    let last = read(), frames = 0, moved = 0;
    const changes = [];
    const start = performance.now(), end = start + ${milliseconds}, latest = start + ${milliseconds * 10};
    while ((performance.now() < end || frames < ${minimumFrames}) && performance.now() < latest) {
      await new Promise(requestAnimationFrame); frames++;
      const now = read();
      const changed = now.flatMap((box, index) => box === last[index] ? [] : [{ frame: frames, region: name(regions[index]), from: last[index], to: box }]);
      if (changed.length > 0) { moved++; if (changes.length < 5) changes.push(...changed.slice(0, 5 - changes.length)); }
      last = now;
    }
    // What moved, for the message of a test that fails: the region and its box before and after.
    return { regions: regions.length, frames, moved, changes };
  })()`;
}
