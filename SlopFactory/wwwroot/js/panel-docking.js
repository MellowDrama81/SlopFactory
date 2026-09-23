(function () {
  let reference=null, drag=null, target=null, zone=null, ghost=null, suppressTabClick=false;
  let modalId=0;
  const showAppModal=({title,message,confirmLabel,initialValue,danger=false})=>new Promise(resolve=>{
    const hasInput=initialValue!==undefined;
    const previousFocus=document.activeElement;
    const dialog=document.createElement('dialog');
    dialog.className=`app-modal${danger?' app-modal-danger':''}`;
    const id=`app-modal-title-${++modalId}`;
    dialog.setAttribute('aria-labelledby',id);
    dialog.innerHTML='<form method="dialog"><div class="app-modal-header"><span class="app-modal-mark" aria-hidden="true"></span><div><p class="app-modal-eyebrow">SLOPFACTORY / ASSETS</p><h2></h2></div></div><p class="app-modal-message"></p><label class="app-modal-field"><span>Name</span><input type="text" required maxlength="255" autocomplete="off" /></label><div class="app-modal-actions"><button type="button" class="app-modal-cancel">Cancel</button><button type="submit" class="app-modal-confirm"></button></div></form>';
    const heading=dialog.querySelector('h2');
    const messageElement=dialog.querySelector('.app-modal-message');
    const field=dialog.querySelector('.app-modal-field');
    const input=dialog.querySelector('input');
    heading.id=id;
    heading.textContent=title;
    messageElement.textContent=message;
    dialog.querySelector('.app-modal-mark').textContent=danger?'!':'✦';
    dialog.querySelector('.app-modal-confirm').textContent=confirmLabel;
    field.hidden=!hasInput;
    input.disabled=!hasInput;
    if(hasInput)input.value=initialValue;
    const finish=value=>{
      dialog.close();
      dialog.remove();
      if(previousFocus?.isConnected)previousFocus.focus();
      resolve(value);
    };
    dialog.querySelector('form').addEventListener('submit',event=>{
      event.preventDefault();
      if(hasInput&&!input.value.trim()){input.setCustomValidity('Enter a name.');input.reportValidity();return;}
      finish(hasInput?input.value.trim():true);
    });
    input.addEventListener('input',()=>input.setCustomValidity(''));
    dialog.querySelector('.app-modal-cancel').addEventListener('click',()=>finish(hasInput?null:false));
    dialog.addEventListener('cancel',event=>{event.preventDefault();finish(hasInput?null:false);});
    dialog.addEventListener('click',event=>{if(event.target===dialog)finish(hasInput?null:false);});
    document.body.appendChild(dialog);
    dialog.showModal();
    if(hasInput){input.focus();input.select();}
    else dialog.querySelector('.app-modal-cancel').focus();
  });
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
  const savedConnectionsKey = 'slopfactory.comfy.connections';
  const currentConnectionKey = 'slopfactory.comfy.currentConnection';
  let overwriteFingerprint = null;
  let selectedConnectionKey = null;
  const getComfyConnections = () => {
    try {
      const saved = JSON.parse(localStorage.getItem(savedConnectionsKey) ?? '{}');
      if (!saved || typeof saved !== 'object' || Array.isArray(saved)) return {};
      if (Object.keys(saved).length) return saved;
      const legacyUrl = localStorage.getItem('slopfactory.comfy.serverUrl');
      if (!legacyUrl) return saved;
      saved[legacyUrl] = { userKey: '', serverUrl: legacyUrl, apiKey: localStorage.getItem('slopfactory.comfy.apiKey') ?? '' };
      localStorage.setItem(savedConnectionsKey, JSON.stringify(saved));
      return saved;
    } catch { return {}; }
  };
  const connectionFields = connection => ({
    key: connection?.querySelector('[data-comfy-connection-key]'),
    serverUrl: connection?.querySelector('[data-comfy-server-url]'),
    apiKey: connection?.querySelector('[data-comfy-api-key]'),
    list: connection?.querySelector('[data-comfy-connection-list]'),
    status: connection?.querySelector('[data-comfy-status]')
  });
  const setComfyStatus = (connection, message, kind) => {
    const status = connectionFields(connection).status;
    if (!status) return;
    status.textContent = message;
    status.className = kind ? `connection-${kind}` : '';
  };
  const populateComfyConnections = (connection, selectedKey = '') => {
    const { list } = connectionFields(connection);
    if (!list) return;
    const connections = getComfyConnections();
    const currentKey = localStorage.getItem(currentConnectionKey);
    const sortedKeys = Object.keys(connections).sort((first, second) => first.localeCompare(second));
    const orderedKeys = currentKey && sortedKeys.includes(currentKey)
      ? [currentKey, ...sortedKeys.filter(key => key !== currentKey)]
      : sortedKeys;
    list.replaceChildren();
    if (!orderedKeys.length) {
      const empty = document.createElement('li');
      empty.className = 'comfy-connection-empty';
      empty.textContent = 'No saved connections yet.';
      list.appendChild(empty);
      return;
    }
    orderedKeys.forEach(key => {
      const item = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'comfy-connection-item';
      button.dataset.comfyConnectionItem = '';
      button.dataset.key = key;
      const label = document.createElement('span');
      label.className = 'comfy-connection-item-label';
      label.textContent = key;
      button.appendChild(label);
      if (key === currentKey) {
        button.classList.add('is-current');
        const badge = document.createElement('span');
        badge.className = 'comfy-connection-current-badge';
        badge.textContent = 'Current';
        button.appendChild(badge);
      }
      if (key === selectedKey) button.classList.add('is-selected');
      item.appendChild(button);
      list.appendChild(item);
    });
  };
  const loadComfyConnection = (connection, key) => {
    const saved = getComfyConnections()[key];
    const fields = connectionFields(connection);
    if (!saved || !fields.serverUrl || !fields.apiKey || !fields.key) return;
    fields.key.value = saved.userKey ?? (key === saved.serverUrl ? '' : key);
    fields.serverUrl.value = saved.serverUrl ?? '';
    fields.apiKey.value = saved.apiKey ?? '';
    selectedConnectionKey = key;
    populateComfyConnections(connection, key);
    overwriteFingerprint = null;
    setComfyStatus(connection, `Loaded “${key}”.`, 'success');
  };
  const restoreComfyConnection = () => {
    try {
      const connection = document.querySelector('[data-comfy-connection]');
      if (!connection) return;
      const connections = getComfyConnections();
      const currentKey = localStorage.getItem(currentConnectionKey);
      const firstKey = Object.keys(connections).sort((first, second) => first.localeCompare(second))[0];
      const selectedKey = currentKey && currentKey in connections ? currentKey : firstKey;
      populateComfyConnections(connection, selectedKey);
      if (selectedKey) loadComfyConnection(connection, selectedKey);
      if (currentKey && selectedKey === currentKey && reference) {
        const saved = connections[currentKey];
        reference.invokeMethodAsync('SetCurrentComfyConnection', saved.serverUrl ?? '', saved.apiKey ?? '', currentKey);
      }
    } catch { /* Local profile storage may be unavailable in a restricted WebView. */ }
  };
  const persistComfyConnection = (connection, userKey, serverUrl, apiKey) => {
    try {
      const key = userKey.trim() || serverUrl.trim();
      if (!key || !serverUrl.trim()) {
        setComfyStatus(connection, 'Enter a ComfyUI API URL before saving.', 'error');
        return false;
      }
      const connections = getComfyConnections();
      const next = { userKey: userKey.trim(), serverUrl: serverUrl.trim(), apiKey };
      const editingSelected = selectedConnectionKey !== null && selectedConnectionKey in connections;
      const existing = connections[key];
      const collidesWithOther = existing && key !== selectedConnectionKey;
      const changed = collidesWithOther && (existing.userKey !== next.userKey || existing.serverUrl !== next.serverUrl || existing.apiKey !== next.apiKey);
      const fingerprint = `${key}\u0000${next.userKey}\u0000${next.serverUrl}\u0000${next.apiKey}`;
      if (changed && overwriteFingerprint !== fingerprint) {
        overwriteFingerprint = fingerprint;
        setComfyStatus(connection, `“${key}” already exists. Save again to replace it.`, 'error');
        return false;
      }
      if (editingSelected && selectedConnectionKey !== key) {
        delete connections[selectedConnectionKey];
        if (localStorage.getItem(currentConnectionKey) === selectedConnectionKey) localStorage.setItem(currentConnectionKey, key);
      }
      connections[key] = next;
      localStorage.setItem(savedConnectionsKey, JSON.stringify(connections));
      overwriteFingerprint = null;
      selectedConnectionKey = key;
      populateComfyConnections(connection, key);
      return true;
    } catch {
      setComfyStatus(connection, 'Could not save the connection in the local app profile.', 'error');
      return false;
    }
  };
  const setProjectStatus = (library, message, kind = '') => {
    const status = library?.querySelector('[data-project-status]');
    if (!status) return;
    status.textContent = message;
    status.className = kind ? `connection-${kind}` : '';
  };
  const renderProjects = (library, projects) => {
    const list = library?.querySelector('[data-project-list]');
    if (!list) return;
    list.replaceChildren();
    if (!projects.length) { list.innerHTML = '<li class="comfy-connection-empty">No projects yet.</li>'; return; }
    projects.forEach(project => {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'comfy-connection-item'; button.dataset.projectItem = '';
      button.dataset.folderPath = project.folderPath; button.dataset.name = project.name ?? '';
      button.textContent = project.displayName || project.folderPath;
      const item = document.createElement('li'); item.appendChild(button); list.appendChild(item);
    });
  };
  const restoreProjects = async () => {
    const library = document.querySelector('[data-project-library]');
    if (!library || !reference) return;
    try { renderProjects(library, JSON.parse(await reference.invokeMethodAsync('GetProjects'))); }
    catch { setProjectStatus(library, 'Could not load projects.', 'error'); }
  };
  const restoreAssetsPicker = async () => {
    const picker = document.querySelector('[data-assets-picker]');
    const list = picker?.querySelector('[data-assets-project-list]');
    if (!picker || !list || !reference) return;
    try {
      const projects = JSON.parse(await reference.invokeMethodAsync('GetProjects'));
      list.replaceChildren();
      if (!projects.length) { list.innerHTML = '<li class="comfy-connection-empty">Add a project before opening assets.</li>'; return; }
      projects.forEach(project => { const item = document.createElement('li'); const button = document.createElement('button'); button.type = 'button'; button.className = 'comfy-connection-item'; button.dataset.assetsProject = ''; button.dataset.folderPath = project.folderPath; button.dataset.name = project.name ?? ''; button.textContent = project.displayName || project.folderPath; item.appendChild(button); list.appendChild(item); });
    } catch { list.innerHTML = '<li class="comfy-connection-empty">Could not load projects.</li>'; }
  };
  let workflowsCache = [];
  let selectedWorkflowId = null;
  const workflowFields = library => ({
    id: library?.querySelector('[data-workflow-id]'),
    displayName: library?.querySelector('[data-workflow-display-name]'),
    description: library?.querySelector('[data-workflow-description]'),
    requiresMask: library?.querySelector('[data-workflow-requires-mask]'),
    minImages: library?.querySelector('[data-workflow-min-images]'),
    maxImages: library?.querySelector('[data-workflow-max-images]'),
    list: library?.querySelector('[data-workflow-list]'),
    status: library?.querySelector('[data-workflow-status]'),
    readonlyNote: library?.querySelector('[data-workflow-readonly-note]'),
    saveButton: library?.querySelector('[data-workflow-action="save"]'),
    deleteButton: library?.querySelector('[data-workflow-action="delete"]')
  });
  const setWorkflowStatus = (library, message, kind) => {
    const status = workflowFields(library).status;
    if (!status) return;
    status.textContent = message;
    status.className = kind ? `connection-${kind}` : '';
  };
  const populateWorkflowList = (library, selectedId = '') => {
    const { list } = workflowFields(library);
    if (!list) return;
    list.replaceChildren();
    if (!workflowsCache.length) {
      const empty = document.createElement('li');
      empty.className = 'comfy-connection-empty';
      empty.textContent = 'No workflows yet.';
      list.appendChild(empty);
      return;
    }
    workflowsCache.forEach(workflow => {
      const item = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'comfy-connection-item';
      button.dataset.workflowItem = '';
      button.dataset.id = workflow.id;
      const label = document.createElement('span');
      label.className = 'comfy-connection-item-label';
      label.textContent = workflow.displayName;
      button.appendChild(label);
      if (workflow.isBuiltIn) {
        button.classList.add('is-current');
        const badge = document.createElement('span');
        badge.className = 'comfy-connection-current-badge';
        badge.textContent = 'Built-in';
        button.appendChild(badge);
      }
      if (workflow.id === selectedId) button.classList.add('is-selected');
      item.appendChild(button);
      list.appendChild(item);
    });
  };
  const applyWorkflowToForm = (library, workflow) => {
    const fields = workflowFields(library);
    if (!fields.id) return;
    fields.id.value = workflow.id;
    fields.displayName.value = workflow.displayName;
    fields.description.value = workflow.description ?? '';
    fields.requiresMask.checked = Boolean(workflow.capabilities?.requiresMask);
    fields.minImages.value = workflow.capabilities?.minImages ?? 0;
    fields.maxImages.value = workflow.capabilities?.maxImages ?? 0;
    [fields.id, fields.displayName, fields.description, fields.requiresMask, fields.minImages, fields.maxImages]
      .forEach(field => { if (field) field.disabled = workflow.isBuiltIn; });
    if (fields.saveButton) fields.saveButton.hidden = workflow.isBuiltIn;
    if (fields.deleteButton) fields.deleteButton.hidden = workflow.isBuiltIn;
    if (fields.readonlyNote) fields.readonlyNote.hidden = !workflow.isBuiltIn;
  };
  const resetWorkflowForm = library => {
    const fields = workflowFields(library);
    selectedWorkflowId = null;
    [fields.id, fields.displayName, fields.description].forEach(field => { if (field) field.value = ''; });
    if (fields.requiresMask) fields.requiresMask.checked = false;
    if (fields.minImages) fields.minImages.value = '1';
    if (fields.maxImages) fields.maxImages.value = '1';
    [fields.id, fields.displayName, fields.description, fields.requiresMask, fields.minImages, fields.maxImages]
      .forEach(field => { if (field) field.disabled = false; });
    if (fields.saveButton) fields.saveButton.hidden = false;
    if (fields.deleteButton) fields.deleteButton.hidden = true;
    if (fields.readonlyNote) fields.readonlyNote.hidden = true;
    populateWorkflowList(library);
  };
  const loadWorkflow = (library, id) => {
    const workflow = workflowsCache.find(item => item.id === id);
    if (!workflow) return;
    selectedWorkflowId = id;
    applyWorkflowToForm(library, workflow);
    populateWorkflowList(library, id);
    setWorkflowStatus(library, `Loaded “${workflow.displayName}”.`, '');
  };
  const uniqueWorkflowId = base => {
    let candidate = base, suffix = 2;
    while (workflowsCache.some(workflow => workflow.id === candidate)) candidate = `${base}-${suffix++}`;
    return candidate;
  };
  const duplicateWorkflow = library => {
    const source = workflowsCache.find(workflow => workflow.id === selectedWorkflowId);
    if (!source) {
      setWorkflowStatus(library, 'Select a workflow to duplicate.', 'error');
      return;
    }
    selectedWorkflowId = null;
    applyWorkflowToForm(library, { ...source, id: uniqueWorkflowId(`${source.id}-copy`), displayName: `${source.displayName} (copy)`, isBuiltIn: false });
    const fields = workflowFields(library);
    if (fields.deleteButton) fields.deleteButton.hidden = true;
    populateWorkflowList(library);
    setWorkflowStatus(library, `Duplicated “${source.displayName}”. Save to add it as a new workflow.`, '');
  };
  const restoreWorkflows = (preferredId = '') => {
    const library = document.querySelector('[data-workflow-library]');
    if (!library || !reference) return;
    reference.invokeMethodAsync('GetWorkflows').then(json => {
      try { workflowsCache = JSON.parse(json) ?? []; } catch { workflowsCache = []; }
      const selectedId = preferredId && workflowsCache.some(workflow => workflow.id === preferredId)
        ? preferredId
        : (workflowsCache[0]?.id ?? '');
      populateWorkflowList(library, selectedId);
      if (selectedId) loadWorkflow(library, selectedId); else resetWorkflowForm(library);
    });
  };
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
  let assetDrag=null;
  const clearAssetDropTargets=()=>document.querySelectorAll('.assets-drop-ready,.assets-external-drop-ready').forEach(panel=>panel.classList.remove('assets-drop-ready','assets-external-drop-ready'));
  const hasExternalFiles=event=>!assetDrag&&Array.from(event.dataTransfer?.types??[]).includes('Files');
  window.addEventListener('dragstart',event=>{
    const asset=event.target.closest?.('[data-assets-drag]');
    if(!asset)return;
    assetDrag={path:asset.dataset.assetsSourcePath??'',root:asset.dataset.assetsSourceRoot??'',element:asset};
    asset.classList.add('asset-dragging');
    event.dataTransfer.effectAllowed='copy';
    event.dataTransfer.setData('text/plain',assetDrag.path);
  },true);
  window.addEventListener('dragover',event=>{
    const panel=event.target.closest?.('[data-assets-drop-target]');
    const external=hasExternalFiles(event);
    if(!assetDrag&&!external)return;
    event.preventDefault();
    if(!panel){clearAssetDropTargets();return;}
    event.dataTransfer.dropEffect='copy';
    clearAssetDropTargets();
    panel.classList.add(external?'assets-external-drop-ready':'assets-drop-ready');
  },true);
  window.addEventListener('dragleave',event=>{
    const panel=event.target.closest?.('[data-assets-drop-target]');
    if(panel&&!panel.contains(event.relatedTarget))panel.classList.remove('assets-drop-ready','assets-external-drop-ready');
  },true);
  window.addEventListener('drop',async event=>{
    const panel=event.target.closest?.('[data-assets-drop-target]');
    const external=hasExternalFiles(event);
    if(!assetDrag&&!external)return;
    event.preventDefault();
    if(!panel){clearAssetDropTargets();return;}
    if(external){
      const files=Array.from(event.dataTransfer?.files??[]);
      const folder=panel.dataset.assetsFolder??'';
      const root=panel.dataset.assetsRoot??'';
      const panelId=panel.dataset.assetsPanel;
      clearAssetDropTargets();
      if(!reference||!files.length)return;
      const status=panel.querySelector('[data-assets-status]');
      if(status)status.textContent=`Adding ${files.length} file${files.length===1?'':'s'}…`;
      let added=0;
      const failures=[];
      for(const file of files){
        try{
          const result=JSON.parse(await reference.invokeMethodAsync('AddDroppedAsset',folder,root,file.name,DotNet.createJSStreamReference(file)));
          if(result.success)added++;
          else failures.push(result.message??`Could not add ${file.name}.`);
        }catch(error){failures.push(`Could not add ${file.name}: ${error?.message??'Unknown error'}`);}
      }
      if(added)await reference.invokeMethodAsync('RefreshAssetsPanels');
      const currentPanel=[...document.querySelectorAll('[data-assets-drop-target]')].find(item=>item.dataset.assetsPanel===panelId);
      const currentStatus=currentPanel?.querySelector('[data-assets-status]');
      if(currentStatus)currentStatus.textContent=failures.length
        ? `${added} added. ${failures.length} failed: ${failures[0]}`
        : `Added ${added} file${added===1?'':'s'}.`;
      return;
    }
    const source=assetDrag;
    clearAssetDropTargets();
    assetDrag=null;
    source.element.classList.remove('asset-dragging');
    const status=panel.querySelector('[data-assets-status]');
    if(!reference)return;
    reference.invokeMethodAsync('CopyAsset',source.path,source.root,panel.dataset.assetsFolder??'',panel.dataset.assetsRoot??'').then(result=>{
      const copied=JSON.parse(result);
      if(status)status.textContent=copied.message??(copied.success?'Asset copied.':'Could not copy asset.');
    }).catch(()=>{if(status)status.textContent='Could not copy asset.';});
  },true);
  window.addEventListener('dragend',()=>{
    assetDrag?.element.classList.remove('asset-dragging');
    assetDrag=null;
    clearAssetDropTargets();
  },true);
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
      const activeTab=ribbonAction==='projects'?'projects':(['workspace','assets','generation','clear-workspace'].includes(ribbonAction)?'workspace':null);
      app?.querySelectorAll('.ribbon-tab').forEach(button=>button.classList.toggle('active',button.dataset.ribbonAction===activeTab));
      app?.classList.toggle('settings-open', ribbonAction === 'settings');
      app?.classList.toggle('workflows-open', ribbonAction === 'workflows');
      app?.classList.toggle('projects-open', ribbonAction === 'projects');
      app?.classList.toggle('assets-picker-open', false);
      if (ribbonAction === 'settings') restoreComfyConnection();
      if (ribbonAction === 'workflows') restoreWorkflows();
      if (ribbonAction === 'projects') restoreProjects();
      if (ribbonAction === 'assets' && reference) reference.invokeMethodAsync('OpenAssetsPanel', '', '');
      if (ribbonAction === 'generation' && reference) reference.invokeMethodAsync('OpenGenerationPanel');
      if (ribbonAction === 'clear-workspace' && reference) reference.invokeMethodAsync('ClearWorkspace');
      return;
    }
    const closePanel = event.target.closest?.('[data-close-panel]');
    if (closePanel && reference) { reference.invokeMethodAsync('ClosePanel', closePanel.dataset.closePanel ?? ''); return; }
    const assetsProject = event.target.closest?.('[data-assets-project-select]');
    if (assetsProject && reference) { reference.invokeMethodAsync('SetAssetsPanelProject', assetsProject.dataset.assetsPanel ?? '', assetsProject.dataset.projectFolder ?? '', assetsProject.dataset.projectName ?? ''); return; }
    const generationProject = event.target.closest?.('[data-generation-project-select]');
    if (generationProject && reference) {
      const status=generationProject.closest('.generation-panel')?.querySelector('[data-generation-status]');
      reference.invokeMethodAsync('SetGenerationPanelProject', generationProject.dataset.generationPanel ?? '', generationProject.dataset.projectFolder ?? '')
        .then(result=>{const selected=JSON.parse(result);if(!selected.success&&status?.isConnected)status.textContent=selected.message??'Could not select project.';})
        .catch(()=>{if(status?.isConnected)status.textContent='Could not select project.';});
      return;
    }
    const generationChange = event.target.closest?.('[data-generation-change-project]');
    if (generationChange && reference) { reference.invokeMethodAsync('ClearGenerationPanelProject', generationChange.dataset.generationPanel ?? ''); return; }
    const assetsAdd = event.target.closest?.('[data-assets-add]');
    if (assetsAdd && reference) {
      const status = assetsAdd.closest('.assets-panel')?.querySelector('[data-assets-status]');
      assetsAdd.disabled = true;
      reference.invokeMethodAsync('AddAssets', assetsAdd.dataset.assetsFolder ?? '', assetsAdd.dataset.assetsRoot ?? '').then(result => {
        const added = JSON.parse(result);
        if (status) status.textContent = added.message ?? (added.success ? 'Assets added.' : 'Could not add assets.');
      }).catch(() => { if (status) status.textContent = 'Could not add assets.'; }).finally(() => { assetsAdd.disabled = false; });
      return;
    }
    const assetsCreateFolder = event.target.closest?.('[data-assets-create-folder]');
    if (assetsCreateFolder && reference) {
      const status = assetsCreateFolder.closest('.assets-panel')?.querySelector('[data-assets-status]');
      showAppModal({title:'Create folder',message:'Add a folder to the current assets location.',confirmLabel:'Create folder',initialValue:''}).then(name=>{
        if(name===null)return;
        reference.invokeMethodAsync('CreateAssetsFolder', assetsCreateFolder.dataset.assetsFolder ?? '', name).then(result => {
          const created = JSON.parse(result);
          if (status?.isConnected) status.textContent = created.message ?? (created.success ? 'Folder created.' : 'Could not create folder.');
        }).catch(() => { if (status?.isConnected) status.textContent = 'Could not create folder.'; });
      });
      return;
    }
    const assetsNavigate = event.target.closest?.('[data-assets-navigate]');
    if (assetsNavigate && reference) { reference.invokeMethodAsync('NavigateAssetsPanel', assetsNavigate.dataset.assetsPanel ?? '', assetsNavigate.dataset.assetsFolder ?? ''); return; }
    const projectItem = event.target.closest?.('[data-project-item]');
    if (projectItem) { const library = projectItem.closest('[data-project-library]'); library?.querySelectorAll('[data-project-item]').forEach(item => item.classList.toggle('is-selected', item === projectItem)); const folder = library?.querySelector('[data-project-folder]'); const name = library?.querySelector('[data-project-name]'); if (folder) { folder.value = projectItem.dataset.folderPath ?? ''; folder.readOnly = true; } if (name) name.value = projectItem.dataset.name ?? ''; return; }
    const projectAction = event.target.closest?.('[data-project-action]')?.dataset.projectAction;
    if (projectAction && reference) {
      const library = event.target.closest('[data-project-library]'); const folder = library?.querySelector('[data-project-folder]'); const name = library?.querySelector('[data-project-name]');
      if (projectAction === 'new') { if (folder) { folder.value = ''; folder.readOnly = false; } if (name) name.value = ''; setProjectStatus(library, 'Choose or create a folder for the new project.'); return; }
      if (projectAction === 'pick') {
        reference.invokeMethodAsync('PickProjectFolder').then(path => {
          if (!path) return;
          if (folder) folder.value = path;
          reference.invokeMethodAsync('SaveProject', path, name?.value ?? '', false).then(result => {
            const saved = JSON.parse(result);
            setProjectStatus(library, saved.message ?? (saved.success ? 'Project added.' : 'Could not add project.'), saved.success ? 'success' : 'error');
            if (saved.success) { if (folder) folder.readOnly = true; restoreProjects(); }
          });
        });
        return;
      }
      if (projectAction === 'remove') {
        const selected = library?.querySelector('[data-project-item].is-selected');
        if (!selected || !folder?.value) { setProjectStatus(library, 'Select a project to remove.', 'error'); return; }
        reference.invokeMethodAsync('RemoveProject', folder.value).then(result => {
          const removed = JSON.parse(result);
          setProjectStatus(library, removed.message ?? (removed.success ? 'Project removed from this app.' : 'Could not remove project.'), removed.success ? 'success' : 'error');
          if (removed.success) { folder.value = ''; folder.readOnly = true; if (name) name.value = ''; restoreProjects(); }
        });
        return;
      }
      if (projectAction === 'export') {
        const selected = library?.querySelector('[data-project-item].is-selected');
        if (!selected || !folder?.value) { setProjectStatus(library, 'Select a project to export.', 'error'); return; }
        reference.invokeMethodAsync('ExportProject', folder.value).then(result => {
          const exported = JSON.parse(result);
          setProjectStatus(library, exported.message ?? (exported.success ? 'Project exported.' : 'Could not export project.'), exported.success ? 'success' : 'error');
        });
        return;
      }
      const create = projectAction === 'create';
      reference.invokeMethodAsync('SaveProject', folder?.value ?? '', name?.value ?? '', create).then(result => { const saved = JSON.parse(result); setProjectStatus(library, saved.message ?? (saved.success ? 'Project saved.' : 'Could not save project.'), saved.success ? 'success' : 'error'); if (saved.success) { if (folder) folder.readOnly = true; restoreProjects(); } });
      return;
    }
    const assetsExportSelected = event.target.closest?.('[data-assets-export-selected]');
    const assetsSelectAll = event.target.closest?.('[data-assets-select-all]');
    if (assetsSelectAll) { assetsSelectAll.closest('.assets-panel')?.querySelectorAll('[data-asset-select]').forEach(item => { item.checked = true; }); return; }
    if (assetsExportSelected && reference) {
      const panel = assetsExportSelected.closest('.assets-panel'); const selected = [...(panel?.querySelectorAll('[data-asset-select]:checked') ?? [])].map(item => item.dataset.assetPath ?? ''); const status = panel?.querySelector('[data-assets-status]');
      if (!selected.length) { if (status) status.textContent = 'Select assets to export.'; return; }
      reference.invokeMethodAsync('ExportAssets', selected).then(result => { const exported = JSON.parse(result); if (status) status.textContent = exported.message ?? (exported.success ? 'Selected assets exported.' : 'Could not export assets.'); });
      return;
    }
    const assetsDeleteSelected = event.target.closest?.('[data-assets-delete-selected]');
    if (assetsDeleteSelected && reference) {
      const panel = assetsDeleteSelected.closest('.assets-panel'); const selected = [...(panel?.querySelectorAll('[data-asset-select]:checked') ?? [])]; const status = panel?.querySelector('[data-assets-status]');
      if (!selected.length) { if (status) status.textContent = 'Select assets to delete.'; return; }
      showAppModal({title:'Delete selected items?',message:`Delete ${selected.length} selected item${selected.length===1?'':'s'}? Folders and their contents will be permanently removed. This cannot be undone.`,confirmLabel:'Delete selected',danger:true}).then(confirmed=>{
        if(!confirmed)return;
        Promise.all(selected.map(item => reference.invokeMethodAsync('DeleteAsset', item.dataset.assetPath ?? '', item.dataset.assetsRoot ?? ''))).then(results => {
          const failures=results.map(result=>JSON.parse(result)).filter(result=>!result.success);
          if(status?.isConnected)status.textContent=failures.length?failures[0].message??'Some items could not be deleted.':'Selected items deleted.';
        }).catch(()=>{if(status?.isConnected)status.textContent='Could not delete selected items.';});
      });
      return;
    }
    const assetsRename = event.target.closest?.('[data-assets-rename]');
    if (assetsRename && reference) {
      const assetPath = assetsRename.dataset.assetPath ?? '';
      const currentName = assetPath.split(/[\\/]/).pop() ?? '';
      const panel = assetsRename.closest('.assets-panel');
      const status = panel?.querySelector('[data-assets-status]');
      showAppModal({title:'Rename item',message:'Choose a new name for this asset or folder.',confirmLabel:'Rename',initialValue:currentName}).then(newName=>{
        if (newName===null || newName===currentName)return;
        assetsRename.disabled = true;
        reference.invokeMethodAsync('RenameAsset', assetPath, assetsRename.dataset.assetsRoot ?? '', newName, panel?.dataset.assetsPanel ?? '').then(result => {
          const renamed = JSON.parse(result);
          if (status?.isConnected) status.textContent = renamed.message ?? (renamed.success ? 'Renamed.' : 'Could not rename asset.');
        }).catch(() => { if (status?.isConnected) status.textContent = 'Could not rename asset.'; })
          .finally(() => { if (assetsRename.isConnected) assetsRename.disabled = false; });
      });
      return;
    }
    const assetsExport = event.target.closest?.('[data-assets-export]');
    if (assetsExport && reference) {
      const panel = assetsExport.closest('.assets-panel'); const status = panel?.querySelector('[data-assets-status]');
      reference.invokeMethodAsync('ExportAsset', assetsExport.dataset.assetPath ?? '').then(result => { const exported = JSON.parse(result); if (status) status.textContent = exported.message ?? (exported.success ? 'Asset exported.' : 'Could not export asset.'); });
      return;
    }
    const assetsDelete = event.target.closest?.('[data-assets-delete]');
    if (assetsDelete && reference) {
      const panel = assetsDelete.closest('.assets-panel'); const status = panel?.querySelector('[data-assets-status]');
      const name=(assetsDelete.dataset.assetPath??'').split(/[\\/]/).pop()??'this item';
      showAppModal({title:'Delete item?',message:`Delete “${name}”? Folders and their contents will be permanently removed. This cannot be undone.`,confirmLabel:'Delete',danger:true}).then(confirmed=>{
        if(!confirmed)return;
        reference.invokeMethodAsync('DeleteAsset', assetsDelete.dataset.assetPath ?? '', assetsDelete.dataset.assetsRoot ?? '').then(result => { const deleted = JSON.parse(result); if (status?.isConnected) status.textContent = deleted.message ?? (deleted.success ? 'Asset deleted.' : 'Could not delete asset.'); })
          .catch(()=>{if(status?.isConnected)status.textContent='Could not delete item.';});
      });
      return;
    }
    if (event.target.closest?.('[data-asset-select]')) return;
    const assetFile = event.target.closest?.('[data-assets-open-details]');
    if (assetFile?.dataset.assetsOpenDetails === 'file' && reference) {
      const panel = assetFile.closest('.assets-panel');
      const status = panel?.querySelector('[data-assets-status]');
      reference.invokeMethodAsync('OpenAssetDetails', assetFile.dataset.assetPath ?? '', assetFile.dataset.assetsSourceRoot ?? '', panel?.dataset.assetsPanel ?? '').then(result => {
        const opened = JSON.parse(result);
        if (!opened.success && status) status.textContent = opened.message ?? 'Could not open asset details.';
      }).catch(() => { if (status) status.textContent = 'Could not open asset details.'; });
      return;
    }
    const workflowItem = event.target.closest?.('[data-workflow-item]');
    if (workflowItem) {
      loadWorkflow(workflowItem.closest('[data-workflow-library]'), workflowItem.dataset.id);
      return;
    }
    const workflowAction = event.target.closest?.('[data-workflow-action]')?.dataset.workflowAction;
    if (workflowAction === 'new') {
      const library = event.target.closest('[data-workflow-library]');
      resetWorkflowForm(library);
      setWorkflowStatus(library, 'Enter details for a new workflow.', '');
      return;
    }
    if (workflowAction === 'duplicate') {
      duplicateWorkflow(event.target.closest('[data-workflow-library]'));
      return;
    }
    if (workflowAction === 'save' && reference) {
      const library = event.target.closest('[data-workflow-library]');
      const fields = workflowFields(library);
      const id = fields.id?.value ?? '';
      const displayName = fields.displayName?.value ?? '';
      const description = fields.description?.value ?? '';
      const requiresMask = Boolean(fields.requiresMask?.checked);
      const minImages = Number(fields.minImages?.value ?? 0);
      const maxImages = Number(fields.maxImages?.value ?? 0);
      reference.invokeMethodAsync('SaveWorkflow', id, displayName, description, requiresMask, minImages, maxImages)
        .then(json => {
          const result = JSON.parse(json);
          if (!result.success) { setWorkflowStatus(library, result.message, 'error'); return; }
          setWorkflowStatus(library, `Saved “${result.workflow.displayName}”.`, 'success');
          restoreWorkflows(result.workflow.id);
        });
      return;
    }
    if (workflowAction === 'delete' && reference) {
      const library = event.target.closest('[data-workflow-library]');
      if (!selectedWorkflowId) return;
      reference.invokeMethodAsync('DeleteWorkflow', selectedWorkflowId).then(json => {
        const result = JSON.parse(json);
        if (!result.success) { setWorkflowStatus(library, result.message, 'error'); return; }
        setWorkflowStatus(library, 'Workflow removed.', 'success');
        restoreWorkflows();
      });
      return;
    }
    const connectionItem = event.target.closest?.('[data-comfy-connection-item]');
    if (connectionItem) {
      loadComfyConnection(connectionItem.closest('[data-comfy-connection]'), connectionItem.dataset.key);
      return;
    }
    const comfyAction = event.target.closest?.('[data-comfy-action]')?.dataset.comfyAction;
    if (comfyAction === 'new') {
      const connection = event.target.closest('[data-comfy-connection]');
      const fields = connectionFields(connection);
      if (fields.key) fields.key.value = '';
      if (fields.serverUrl) fields.serverUrl.value = '';
      if (fields.apiKey) fields.apiKey.value = '';
      selectedConnectionKey = null;
      populateComfyConnections(connection);
      overwriteFingerprint = null;
      setComfyStatus(connection, 'Enter details for a new connection.', '');
      return;
    }
    if (comfyAction === 'use-current' && reference) {
      const connection = event.target.closest('[data-comfy-connection]');
      const fields = connectionFields(connection);
      const serverUrl = fields.serverUrl?.value ?? '';
      const apiKey = fields.apiKey?.value ?? '';
      const key = (fields.key?.value ?? '').trim() || serverUrl.trim();
      const saved = getComfyConnections()[key];
      if (!key || !saved || saved.serverUrl !== serverUrl.trim() || saved.apiKey !== apiKey) {
        setComfyStatus(connection, 'Save this connection before making it current.', 'error');
        return;
      }
      try {
        localStorage.setItem(currentConnectionKey, key);
        populateComfyConnections(connection, key);
        setComfyStatus(connection, `“${key}” is now the current connection.`, 'success');
        reference.invokeMethodAsync('SetCurrentComfyConnection', serverUrl, apiKey, key)
          .then(() => populateComfyConnections(connection, key));
      } catch {
        setComfyStatus(connection, 'Could not set the current connection.', 'error');
      }
      return;
    }
    if ((comfyAction === 'test' || comfyAction === 'save') && reference) {
      const connection = event.target.closest('[data-comfy-connection]');
      const fields = connectionFields(connection);
      const serverUrl = fields.serverUrl?.value ?? '';
      const apiKey = fields.apiKey?.value ?? '';
      const key = fields.key?.value ?? '';
      if (comfyAction === 'save' && !persistComfyConnection(connection, key, serverUrl, apiKey)) return;
      if (comfyAction === 'test') reference.invokeMethodAsync('TestComfyConnection', serverUrl, apiKey);
      else {
        reference.invokeMethodAsync('SaveComfyConnection', serverUrl, apiKey, key);
        const savedKey = key.trim() || serverUrl.trim();
        if (localStorage.getItem(currentConnectionKey) === savedKey)
          reference.invokeMethodAsync('SetCurrentComfyConnection', serverUrl, apiKey, savedKey);
      }
      return;
    }
    if (!tab) return;
    const group = tab.closest('[data-group]');
    if (!group) return;
    group.querySelectorAll('[data-tab]').forEach(item => item.classList.toggle('selected', item === tab));
    group.querySelectorAll('[data-panel-view]').forEach(item => item.classList.toggle('is-active', item.dataset.panelView === tab.dataset.tab));
  }, true);
  window.recursiveDock={setReference:dotnet=>{
    reference=dotnet;
    try {
      const key=localStorage.getItem(currentConnectionKey);
      const saved=key ? getComfyConnections()[key] : null;
      if (saved) reference.invokeMethodAsync('SetCurrentComfyConnection',saved.serverUrl ?? '',saved.apiKey ?? '',key);
    } catch { /* Use the connection already restored by .NET preferences. */ }
  },setSplitRatio:(id,ratio)=>reference?.invokeMethodAsync('SetSplitRatio',id,ratio),imageDimensions:image=>[image.naturalWidth,image.naturalHeight]};
})();
