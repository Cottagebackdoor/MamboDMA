(function(){
  const CANVAS = document.getElementById("radar");
  const CTX    = CANVAS.getContext("2d");
  const TOOLTIP = document.getElementById("tooltip");

  // --------- UI refs ---------
  const toolbarEl   = document.getElementById("toolbar");
  const collapseBtn = document.getElementById("collapseBtn");
  const tabs        = Array.from(document.querySelectorAll(".tabs .tab"));
  const tabPages    = {
    general: document.getElementById("tab-general"),
    players: document.getElementById("tab-players"),
    loot: document.getElementById("tab-loot"),
  };

  const mapSelEl    = document.getElementById("mapSel");
  const btnRescan   = document.getElementById("btnRescan");
  const serverUrlEl = document.getElementById("serverUrl");
  const btnConn     = document.getElementById("btnConnect");
  const btnSaveMap  = document.getElementById("btnSaveMap");
  const statusEl    = document.getElementById("status");

  // General controls
  const zoomEl      = document.getElementById("zoom");
  const inScaleEl   = document.getElementById("inScale");
  const yFlipEl     = document.getElementById("yFlip");

  // Player settings
  const fSelfEl    = document.getElementById("fSelf");
  const fPMCsEl    = document.getElementById("fPMCs");
  const fBotsEl    = document.getElementById("fBots");
  const fPlayersEl = document.getElementById("fPlayers");
  const fZombiesEl = document.getElementById("fZombies");
  const fDeadEl    = document.getElementById("fDead");
  const maxRangeEl = document.getElementById("maxRange");
  const szSelfEl   = document.getElementById("szSelf");
  const szPMCEl    = document.getElementById("szPMC");
  const szBotEl    = document.getElementById("szBot");
  const szDeadEl   = document.getElementById("szDead");
  const colSelfEl  = document.getElementById("colSelf");
  const colPMCEl   = document.getElementById("colPMC");
  const colBotEl   = document.getElementById("colBot");
  const colDeadEl  = document.getElementById("colDead");

  // Loot settings
  const fLootEl = document.getElementById("fLoot");
  const fContainersEl = document.getElementById("fContainers");
  const fEmptyContainersEl = document.getElementById("fEmptyContainers");
  const lootMaxRangeEl = document.getElementById("lootMaxRange");
  const lootMinPriceEl = document.getElementById("lootMinPrice");
  const szLootEl = document.getElementById("szLoot");
  const szContainerEl = document.getElementById("szContainer");
  const colLootEl = document.getElementById("colLoot");
  const colLootImportantEl = document.getElementById("colLootImportant");
  const colContainerFilledEl = document.getElementById("colContainerFilled");
  const colContainerEmptyEl = document.getElementById("colContainerEmpty");
  const colContainerImportantEl = document.getElementById("colContainerImportant");
  const lootImportantThresholdEl = document.getElementById("lootImportantThreshold");

  let dpr = window.devicePixelRatio || 1;

  // Connection state
  let es=null, pollTimer=null;
  let lastFrame = null;
  let frameCount = 0; // DEBUG COUNTER
  let currentGame = "abi"; // gates per-game UI; updated from /status

  // Map state
  let mapList = [];
  let activeMap = null;
  let mapImg = new Image();
  let imgPan = { x:0, y:0 };
  // Tracks server's selectedMap to detect transitions; null means "no server preference yet".
  let lastServerSelectedMap = null;

  // --------- CALIBRATION STATE ---------
  let currentSession = null;
  let calibrationOffset = { x: 0, y: 0 };
  let isCalibrated = false;
  let calibrationMode = false;

  // --------- HOVER STATE ---------
  let hoveredEntity = null;

  // --------- BOGUS POSITION FILTER ---------
  function isBogusPosition(x, y, z) {
    if (x === 0 && y === 0) return true;
    if (Math.abs(x) < 10 && Math.abs(y) < 10) return true;
    if (!isFinite(x) || !isFinite(y) || !isFinite(z)) return true;
    return false;
  }

  // --------- Per-map config ---------
  const cfgKey = (name)=> `wr.mapcfg.${name}`;
  const defaultCfg = ()=> ({
    inScale: 33.120,
    zoom: 0.5,
    yFlip: false,
    file: null,
  });

  function mergeCfg(base, override){
    if (!override) return base;
    const out = { ...base };
    if (typeof override.inScale === "number" && isFinite(override.inScale) && override.inScale > 0) out.inScale = override.inScale;
    if (typeof override.zoom === "number"   && isFinite(override.zoom))                               out.zoom    = override.zoom;
    if (typeof override.yFlip === "boolean")                                                         out.yFlip   = override.yFlip;
    if (typeof override.file === "string") out.file = override.file;
    return out;
  }

  function loadCfg(name){
    try{
      const raw = localStorage.getItem(cfgKey(name));
      const cfg = raw ? JSON.parse(raw) : defaultCfg();
      if (!(cfg.inScale > 0)) cfg.inScale = 33.120;
      if (typeof cfg.zoom !== "number") cfg.zoom = 0.5;
      if (typeof cfg.yFlip !== "boolean") cfg.yFlip = false;
      return cfg;
    }catch{ return defaultCfg(); }
  }
  
  function saveCfg(name, cfg){
    try{ localStorage.setItem(cfgKey(name), JSON.stringify(cfg)); }catch{}
  }

  function urlDirname(url){
    const q = url.split("?")[0].split("#")[0];
    const idx = q.lastIndexOf("/");
    return idx >= 0 ? q.slice(0, idx) : "";
  }
  
  function joinUrl(dir, file){
    if (!dir) return `/${file.replace(/^\/+/,"")}`;
    return `${dir.replace(/\/+$/,"")}/${file.replace(/^\/+/,"")}`;
  }

  function applyGameMode(game){
    const g = (game === "dayz") ? "dayz" : "abi"; // unknown/missing falls back to ABI for compat
    if (g !== currentGame) lastServerSelectedMap = null; // game transition invalidates cached server map
    currentGame = g;
    document.body.classList.remove("game-abi", "game-dayz");
    document.body.classList.add("game-" + g);
    // Map dropdown is server-controlled in DayZ; ABI keeps user picker.
    if (mapSelEl) mapSelEl.disabled = (g === "dayz");
  }

  // Browser follows server's selectedMap so DayZ "Map" panel is single source of truth.
  function syncServerSelectedMap(frame){
    if (!frame || currentGame !== "dayz") return;
    const sel = frame.selectedMap;
    if (typeof sel !== "string" || !sel) return;
    if (sel === lastServerSelectedMap) return;
    // Don't cache until we've actually committed to a switch, otherwise a mapList-not-ready-yet frame poisons the cache and later valid frames are suppressed.
    const match = mapList.find(m => m.name.toLowerCase() === sel.toLowerCase());
    if (!match) return;
    if (activeMap && activeMap.name === match.name) { lastServerSelectedMap = sel; return; }
    lastServerSelectedMap = sel;
    if (mapSelEl) mapSelEl.value = match.name;
    setActiveMapByName(match.name).then(()=> drawFrame(lastFrame));
  }

  async function fetchGameMode(){
    try {
      const r = await fetch(baseUrl() + "/status", { cache:"no-store" });
      if (!r.ok) return;
      const j = await r.json();
      applyGameMode(j && j.game);
    } catch {}
  }

  async function fetchJsonNoThrow(url, init){
    try{
      const r = await fetch(url, Object.assign({ cache:"no-store" }, init || {}));
      if (!r.ok) return null;
      return await r.json();
    }catch{ return null; }
  }

  async function loadSidecarForMap(mapMeta){
    if (!mapMeta) return null;
    const url = mapMeta.url || ("/" + (mapMeta.file || ""));
    const dir = urlDirname(url);
    const name = mapMeta.name;

    // Generic sidecar route delegates to the active game's frame source (DayZ translates schema; ABI reads file).
    const sidecar = await fetchJsonNoThrow(`/api/mapsidecar/${encodeURIComponent(name)}`);
    if (sidecar) return { cfg: sidecar, source: `/api/mapsidecar/${name}` };

    const mapCfgUrl = joinUrl(dir, `${name}.json`);
    const exact = await fetchJsonNoThrow(mapCfgUrl);
    if (exact) return { cfg: exact, source: mapCfgUrl };

    const defaultUrl = joinUrl(dir, `default.json`);
    const def = await fetchJsonNoThrow(defaultUrl);
    if (def) return { cfg: def, source: defaultUrl };

    return null;
  }

  // --------- Canvas & view ---------
  function resizeCanvas(){
    dpr = window.devicePixelRatio || 1;
    const rect = CANVAS.getBoundingClientRect();
    CANVAS.width  = Math.max(1, Math.round(rect.width  * dpr));
    CANVAS.height = Math.max(1, Math.round(rect.height * dpr));
    CTX.setTransform(1,0,0,1,0,0);
  }

  function imageDrawRect(v){
    const sw = v.imgW, sh = v.imgH;
    const cover = Math.max(v.canvasW / sw, v.canvasH / sh);
    const s  = cover * v.zoom;
    const dw = sw * s, dh = sh * s;
    const dx = v.cx - dw/2 + imgPan.x;
    const dy = v.cy - dh/2 + imgPan.y;
    return { dx, dy, s, dw, dh };
  }

  function readView(){
    const cfg = activeMap ? loadCfg(activeMap.name) : defaultCfg();
    const z = clamp(parseFloat(zoomEl?.value ?? cfg.zoom) || cfg.zoom, 0.05, 10);
    const scl = Math.max(0.001, parseFloat(inScaleEl?.value ?? cfg.inScale) || cfg.inScale);
    
    return {
      zoom: z,
      canvasW: CANVAS.width,
      canvasH: CANVAS.height,
      cx: CANVAS.width * 0.5,
      cy: CANVAS.height * 0.5,
      imgW: mapImg.naturalWidth  || 1,
      imgH: mapImg.naturalHeight || 1,
      inScale: scl,
      yFlip: cfg.yFlip,
      worldOffsetCm: { x: calibrationOffset.x, y: calibrationOffset.y }
    };
  }

  // --------- CLICK-TO-CALIBRATE ---------
  function enterCalibrationMode(){
    calibrationMode = true;
    statusFlash("Click your position on the map to calibrate");
    CANVAS.style.cursor = "crosshair";
    console.log("[WebRadar] Calibration mode: Click where you are on the map");
    console.log("[WebRadar] Current lastFrame:", lastFrame);
    console.log("[WebRadar] lastFrame.self:", lastFrame?.self);
  }

  function exitCalibrationMode(){
    calibrationMode = false;
    CANVAS.style.cursor = "grab";
    statusFlash("Calibration mode exited");
  }

  function calibrateFromClick(clickImgX, clickImgY){
    console.log("[WebRadar] ?? Calibrate click at:", clickImgX, clickImgY);
    console.log("[WebRadar] lastFrame state:", {
      exists: !!lastFrame,
      ok: lastFrame?.ok,
      hasSelf: !!lastFrame?.self,
      self: lastFrame?.self
    });
    
    if (!lastFrame || !lastFrame.self) {
      statusFlash("? No player data - wait for connection");
      console.error("[WebRadar] Cannot calibrate: lastFrame or self missing");
      return;
    }
    
    const cfg = activeMap ? loadCfg(activeMap.name) : defaultCfg();
    const scale = cfg.inScale;
    
    let offsetX = clickImgX * scale - lastFrame.self.x;
    let offsetY = clickImgY * scale - lastFrame.self.y;
    
    if (cfg.yFlip) {
      offsetY = -(clickImgY * scale) - lastFrame.self.y;
    }
    
    calibrationOffset.x = offsetX;
    calibrationOffset.y = offsetY;
    isCalibrated = true;
    
    console.log(`[WebRadar] ? Calibrated by click:`);
    console.log(`  World pos: (${lastFrame.self.x.toFixed(0)}, ${lastFrame.self.y.toFixed(0)})`);
    console.log(`  Clicked image pos: (${clickImgX.toFixed(0)}, ${clickImgY.toFixed(0)})`);
    console.log(`  Offset: (${offsetX.toFixed(0)}, ${offsetY.toFixed(0)})`);
    
    statusFlash(`? Calibrated to (${clickImgX.toFixed(0)}, ${clickImgY.toFixed(0)})`);
    exitCalibrationMode();
    drawFrame(lastFrame);
  }

  // --------- Mapping math ---------
  function worldToImagePx(x, y, v){
    const ox = v.worldOffsetCm?.x || 0;
    const oy = v.worldOffsetCm?.y || 0;
    const sx = (x + ox) / v.inScale;
    const syRaw = (y + oy) / v.inScale;
    const sy = v.yFlip ? -syRaw : syRaw;
    return [ sx, sy ];
  }

  // --------- Drawing helpers ---------
  function drawSelfMarker(x,y,r,deg,col){
    const a = (deg * Math.PI / 180);
    const rr = r*dpr;
    const p0 = { x: x + Math.sin(a) * rr,            y: y - Math.cos(a) * rr            };
    const p1 = { x: x - Math.cos(a) * rr * 0.7,      y: y - Math.sin(a) * rr * 0.7      };
    const p2 = { x: x + Math.cos(a) * rr * 0.7,      y: y + Math.sin(a) * rr * 0.7      };
    CTX.fillStyle = col;
    CTX.beginPath(); CTX.moveTo(p0.x,p0.y); CTX.lineTo(p1.x,p1.y); CTX.lineTo(p2.x,p2.y); CTX.closePath(); CTX.fill();
    CTX.lineWidth = 1*dpr; CTX.strokeStyle="rgba(0,0,0,.9)"; CTX.stroke();
  }

  function drawEntityCircleWithHeading(x,y,r,deg,color,arrow){
    CTX.fillStyle = color;
    CTX.beginPath(); CTX.arc(x,y,r*dpr,0,Math.PI*2); CTX.fill();
    const a = (deg||0) * Math.PI/180;
    const L = r * 1.6 * dpr;
    CTX.strokeStyle = "rgba(0,0,0,.9)";
    CTX.lineWidth = 1.5*dpr;
    CTX.beginPath();
    CTX.moveTo(x, y);
    CTX.lineTo(x + Math.sin(a) * L, y - Math.cos(a) * L);
    CTX.stroke();
    if (arrow){
      CTX.fillStyle = color;
      const ay = y - (r*1.9*dpr);
      const ax = x;
      const s = 3*dpr;
      CTX.beginPath();
      if (arrow === 'up'){
        CTX.moveTo(ax, ay - s);
        CTX.lineTo(ax - s, ay + s);
        CTX.lineTo(ax + s, ay + s);
      } else {
        CTX.moveTo(ax, ay + s);
        CTX.lineTo(ax - s, ay - s);
        CTX.lineTo(ax + s, ay - s);
      }
      CTX.closePath(); CTX.fill();
      CTX.strokeStyle="rgba(0,0,0,.9)"; CTX.lineWidth=1*dpr; CTX.stroke();
    }
  }

  function drawLootMarker(x, y, r, color, isImportant){
    const half = r * dpr;
    
    if (isImportant) {
      CTX.fillStyle = color;
      CTX.beginPath();
      for (let i = 0; i < 8; i++) {
        const angle = (i * Math.PI / 4) - Math.PI / 2;
        const radius = (i % 2 === 0) ? half : half * 0.4;
        const px = x + Math.cos(angle) * radius;
        const py = y + Math.sin(angle) * radius;
        if (i === 0) CTX.moveTo(px, py);
        else CTX.lineTo(px, py);
      }
      CTX.closePath();
      CTX.fill();
      CTX.strokeStyle = "rgba(0,0,0,.9)";
      CTX.lineWidth = 1.5*dpr;
      CTX.stroke();
    } else {
      CTX.fillStyle = color;
      CTX.beginPath();
      CTX.moveTo(x, y - half);
      CTX.lineTo(x + half, y);
      CTX.lineTo(x, y + half);
      CTX.lineTo(x - half, y);
      CTX.closePath();
      CTX.fill();
      CTX.strokeStyle = "rgba(0,0,0,.9)";
      CTX.lineWidth = 1.5*dpr;
      CTX.stroke();
    }
  }

  function drawContainerMarker(x, y, r, color){
    const half = r * dpr;
    CTX.fillStyle = color;
    CTX.fillRect(x - half, y - half, half * 2, half * 2);
    CTX.strokeStyle = "rgba(0,0,0,.9)";
    CTX.lineWidth = 1.5*dpr;
    CTX.strokeRect(x - half, y - half, half * 2, half * 2);
  }

  function drawGridBackdrop(){
    CTX.fillStyle="#000"; CTX.fillRect(0,0,CANVAS.width,CANVAS.height);
    CTX.strokeStyle="rgba(255,255,255,0.08)"; CTX.lineWidth=1*dpr;
    const step = Math.max(24*dpr, Math.min(CANVAS.width, CANVAS.height)/16);
    for (let x=0; x<=CANVAS.width; x+=step){ CTX.beginPath(); CTX.moveTo(x,0); CTX.lineTo(x,CANVAS.height); CTX.stroke(); }
    for (let y=0; y<=CANVAS.height; y+=step){ CTX.beginPath(); CTX.moveTo(0,y); CTX.lineTo(CANVAS.width,y); CTX.stroke(); }
  }

  function drawDebugInfo(frame, v){
    if (!frame || !frame.self) return;
    
    const cfg = activeMap ? loadCfg(activeMap.name) : defaultCfg();
    const imgPos = worldToImagePx(frame.self.x, frame.self.y, v);
    
    let validActors = 0;
    if (Array.isArray(frame.actors)) {
      validActors = frame.actors.filter(a => 
        a && !isBogusPosition(a.x, a.y, a.z)
      ).length;
    }
    
    CTX.fillStyle = "rgba(0, 0, 0, 0.75)";
    CTX.fillRect(10*dpr, 10*dpr, 420*dpr, 300*dpr);
    
    CTX.fillStyle = "#00ff00";
    CTX.font = `${12*dpr}px monospace`;
    let y = 25*dpr;
    const line = (txt) => { CTX.fillText(txt, 15*dpr, y); y += 15*dpr; };
    
    line(`Session: 0x${(currentSession || 0).toString(16)}`);
    line(`Frames Received: ${frameCount}`);
    line(`World Pos: (${frame.self.x.toFixed(0)}, ${frame.self.y.toFixed(0)})`);
    line(`Image Pos: (${imgPos[0].toFixed(1)}, ${imgPos[1].toFixed(1)}) px`);
    line(`Image Size: ${v.imgW}x${v.imgH} px`);
    line(`Scale: ${v.inScale} cm/px`);
    line(`Y-Flip: ${v.yFlip}`);
    line(`Offset: (${v.worldOffsetCm.x.toFixed(0)}, ${v.worldOffsetCm.y.toFixed(0)})`);
    line(`Calibrated: ${isCalibrated ? 'YES' : 'NO'}`);
    line(`Pan: (${imgPan.x.toFixed(0)}, ${imgPan.y.toFixed(0)})`);
    line(`Zoom: ${v.zoom.toFixed(2)}`);
    line(`Total Actors: ${(frame.actors || []).length} (valid: ${validActors})`);
    line(`Loot: ${(frame.loot || []).length}`);
    line(`Containers: ${(frame.containers || []).length}`);
    line(`Loot MinPrice: ${lootMinPriceEl?.value || 0}`);
    line(`Important Threshold: ${lootImportantThresholdEl?.value || 50000}`);
    line(``);
    line(`Press 'C' to calibrate | 'R' to reset`);
  }

  // --------- HOVER DETECTION ---------
  function findHoveredEntity(mouseCanvasX, mouseCanvasY, frame, v, R){
    if (!frame || frame.ok !== true) return null;

    const HOVER_RADIUS = 15 * dpr;
    
    const inRange = (x, y) => {
      const dx = x - mouseCanvasX;
      const dy = y - mouseCanvasY;
      return Math.hypot(dx, dy) <= HOVER_RADIUS;
    };

    const showLoot = !!(fLootEl?.checked);
    const showContainers = !!(fContainersEl?.checked);
    const showPMCs = !!(fPMCsEl?.checked);
    const showBots = !!(fBotsEl?.checked);
    const showDead = !!(fDeadEl?.checked);

    if (showLoot && Array.isArray(frame.loot)){
      for (const item of frame.loot){
        if (!item || typeof item.x!=="number" || typeof item.y!=="number") continue;
        if (isBogusPosition(item.x, item.y, item.z || 0)) continue;
        
        const img = worldToImagePx(item.x, item.y, v);
        const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;
        if (inRange(x, y)){
          return {
            type: 'loot',
            data: item,
            screenX: x / dpr,
            screenY: y / dpr
          };
        }
      }
    }

    if (showContainers && Array.isArray(frame.containers)){
      for (const cont of frame.containers){
        if (!cont || typeof cont.x!=="number" || typeof cont.y!=="number") continue;
        if (isBogusPosition(cont.x, cont.y, cont.z || 0)) continue;
        
        const img = worldToImagePx(cont.x, cont.y, v);
        const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;
        if (inRange(x, y)){
          return {
            type: 'container',
            data: cont,
            screenX: x / dpr,
            screenY: y / dpr
          };
        }
      }
    }

    if (Array.isArray(frame.actors)){
      const selfId = frame.self?.id ?? frame.self?.pawn ?? null;
      for (const a of frame.actors){
        if (!a || typeof a.x!=="number" || typeof a.y!=="number") continue;
        if (isBogusPosition(a.x, a.y, a.z || 0)) continue;
        if (a.self === true) continue;
        if (selfId != null && (a.id === selfId || a.pawn === selfId)) continue;
        
        const isZombieHover = a.kind === "zombie";
        const isDayzPlayerHover = a.kind === "player";

        if (a.dead && !showDead) continue;
        if (isZombieHover && !(fZombiesEl?.checked)) continue;
        if (isDayzPlayerHover && !(fPlayersEl?.checked)) continue;
        if (!isZombieHover && !isDayzPlayerHover) {
          if (a.bot && !showBots) continue;
          if (!a.bot && !a.dead && !showPMCs) continue;
        }

        const img = worldToImagePx(a.x, a.y, v);
        const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;
        if (inRange(x, y)){
          const hoverType = a.dead ? 'dead'
                          : isZombieHover ? 'zombie'
                          : isDayzPlayerHover ? 'player'
                          : (a.bot ? 'bot' : 'pmc');
          return {
            type: hoverType,
            data: a,
            screenX: x / dpr,
            screenY: y / dpr
          };
        }
      }
    }

    return null;
  }

  // --------- TOOLTIP RENDERING ---------
  function showTooltip(entity){
    if (!entity) {
      TOOLTIP.classList.remove('visible');
      return;
    }

    let html = '';
    
    if (entity.type === 'loot'){
      const item = entity.data;
      html = `<div class="tt-header">${item.name || item.className || 'Item'}</div>`;
      
      if (item.price > 0){
        html += `<div class="tt-row"><span class="tt-label">Value:</span><span class="tt-value tt-price">?${item.price.toLocaleString()}</span></div>`;
      }
      
      const dist = calculateDistance(item, lastFrame?.self);
      if (dist !== null){
        html += `<div class="tt-row"><span class="tt-label">Distance:</span><span class="tt-value">${dist.toFixed(1)}m</span></div>`;
      }
      html += `<div class="tt-row"><span class="tt-label">Type:</span><span class="tt-value">Ground Loot</span></div>`;
    }
    else if (entity.type === 'container'){
      const cont = entity.data;
      if (cont.kind === 'vehicle') {
        html = `<div class="tt-header">Vehicle</div>`;
        const dist = calculateDistance(cont, lastFrame?.self);
        if (dist !== null){
          html += `<div class="tt-row"><span class="tt-label">Distance:</span><span class="tt-value">${dist.toFixed(1)}m</span></div>`;
        }
      } else {
        html = `<div class="tt-header">Container</div>`;
        html += `<div class="tt-row"><span class="tt-label">Items:</span><span class="tt-value">${cont.count || 0}</span></div>`;

        if (cont.totalValue && cont.totalValue > 0){
          html += `<div class="tt-row"><span class="tt-label">Total Value:</span><span class="tt-value tt-price">?${cont.totalValue.toLocaleString()}</span></div>`;
        }

        const dist = calculateDistance(cont, lastFrame?.self);
        if (dist !== null){
          html += `<div class="tt-row"><span class="tt-label">Distance:</span><span class="tt-value">${dist.toFixed(1)}m</span></div>`;
        }
        html += `<div class="tt-row"><span class="tt-label">Status:</span><span class="tt-value">${cont.count > 0 ? 'Has Loot' : 'Empty'}</span></div>`;
      }
    }
    else if (entity.type === 'pmc' || entity.type === 'bot' || entity.type === 'dead' || entity.type === 'zombie' || entity.type === 'player'){
      const actor = entity.data;
      const typeName = entity.type === 'dead' ? 'Dead'
                     : entity.type === 'bot' ? 'Bot'
                     : entity.type === 'zombie' ? 'Zombie'
                     : entity.type === 'player' ? 'Player'
                     : 'PMC';
      html = `<div class="tt-header">${typeName}</div>`;
      
      const dist = calculateDistance(actor, lastFrame?.self);
      if (dist !== null){
        html += `<div class="tt-row"><span class="tt-label">Distance:</span><span class="tt-value">${dist.toFixed(1)}m</span></div>`;
      }
      
      if (actor.z !== undefined && lastFrame?.self?.z !== undefined){
        const heightDiff = actor.z - lastFrame.self.z;
        const heightDir = heightDiff > 50 ? '��' : (heightDiff < -50 ? '��' : '��');
        html += `<div class="tt-row"><span class="tt-label">Elevation:</span><span class="tt-value">${heightDir} ${Math.abs(heightDiff/100).toFixed(1)}m</span></div>`;
      }

      if (entity.type === 'dead'){
        html += `<div class="tt-row"><span class="tt-label">Status:</span><span class="tt-value tt-dead">DEAD</span></div>`;
      }
      
      if (actor.pawn){
        html += `<div class="tt-row"><span class="tt-label">ID:</span><span class="tt-value">0x${actor.pawn.slice(0, 8)}</span></div>`;
      }
    }

    TOOLTIP.innerHTML = html;
    TOOLTIP.classList.add('visible');
    
    const rect = CANVAS.getBoundingClientRect();
    const tooltipRect = TOOLTIP.getBoundingClientRect();
    
    let x = rect.left + entity.screenX + 20;
    let y = rect.top + entity.screenY - tooltipRect.height / 2;
    
    if (x + tooltipRect.width > window.innerWidth){
      x = rect.left + entity.screenX - tooltipRect.width - 20;
    }
    if (y < 0) y = 10;
    if (y + tooltipRect.height > window.innerHeight){
      y = window.innerHeight - tooltipRect.height - 10;
    }
    
    TOOLTIP.style.left = `${x}px`;
    TOOLTIP.style.top = `${y}px`;
  }

  function calculateDistance(entity, self){
    if (!entity || !self || typeof entity.x !== "number" || typeof self.x !== "number") return null;
    const dx = entity.x - self.x;
    const dy = entity.y - self.y;
    return Math.hypot(dx, dy) / 100;
  }

  function drawFrame(frame){
    try{
      CTX.setTransform(1,0,0,1,0,0);
      CTX.clearRect(0,0,CANVAS.width,CANVAS.height);
      const v = readView();

      if (!(mapImg.complete && mapImg.naturalWidth)) {
        drawGridBackdrop();
        
        if (frame && frame.ok === true) {
          CTX.fillStyle = "#00ff00";
          CTX.font = `${16*dpr}px sans-serif`;
          CTX.textAlign = "center";
          CTX.fillText("? CONNECTED - Waiting for map image...", CANVAS.width/2, CANVAS.height/2);
          CTX.fillText(`Frames: ${frameCount} | Actors: ${(frame.actors || []).length}`, CANVAS.width/2, CANVAS.height/2 + 25*dpr);
          CTX.textAlign = "left";
        }
        return;
      }

      const R = imageDrawRect(v);
      CTX.drawImage(mapImg, 0,0, v.imgW,v.imgH, R.dx, R.dy, R.dw, R.dh);

      if (frame && frame.ok === true){
        if (frame.session && frame.session !== currentSession) {
          console.log(`[WebRadar] ? New session detected: 0x${frame.session.toString(16)}`);
          currentSession = frame.session;
          isCalibrated = false;
          calibrationOffset = { x: 0, y: 0 };
          imgPan = { x: 0, y: 0 };
          setTimeout(()=> enterCalibrationMode(), 500);
        }

        const showSelf = !!(fSelfEl?.checked);
        const showPMCs = !!(fPMCsEl?.checked);
        const showBots = !!(fBotsEl?.checked);
        const showDead = !!(fDeadEl?.checked);
        const showLoot = !!(fLootEl?.checked);
        const showContainers = !!(fContainersEl?.checked);
        const showEmptyContainers = !!(fEmptyContainersEl?.checked);

        const useRange  = true;
        const maxRangeM = Math.max(0, parseFloat(maxRangeEl?.value || "0"));
        const lootMaxRangeM = Math.max(0, parseFloat(lootMaxRangeEl?.value || "0"));
        const lootMinPrice = Math.max(0, parseInt(lootMinPriceEl?.value || "0", 10));
        const lootImportantThreshold = Math.max(0, parseInt(lootImportantThresholdEl?.value || "50000", 10));

        const rSelf = clamp(parseInt(szSelfEl?.value || "8",10), 2, 30);
        const rPMC  = clamp(parseInt(szPMCEl?.value  || "6",10), 2, 30);
        const rBot  = clamp(parseInt(szBotEl?.value  || "5",10), 2, 30);
        const rDead = clamp(parseInt(szDeadEl?.value || "6",10), 2, 30);
        const rLoot = clamp(parseInt(szLootEl?.value || "5",10), 2, 30);
        const rContainer = clamp(parseInt(szContainerEl?.value || "7",10), 2, 30);

        const cSelf = colSelfEl?.value || "#19ff6a";
        const cPMC  = colPMCEl?.value  || "#ff4a4a";
        const cBot  = colBotEl?.value  || "#0db1ff";
        const cDead = colDeadEl?.value || "#ffd600";
        const cLoot = colLootEl?.value || "#00ff88";
        const cLootImportant = colLootImportantEl?.value || "#ff0aff";
        const cContainerFilled = colContainerFilledEl?.value || "#ffd700";
        const cContainerEmpty = colContainerEmptyEl?.value || "#808080";
        const cContainerImportant = colContainerImportantEl?.value || "#ff0aff";

        let selfZ = 0;
        
        if (frame.self && typeof frame.self.x==="number" && typeof frame.self.y==="number"){
          if (!isBogusPosition(frame.self.x, frame.self.y, frame.self.z || 0)) {
            selfZ = (typeof frame.self.z === "number") ? frame.self.z : 0;
            if (showSelf){
              const img = worldToImagePx(frame.self.x, frame.self.y, v);
              const yaw = (typeof frame.self.yaw === "number") ? frame.self.yaw : 0;
              const sx = R.dx + img[0]*R.s, sy = R.dy + img[1]*R.s;
              drawSelfMarker(sx, sy, rSelf, yaw, cSelf);
            }
          }
        }

        if (showLoot && Array.isArray(frame.loot)){
          for (const item of frame.loot){
            if (!item || typeof item.x!=="number" || typeof item.y!=="number") continue;
            if (isBogusPosition(item.x, item.y, item.z || 0)) continue;
            
            if (lootMinPrice > 0 && (item.price || 0) < lootMinPrice) continue;
            
            if (lootMaxRangeM > 0 && frame.self){
              const dx = (item.x - frame.self.x), dy = (item.y - frame.self.y);
              const distM = Math.hypot(dx,dy)/100;
              if (distM > lootMaxRangeM) continue;
            }

            const img = worldToImagePx(item.x, item.y, v);
            const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;
            
            const isImportant = (item.price || 0) >= lootImportantThreshold;
            const color = isImportant ? cLootImportant : cLoot;
            
            drawLootMarker(x, y, rLoot, color, isImportant);
          }
        }

        if (showContainers && Array.isArray(frame.containers)){
          for (const cont of frame.containers){
            if (!cont || typeof cont.x!=="number" || typeof cont.y!=="number") continue;
            if (isBogusPosition(cont.x, cont.y, cont.z || 0)) continue;
            
            if (cont.count === 0 && !showEmptyContainers) continue;
            
            if (lootMaxRangeM > 0 && frame.self){
              const dx = (cont.x - frame.self.x), dy = (cont.y - frame.self.y);
              const distM = Math.hypot(dx,dy)/100;
              if (distM > lootMaxRangeM) continue;
            }

            const img = worldToImagePx(cont.x, cont.y, v);
            const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;
            
            let color;
            if (cont.kind === "vehicle") color = "#00aaff";
            else if (cont.count === 0) color = cContainerEmpty;
            else color = cContainerFilled;
            if (cont.totalValue && cont.totalValue >= lootImportantThreshold) {
              color = cContainerImportant;
            }

            drawContainerMarker(x, y, rContainer, color);
          }
        }

        let drawnActors = 0;
        if (Array.isArray(frame.actors)){
          const selfId = frame.self?.id ?? frame.self?.pawn ?? null;
          for (const a of frame.actors){
            if (!a || typeof a.x!=="number" || typeof a.y!=="number") continue;
            if (isBogusPosition(a.x, a.y, a.z || 0)) continue;
            if (a.self === true) continue;
            if (selfId != null && (a.id === selfId || a.pawn === selfId)) continue;

            if (useRange && maxRangeM > 0 && frame.self){
              const dx = (a.x - frame.self.x), dy = (a.y - frame.self.y);
              const distM = Math.hypot(dx,dy)/100; 
              if (distM > maxRangeM) continue;
            }

            const img = worldToImagePx(a.x, a.y, v);
            const x = R.dx + img[0]*R.s, y = R.dy + img[1]*R.s;

            const yaw = (typeof a.yaw === "number") ? a.yaw : 0;
            const az  = (typeof a.z   === "number") ? a.z   : 0;
            const altArrow = (frame.self && Math.abs(az - selfZ) > 50) ? (az > selfZ ? 'up' : 'down') : null;

            const isZombie = a.kind === "zombie";
            const isDayzPlayer = a.kind === "player";
            if (a.dead) {
              if (!showDead) continue;
              drawEntityCircleWithHeading(x,y,rDead, yaw, cDead, altArrow);
              drawnActors++;
              continue;
            }
            if (isZombie) {
              if (!(fZombiesEl?.checked)) continue; // zombies are NEVER gated by the ABI Bots checkbox
              const zombieColor = lastFrame?.colors?.zombie || "#ff3030";
              drawEntityCircleWithHeading(x,y,rBot, yaw, zombieColor, altArrow);
              drawnActors++;
              continue;
            }
            if (isDayzPlayer) {
              if (!(fPlayersEl?.checked)) continue;
              drawEntityCircleWithHeading(x,y,rPMC, yaw, cPMC, altArrow);
              drawnActors++;
              continue;
            }
            if (a.bot) {
              if (!showBots) continue;
              drawEntityCircleWithHeading(x,y,rBot, yaw, cBot, altArrow);
              drawnActors++;
            }
            else {
              if (!showPMCs) continue;
              drawEntityCircleWithHeading(x,y,rPMC, yaw, cPMC, altArrow);
              drawnActors++;
            }
          }
        }
        
        if (frameCount % 100 === 0) {
          console.log(`[WebRadar] Drew ${drawnActors} actors on canvas (frame ${frameCount})`);
        }
        
        drawDebugInfo(frame, v);
        
        if (!isCalibrated || calibrationMode) {
          CTX.fillStyle = "rgba(0, 0, 0, 0.85)";
          CTX.fillRect(CANVAS.width/2 - 280*dpr, CANVAS.height/2 - 50*dpr, 560*dpr, 100*dpr);
          
          CTX.fillStyle = "#ffcc00";
          CTX.font = `bold ${20*dpr}px sans-serif`;
          CTX.textAlign = "center";
          CTX.fillText("CALIBRATION MODE", CANVAS.width/2, CANVAS.height/2 - 15*dpr);
          
          CTX.font = `${15*dpr}px sans-serif`;
          CTX.fillStyle = "#ffffff";
          CTX.fillText("Click on the map where you are currently located", CANVAS.width/2, CANVAS.height/2 + 15*dpr);
          
          CTX.font = `${13*dpr}px sans-serif`;
          CTX.fillStyle = "#aaaaaa";
          CTX.fillText("Press ESC to cancel | Press C to toggle", CANVAS.width/2, CANVAS.height/2 + 35*dpr);
          
          CTX.textAlign = "left";
        }
      }
    } catch (err){
      console.error("[WebRadar] draw error:", err);
      CTX.setTransform(1,0,0,1,0,0);
      CTX.fillStyle="#000"; CTX.fillRect(0,0,CANVAS.width,CANVAS.height);
    }
  }

  // --------- Map loading & UI wiring ---------
  function applyCfgToUI(name){
    const cfg = loadCfg(name);
    if (zoomEl)    zoomEl.value    = (cfg.zoom ?? 0.5);
    if (inScaleEl) inScaleEl.value = (cfg.inScale ?? 33.120).toFixed(3);
    if (yFlipEl)   yFlipEl.checked = !!cfg.yFlip;
  }

  function loadMapImage(fileOrUrl){
    imgPan.x = 0; imgPan.y = 0;
    isCalibrated = false;
    calibrationOffset = { x: 0, y: 0 };
    
    console.log(`[WebRadar] Loading map image: ${fileOrUrl}`);
    mapImg = new Image();
    mapImg.onload  = ()=> { 
      console.log(`[WebRadar] ? Map loaded: ${mapImg.naturalWidth}x${mapImg.naturalHeight}`);
      drawFrame(lastFrame); 
    };
    mapImg.onerror = ()=> { 
      console.error("[WebRadar] ? Failed to load map:", fileOrUrl); 
      drawFrame(lastFrame); 
    };
    mapImg.src = fileOrUrl;
  }

  async function setActiveMapByName(name){
    const m = mapList.find(x => x.name === name) || mapList[0];
    if (!m) return;
    activeMap = m;

    let cfg = loadCfg(m.name);
    if (!cfg.file) { cfg.file = m.file || ""; }

    const side = await loadSidecarForMap(m);
    if (side && side.cfg){
      cfg = mergeCfg(cfg, side.cfg);
      saveCfg(m.name, cfg);
      statusFlash(`loaded ${side.source}`);
    }

    applyCfgToUI(m.name);
    loadMapImage(m.url || ("/" + (m.file || "")));
  }

  async function refreshMapList(){
    try{
      const base = baseUrl();
      console.log(`[WebRadar] Fetching maps from ${base}/api/maps`);
      const r = await fetch(base + "/api/maps", { cache:"no-store" });
      if (!r.ok) throw 0;
      const arr = await r.json();
      if (!Array.isArray(arr) || arr.length === 0) throw 0;
      mapList = arr.map(m => ({ name: m.name, file: m.file, url: m.url || ("/" + m.file) }));
      console.log(`[WebRadar] ? Found ${mapList.length} maps:`, mapList.map(m => m.name));
    }catch{
      console.warn("[WebRadar] Using fallback maps");
      mapList = [
        { name:"Farm",   file:"Farm.png",   url:"/Farm/Farm.png"   },
        { name:"Valley", file:"Valley.png", url:"/Valley/Valley.png" }
      ];
    }

    if (mapSelEl){
      mapSelEl.innerHTML = "";
      for (const m of mapList){
        const opt = document.createElement("option");
        opt.value = m.name; opt.textContent = m.name;
        mapSelEl.appendChild(opt);
      }
      mapSelEl.value = mapList[0]?.name || "";
    }
    if (mapList.length) await setActiveMapByName(mapSelEl.value);
  }

  function persistUIToCfg(){
    if (!activeMap) return;
    const cfg = loadCfg(activeMap.name);
    cfg.zoom    = clamp(parseFloat(zoomEl.value)||cfg.zoom, 0.05, 10);
    cfg.inScale = Math.max(0.001, parseFloat(inScaleEl.value) || cfg.inScale);
    cfg.yFlip   = !!yFlipEl.checked;
    cfg.file    = activeMap.file;
    saveCfg(activeMap.name, cfg);
  }

  async function saveMapSetup(){
    if (!activeMap){ statusFlash("no active map"); return; }
    const cfg = loadCfg(activeMap.name);
    const url = activeMap.url || ("/" + (activeMap.file || ""));
    const dir = urlDirname(url);
    const name = activeMap.name;
    const sidecarPath = joinUrl(dir, `${name}.json`);

    const payload = JSON.stringify({
      file: cfg.file || activeMap.file,
      inScale: cfg.inScale,
      yFlip: cfg.yFlip,
      zoom: cfg.zoom
    }, null, 2);

    let ok = false;
    // Generic save route; HandleSidecarPut was removed when WebRadarServer became game-agnostic.
    try{
      const r = await fetch(`/api/mapsidecar/${encodeURIComponent(name)}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: payload
      });
      ok = r.ok;
    }catch{}

    if (!ok){
      try{
        const r = await fetch(`/api/mapcfg/${encodeURIComponent(name)}`, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ dir, name, config: JSON.parse(payload) })
        });
        ok = r.ok;
      }catch{}
    }

    if (!ok){
      const a = document.createElement("a");
      a.href = URL.createObjectURL(new Blob([payload], {type:"application/json"}));
      a.download = `${name}.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      statusFlash(`downloaded ${name}.json (place next to the map image)`);
      return;
    }

    statusFlash(`saved ${sidecarPath}`);
  }

  function baseUrl(){
    const typed = serverUrlEl?.value?.trim() || "";
    if (/^https?:/i.test(typed)) return typed.replace(/\/+$/,"");
    if (location.origin && /^https?:/i.test(location.origin)) return location.origin;
    return "http://localhost:8088";
  }

  function connect(){
    const base = baseUrl();
    console.log(`[WebRadar] ? Connecting to ${base}`);
    fetchGameMode(); // re-detect game when user (re)connects, may have switched server
    
    try { 
      es = new EventSource(base + "/stream"); 
      console.log("[WebRadar] Using SSE connection");
    }
    catch { 
      console.log("[WebRadar] SSE failed, falling back to polling");
      return startPolling(base); 
    }
    
    statusEl.textContent="connecting��"; btnConn.textContent="Connecting��";
    
    es.addEventListener("open", ()=>{
      console.log("[WebRadar] ? SSE connection established");
      statusEl.textContent="connected (SSE)"; btnConn.textContent="Disconnect";
      if (pollTimer){ clearInterval(pollTimer); pollTimer=null; }
    });
    
    es.addEventListener("error", (err)=>{
      console.warn("[WebRadar] ? SSE error, switching to polling", err);
      if (es){ es.close(); es=null; }
      startPolling(base);
    });
    
    es.addEventListener("frame", ev=>{
      try {
        lastFrame = JSON.parse(ev.data);
        frameCount++;

        if (frameCount <= 3 || frameCount % 100 === 0) {
          console.log(`[WebRadar] ? Frame #${frameCount}:`, {
            ok: lastFrame.ok,
            session: lastFrame.session?.toString(16),
            actors: lastFrame.actors?.length,
            loot: lastFrame.loot?.length,
            self: lastFrame.self ? `(${lastFrame.self.x.toFixed(0)}, ${lastFrame.self.y.toFixed(0)})` : 'MISSING'
          });
        }

        syncServerSelectedMap(lastFrame);
        drawFrame(lastFrame);
      } catch (e) { 
        console.error("[WebRadar] Failed to parse frame:", e);
        return; 
      }
    });
  }

  function startPolling(base){
    if (pollTimer) clearInterval(pollTimer);
    console.log("[WebRadar] Starting polling mode");
    statusEl.textContent="connected (poll)"; btnConn.textContent="Disconnect";
    const tick = ()=> {
      fetch(base + "/api/frame", { cache:"no-store" })
        .then(r => {
          if (!r.ok) {
            console.error(`[WebRadar] Polling failed: ${r.status}`);
            return Promise.reject();
          }
          return r.json();
        })
        .then(j => {
          lastFrame=j;
          frameCount++;

          if (frameCount <= 3 || frameCount % 100 === 0) {
            console.log(`[WebRadar] ? Poll frame #${frameCount}:`, {
              ok: j.ok,
              actors: j.actors?.length,
              self: j.self ? `(${j.self.x.toFixed(0)}, ${j.self.y.toFixed(0)})` : 'MISSING'
            });
          }

          syncServerSelectedMap(lastFrame);
          drawFrame(lastFrame);
        })
        .catch((err)=>{
          console.error("[WebRadar] Polling error:", err);
        });
    };
    tick(); 
    pollTimer = setInterval(tick, 100);
  }

  function disconnect(){
    console.log("[WebRadar] Disconnecting");
    if (es){ es.close(); es=null; }
    if (pollTimer){ clearInterval(pollTimer); pollTimer=null; }
    statusEl.textContent="disconnected"; btnConn.textContent="Connect";
    lastFrame = null;
    frameCount = 0;
    lastServerSelectedMap = null; // re-apply on next connect
  }

  // --------- Interaction ---------
  let isDragging=false, dragStart={x:0,y:0}, panStart={x:0,y:0};
  
  CANVAS.addEventListener("mousedown", (e)=>{
    if (calibrationMode) return;
    isDragging=true; 
    CANVAS.classList.add("dragging"); 
    dragStart=clientToCanvas(e); 
    panStart={...imgPan};
  });

  CANVAS.addEventListener("click", (e)=>{
    if (!calibrationMode) return;
    const canvasPos = clientToCanvas(e);
    const v = readView();
    const R = imageDrawRect(v);
    const imgX = (canvasPos.x - R.dx) / R.s;
    const imgY = (canvasPos.y - R.dy) / R.s;
    if (imgX < 0 || imgX > v.imgW || imgY < 0 || imgY > v.imgH) {
      statusFlash("Click on the map, not outside it!");
      return;
    }
    calibrateFromClick(imgX, imgY);
  });

  window.addEventListener("mouseup", ()=>{ 
    if (isDragging){ isDragging=false; CANVAS.classList.remove("dragging"); }
  });

  window.addEventListener("mousemove", (e)=>{
    if (!isDragging || calibrationMode) {
      const canvasPos = clientToCanvas(e);
      const v = readView();
      const R = imageDrawRect(v);
      const newHovered = findHoveredEntity(canvasPos.x, canvasPos.y, lastFrame, v, R);
      
      if (newHovered !== hoveredEntity){
        hoveredEntity = newHovered;
        showTooltip(hoveredEntity);
      }
      return;
    }
    
    const cur = clientToCanvas(e);
    imgPan.x = panStart.x + (cur.x - dragStart.x);
    imgPan.y = panStart.y + (cur.y - dragStart.y);
    drawFrame(lastFrame);
  });

  CANVAS.addEventListener("wheel", (e)=>{
    e.preventDefault();
    if (calibrationMode) return;
    const z = clamp((parseFloat(zoomEl.value)||0.5) * (e.deltaY < 0 ? 1.1 : 0.9), 0.25, 10);
    zoomEl.value = z.toFixed(2);
    persistUIToCfg();
    drawFrame(lastFrame);
  }, { passive:false });

  window.addEventListener("keydown", (e)=>{
    if (e.key === "c" || e.key === "C") {
      if (calibrationMode) exitCalibrationMode();
      else enterCalibrationMode();
      drawFrame(lastFrame);
    }
    if (e.key === "Escape") {
      if (calibrationMode) {
        exitCalibrationMode();
        drawFrame(lastFrame);
      }
    }
    if (e.key === "r" || e.key === "R") {
      isCalibrated = false;
      calibrationOffset = { x: 0, y: 0 };
      enterCalibrationMode();
      console.log("[WebRadar] Reset calibration - click to recalibrate");
    }
    
    // DEBUG: Manual frame test
    if (e.key === "t" || e.key === "T") {
      console.log("[WebRadar] ? TEST - Current state:", {
        lastFrame: lastFrame,
        frameCount: frameCount,
        isConnected: !!(es || pollTimer),
        mapLoaded: mapImg.complete && mapImg.naturalWidth > 0
      });
    }
  });

  collapseBtn?.addEventListener("click", ()=>{
    const collapsed = toolbarEl.classList.toggle("collapsed");
    resizeCanvas();
    drawFrame(lastFrame);
  });

  tabs.forEach(btn=>{
    btn.addEventListener("click", ()=>{
      tabs.forEach(b=> b.classList.remove("active"));
      btn.classList.add("active");
      const key = btn.dataset.tab;
      Object.entries(tabPages).forEach(([k,el])=>{
        if (k === key) el.classList.add("active"); else el.classList.remove("active");
      });
      drawFrame(lastFrame);
    });
  });

  btnConn?.addEventListener("click", ()=> (es||pollTimer) ? disconnect() : connect());
  btnRescan?.addEventListener("click", ()=> refreshMapList());
  btnSaveMap?.addEventListener("click", ()=> saveMapSetup());

  mapSelEl?.addEventListener("change", ()=>{ setActiveMapByName(mapSelEl.value).then(()=> drawFrame(lastFrame)); });
  
  for (const el of [zoomEl, inScaleEl, yFlipEl,
                    fSelfEl, fPMCsEl, fBotsEl, fPlayersEl, fZombiesEl, fDeadEl, maxRangeEl,
                    szSelfEl, szPMCEl, szBotEl, szDeadEl,
                    colSelfEl, colPMCEl, colBotEl, colDeadEl,
                    fLootEl, fContainersEl, fEmptyContainersEl, lootMaxRangeEl, lootMinPriceEl,
                    szLootEl, szContainerEl, colLootEl, colLootImportantEl,
                    colContainerFilledEl, colContainerEmptyEl, colContainerImportantEl,
                    lootImportantThresholdEl]) {
    el?.addEventListener("input",  ()=>{ persistUIToCfg(); drawFrame(lastFrame); });
    el?.addEventListener("change", ()=>{ persistUIToCfg(); drawFrame(lastFrame); });
  }

  function clientToCanvas(e){ 
    const r = CANVAS.getBoundingClientRect(); 
    return { x:(e.clientX - r.left)*dpr, y:(e.clientY - r.top)*dpr }; 
  }
  
  function clamp(v,lo,hi){ return Math.max(lo, Math.min(hi, v)); }
  
  function statusFlash(txt){
    if (!statusEl) return;
    statusEl.textContent = txt;
    setTimeout(()=> statusEl.textContent = (es||pollTimer) ? "connected" : "disconnected", 1400);
  }

  resizeCanvas();
  window.addEventListener("resize", ()=>{ resizeCanvas(); drawFrame(lastFrame); });

  if (serverUrlEl) {
    serverUrlEl.value = (location.origin && /^https?:/i.test(location.origin)) ? location.origin : "http://localhost:8088";
  }

  console.log("[WebRadar] ? Initializing...");
  fetchGameMode(); // body stays game-abi until /status responds, preserving ABI defaults
  refreshMapList().then(()=> {
    console.log("[WebRadar] ? Initialization complete");
    drawFrame(null);
  });
})();