window.orbitWorkspace = {
  version: '2.0',
  enablePointerDocking: function (dotnet) {
    let drag = null, resize = null, highlighted = null;
    const snap = 14;
    const canvas = () => document.querySelector('.freeform-canvas');
    const peers = id => [...document.querySelectorAll('.freeform-canvas > [data-panel]')].filter(p => p.dataset.panel !== id);
    const mark = target => { if (highlighted === target) return; highlighted?.classList.remove('dock-target'); highlighted = target; highlighted?.classList.add('dock-target'); };
    const moveSnap = (left, top, width, height, id) => {
      let x = left, y = top, locked = false, surface = canvas().getBoundingClientRect();
      peers(id).forEach(p => {
        const r = p.getBoundingClientRect(), ox = r.left - surface.left, oy = r.top - surface.top;
        [[left, ox, 0], [left + width, ox + r.width, width], [left, ox + r.width, 0], [left + width, ox, width]].forEach(([a,b,offset]) => { if (Math.abs(a-b) < snap) { x = b - offset; locked = true; } });
        [[top, oy, 0], [top + height, oy + r.height, height], [top, oy + r.height, 0], [top + height, oy, height]].forEach(([a,b,offset]) => { if (Math.abs(a-b) < snap) { y = b - offset; locked = true; } });
      });
      return { x: Math.max(0, x), y: Math.max(0, y), locked };
    };
    const sizeSnap = (left, top, width, height, id) => {
      let w = Math.max(220, width), h = Math.max(140, height), locked = false, surface = canvas().getBoundingClientRect();
      peers(id).forEach(p => {
        const r = p.getBoundingClientRect(), xs = [r.left-surface.left, r.right-surface.left], ys = [r.top-surface.top, r.bottom-surface.top];
        xs.forEach(edge => { if (Math.abs(left + w - edge) < snap) { w = edge-left; locked = true; } });
        ys.forEach(edge => { if (Math.abs(top + h - edge) < snap) { h = edge-top; locked = true; } });
      });
      return { width:w, height:h, locked };
    };
    document.addEventListener('pointerdown', e => {
      const panel = e.target.closest('[data-panel]'); if (!panel) return;
      const r = panel.getBoundingClientRect(), s = canvas().getBoundingClientRect();
      if (e.target.closest('.resize-handle')) { resize = { panel, id:panel.dataset.panel, x:e.clientX, y:e.clientY, left:r.left-s.left, top:r.top-s.top, width:r.width, height:r.height }; e.preventDefault(); return; }
      if (!e.target.closest('.drag-handle') || e.button !== 0) return;
      drag = { panel, id:panel.dataset.panel, x:e.clientX, y:e.clientY, left:r.left-s.left, top:r.top-s.top, width:r.width, height:r.height, moved:false };
    });
    document.addEventListener('pointermove', e => {
      if (resize) { const n=sizeSnap(resize.left,resize.top,resize.width+e.clientX-resize.x,resize.height+e.clientY-resize.y,resize.id); resize.panel.style.width=`${n.width}px`; resize.panel.style.height=`${n.height}px`; resize.panel.classList.toggle('snap-locked',n.locked); return; }
      if (!drag) return;
      if (!drag.moved && Math.hypot(e.clientX-drag.x,e.clientY-drag.y)<7) return;
      drag.moved=true; drag.panel.classList.add('pointer-dragging');
      const n=moveSnap(drag.left+e.clientX-drag.x,drag.top+e.clientY-drag.y,drag.width,drag.height,drag.id);
      drag.panel.style.left=`${n.x}px`; drag.panel.style.top=`${n.y}px`; drag.panel.classList.toggle('snap-locked',n.locked);
      const target=document.elementFromPoint(e.clientX,e.clientY)?.closest('[data-panel]'); mark(target && target.dataset.panel!==drag.id ? target : null);
    });
    document.addEventListener('pointerup', e => {
      if (resize) { const item=resize; resize=null; item.panel.classList.remove('snap-locked'); dotnet.invokeMethodAsync('PointerResize',item.id,parseFloat(item.panel.style.width),parseFloat(item.panel.style.height)); return; }
      if (!drag) return;
      const item=drag; drag=null;
      // Preserve the highlighted destination: embedded WebViews can change the
      // hit-test result at pointer release as capture is relinquished.
      const target=highlighted || document.elementFromPoint(e.clientX,e.clientY)?.closest('[data-panel]');
      item.panel.classList.remove('pointer-dragging','snap-locked'); mark(null);
      if (!item.moved) return;
      if (target && target.dataset.panel!==item.id) dotnet.invokeMethodAsync('PointerDockOn',item.id,target.dataset.panel);
      else dotnet.invokeMethodAsync('PointerPosition',item.id,parseFloat(item.panel.style.left),parseFloat(item.panel.style.top));
    });
    document.addEventListener('pointercancel', () => { drag?.panel.classList.remove('pointer-dragging','snap-locked'); resize?.panel.classList.remove('snap-locked'); drag=null; resize=null; mark(null); });
  }
};
