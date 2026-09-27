// Test-only browser expression: observe layout without scrolling or changing owners.
export const timelineGeometryProbe = `(()=>{
  const measure=n=>{const r=n.getBoundingClientRect(),s=getComputedStyle(n);
    const range=document.createRange();range.selectNodeContents(n);
    return {tag:n.tagName,cls:n.className,text:n.textContent?.slice(0,110),top:r.top,height:r.height,width:r.width,
      scroll:n.scrollTop,scrollHeight:n.scrollHeight,clientHeight:n.clientHeight,
      font:s.fontFamily,fontSize:s.fontSize,lineHeight:s.lineHeight,anchor:s.overflowAnchor,
      lines:[...range.getClientRects()].slice(0,12).map(r=>[r.top,r.height,r.width])};};
  const root=document.querySelector('.timeline-scroll');
  const messages=[...root.querySelectorAll('.timeline-message')];
  const sample=new Set([...messages.slice(0,3),...messages.slice(-3),...messages.filter(n=>n.classList.contains('message-tool')).slice(0,2)]);
  const chrome=[...root.querySelectorAll('h2,h3,h4,p,span,summary,button,strong,small,time,dt,dd,label,.section-heading,.banner,.live-indicator,.history,.message-body,.message-heading,.event-detail-body,.event-meta-inline')]
    .filter(n=>!n.closest('.timeline-message')||sample.has(n.closest('.timeline-message')));
  return {locale:document.documentElement.lang,following:root.dataset.following,pausedControl:!!document.querySelector('.timeline-bottom-button'),
    containers:[root,...[root.parentElement,document.querySelector('.outer-scroll'),document.querySelector('.active-session-content')].filter(Boolean)].map(measure),
    chrome:chrome.map(measure)};
})()`;
