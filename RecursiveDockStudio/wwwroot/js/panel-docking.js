(function () {
  let reference=null, drag=null, target=null, zone=null, ghost=null, suppressTabClick=false;
  const clear=()=>{target?.classList.remove('dock-stack','dock-left','dock-right','dock-top','dock-bottom','root-dock-top','root-dock-bottom','root-dock-left','root-dock-right');target=null;zone=null;};
  const groupAt=(x,y)=>{const points=document.elementsFromPoint?document.elementsFromPoint(x,y):[document.elementFromPoint(x,y)];return points.map(e=>e?.closest?.('[data-group]')).find(Boolean)||null;};
  const show = (event, text) => {
    if (!ghost) {
      ghost = document.createElement('div');
      ghost.className = 'dock-drag-ghost';
      document.body.appendChild(ghost);
    }
    ghost.textContent = text;
    // Coordinates are viewport-relative, just like the fixed element's origin.
    const x = Math.max(8, Math.min(event.clientX + 14, window.innerWidth - ghost.offsetWidth - 8));
    const y = Math.max(8, Math.min(event.clientY + 14, window.innerHeight - ghost.offsetHeight - 8));
    ghost.style.transform = `translate(${x}px,${y}px)`;
  };
  const hide=()=>{ghost?.remove();ghost=null;};
  const pick=(group,e)=>{const r=group.getBoundingClientRect(),x=(e.clientX-r.left)/r.width,y=(e.clientY-r.top)/r.height,edge=Math.min(x,1-x,y,1-y);if(edge>.24)return 'stack';if(edge===x)return'left';if(edge===1-x)return'right';return edge===y?'top':'bottom';};
  const rootEdge = event => {
    const host = document.querySelector('[data-root-dock]');
    if (!host) return null;
    const rect = host.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) return null;
    const edges = [
      { zone: 'root-top', distance: event.clientY - rect.top },
      { zone: 'root-bottom', distance: rect.bottom - event.clientY },
      { zone: 'root-left', distance: event.clientX - rect.left },
      { zone: 'root-right', distance: rect.right - event.clientX }
    ].sort((first, second) => first.distance - second.distance);
    return edges[0].distance < 36 ? { host, zone: edges[0].zone } : null;
  };
  const begin=e=>{if(drag)return;const panel=e.target.closest?.('[data-drag-panel]');if(!panel||(e.button!==undefined&&e.button!==0))return;if(e.pointerId!==undefined)panel.setPointerCapture?.(e.pointerId);drag={id:panel.dataset.dragPanel,source:panel.closest('[data-group]')?.dataset.group,panel,isTab:true,x:e.clientX,y:e.clientY,moved:false};};
  const move=e=>{if(!drag)return;if(!drag.moved&&Math.hypot(e.clientX-drag.x,e.clientY-drag.y)<5)return;drag.moved=true;drag.panel.classList.add('dragging-tab');const root=rootEdge(e);if(root){if(target!==root.host||zone!==root.zone){clear();target=root.host;zone=root.zone;target.classList.add(`root-dock-${root.zone.substring(5)}`);}show(e,`Drop to dock ${root.zone.substring(5)}`);return;}const group=groupAt(e.clientX,e.clientY);if(!group||group.dataset.group===drag.source){clear();show(e,'Moving panel');return;}const next=pick(group,e);if(target!==group||zone!==next){clear();target=group;zone=next;target.classList.add(`dock-${zone}`);}show(e,next==='stack'?'Drop to stack':`Drop to split ${next}`);};
  const end=()=>{if(!drag)return;const item=drag;drag=null;item.panel.classList.remove('dragging-tab');hide();const destination=target,next=zone;clear();if(item.moved&&item.isTab)suppressTabClick=true;if(item.moved&&destination&&next&&reference){if(next?.startsWith('root-'))reference.invokeMethodAsync('ApplyRootDockDrop',item.id,next.substring(5));else reference.invokeMethodAsync('ApplyDockDrop',item.id,destination.dataset.group,next);}};
  const cancel=()=>{drag?.panel.classList.remove('dragging-tab');drag=null;hide();clear();};
  // Ignore compatibility mousedown events while a pointer drag is already active.
  window.addEventListener('pointerdown',begin,true);window.addEventListener('pointermove',move,true);window.addEventListener('pointerup',end,true);window.addEventListener('pointercancel',cancel,true);
  window.addEventListener('mousedown',begin,true);window.addEventListener('mousemove',move,true);window.addEventListener('mouseup',end,true);
  // The DOM update gives tab selection instant feedback while Blazor persists
  // the active tab in the recursive dock tree through the button callback.
  window.addEventListener('click', event => {
    const tab = event.target.closest?.('[data-tab]');
    if (suppressTabClick && tab) { suppressTabClick = false; event.preventDefault(); event.stopImmediatePropagation(); return; }
    // A drag may not produce a browser click event. Never let its stale flag
    // block a later ribbon or settings button click.
    if (!tab) suppressTabClick = false;
    const ribbonAction = event.target.closest?.('[data-ribbon-action]')?.dataset.ribbonAction;
    if (ribbonAction) {
      const app = document.querySelector('.dock-app');
      app?.classList.toggle('settings-open', ribbonAction === 'settings');
      return;
    }
    if (!tab) return;
    const group = tab.closest('[data-group]');
    if (!group) return;
    group.querySelectorAll('[data-tab]').forEach(item => item.classList.toggle('selected', item === tab));
    group.querySelectorAll('[data-panel-view]').forEach(item => item.classList.toggle('is-active', item.dataset.panelView === tab.dataset.tab));
  }, true);
  window.recursiveDock={setReference:dotnet=>{reference=dotnet;}};
})();
