const $ = id => document.getElementById(id);
let hls;
let refreshTimer;
let profileRetryAt = 0;
let gatewayRetryAt = 0;
let gatewayCheck;
let lastGatewayUrl;
let refreshInProgress = false;
let lastSuccessfulRefresh;
let clientPresenceTimer;
let clientPresenceId;

async function api(path, options = {}) {
  return fetch(path, {
    ...options,
    credentials: 'same-origin',
    cache: 'no-store',
    signal: options.signal || AbortSignal.timeout(3000),
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
}

function show(id, visible) { $(id).classList.toggle('hidden', !visible); }
function stopRefresh() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = undefined;
  }
}
function startRefresh() {
  stopRefresh();
  const poll = async () => {
    if (refreshInProgress) return;
    refreshInProgress = true;
    try { await refresh(); }
    catch {
      setPill('api-health', 'Sem atualização', 'pending');
      setDependency('dep-api', 'Sem atualização', 'pending');
      setPill('capture-health', 'Sem atualização', 'pending');
      setDependency('dep-capture', 'Sem atualização', 'pending');
      setPill('gateway-health', 'Não verificado', 'pending');
      setDependency('dep-hls', 'Não verificado', 'pending');
      const last = lastSuccessfulRefresh
        ? `Última atualização válida ${lastSuccessfulRefresh.toLocaleTimeString('pt-BR')}`
        : 'Ainda sem leitura válida';
      $('last-updated').textContent = `Sem resposta · ${last}`;
    } finally { refreshInProgress = false; }
  };
  void poll();
  refreshTimer = setInterval(() => void poll(), 3000);
}
function setPill(id, text, state) {
  const element = $(id);
  element.textContent = text;
  element.className = `health-pill ${state}`;
}
function setDependency(id, text, state) {
  const element = $(id);
  element.textContent = text;
  element.className = `dependency-state ${state}`;
}
function setPlayerState(text, state = 'pending') {
  setPill('player-health', text, state);
}
function stopClientPresence() {
  if (clientPresenceTimer) {
    clearInterval(clientPresenceTimer);
    clientPresenceTimer = undefined;
  }
  clientPresenceId = undefined;
  show('client-count', false);
  $('connected-client-count').textContent = '—';
}
async function sendClientHeartbeat() {
  if (!clientPresenceId) return;
  const clientId = clientPresenceId;
  try {
    const response = await api('/api/v1/clients/presence', {
      method: 'PUT',
      body: JSON.stringify({ clientId })
    });
    if (clientId !== clientPresenceId) return;
    if (response.status === 401) {
      stopClientPresence();
      return;
    }
    const status = await response.json();
    show('client-count', true);
    $('connected-client-count').textContent = Number.isInteger(status.activeClients)
      ? status.activeClients : '—';
    $('client-count').title = response.ok
      ? 'Abas autenticadas com heartbeat recente; uma conta pode ter vários clientes.'
      : 'Limite local de clientes ativos atingido.';
  } catch {
    if (clientId !== clientPresenceId) return;
    show('client-count', true);
    $('connected-client-count').textContent = '—';
    $('client-count').title = 'Contagem temporariamente indisponível.';
  }
}
function startClientPresence() {
  stopClientPresence();
  clientPresenceId = crypto.randomUUID();
  void sendClientHeartbeat();
  clientPresenceTimer = setInterval(() => void sendClientHeartbeat(), 10000);
}
function updatePlaybackControl(playing) {
  $('player-toggle-label').textContent = playing ? 'Pausar' : 'Reproduzir';
  $('player-toggle').setAttribute('aria-label', playing ? 'Pausar vídeo' : 'Reproduzir vídeo');
  $('player-play-icon').classList.toggle('hidden', playing);
  $('player-pause-icon').classList.toggle('hidden', !playing);
}
function liveEdgePosition() {
  if (hls && Number.isFinite(hls.liveSyncPosition)) return hls.liveSyncPosition;
  const video = $('live');
  if (!video.seekable.length) return undefined;
  const range = video.seekable.length - 1;
  return Math.max(video.seekable.start(range), video.seekable.end(range) - 0.5);
}
function updateLiveSyncControl() {
  const target = liveEdgePosition();
  const video = $('live');
  if (!Number.isFinite(target) || !Number.isFinite(video.currentTime)) return;
  const atLiveEdge = Math.abs(target - video.currentTime) <= 3;
  $('player-sync-label').textContent = atLiveEdge ? 'AO VIVO' : 'IR AO VIVO';
  $('player-sync').classList.toggle('is-live', atLiveEdge);
  $('player-sync').setAttribute('aria-label', atLiveEdge
    ? 'Você está no ponto ao vivo' : 'Ir ao ponto mais recente do vídeo');
}
async function syncToLive() {
  const target = liveEdgePosition();
  const video = $('live');
  if (!Number.isFinite(target)) {
    $('live-message').textContent = 'O ponto ao vivo ainda não está disponível; aguarde a playlist e tente novamente.';
    setPlayerState('Aguardando janela ao vivo', 'pending');
    return;
  }
  try {
    video.currentTime = target;
    updateLiveSyncControl();
    setPlayerState('Sincronizando ao vivo', 'pending');
    await video.play();
  } catch {
    $('live-message').textContent = 'Ponto ao vivo selecionado. Pressione reproduzir para continuar.';
    updatePlaybackControl(false);
  }
}
function captureLabel(state) {
  return ({
    capturing: ['Capturando', 'healthy'],
    connecting: ['Conectando à fonte', 'pending'],
    reconnecting: ['Reconectando', 'degraded'],
    'spool-unavailable': ['Spool indisponível', 'failed'],
    starting: ['Iniciando', 'pending'],
    stopped: ['Parada', 'failed']
  })[state] || ['Indisponível', 'failed'];
}
function captureErrorLabel(code) {
  return ({
    capture_source_unavailable: 'Fonte sem resposta; nova tentativa agendada.',
    capture_codec_unsupported: 'Formato de vídeo não aceito pelo adaptador.',
    capture_audio_unsupported: 'Formato de áudio não aceito pelo adaptador.',
    capture_segment_size_limit: 'Segmento excedeu o limite configurado.',
    local_spool_unavailable: 'Armazenamento local temporário indisponível.'
  })[code] || '';
}
function detachPlayer() {
  if (hls) {
    hls.destroy();
    hls = undefined;
  }
  const video = $('live');
  video.removeAttribute('src');
  delete video.dataset.url;
  video.load();
  $('player-toggle').disabled = true;
  $('player-sync').disabled = true;
  updatePlaybackControl(false);
}

async function checkGateway(url) {
  if (!url || gatewayCheck || Date.now() < gatewayRetryAt) return gatewayCheck;
  gatewayRetryAt = Date.now() + 15000;
  gatewayCheck = (async () => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2500);
    const started = performance.now();
    try {
      const response = await fetch(url, {
        credentials: 'same-origin',
        cache: 'no-store',
        signal: controller.signal
      });
      if (response.status === 401) {
        setPill('gateway-health', 'Sessão expirada', 'failed');
        setDependency('dep-hls', 'Sem sessão', 'failed');
        return;
      }
      const playlist = await response.text();
      const elapsed = Math.round(performance.now() - started);
      if (response.ok && playlist.startsWith('#EXTM3U')) {
        setPill('gateway-health', 'Disponível', 'healthy');
        $('gateway-detail').textContent = `Playlist autenticada · ${elapsed} ms`;
        setDependency('dep-hls', 'Disponível', 'healthy');
      } else if (response.status === 503) {
        setPill('gateway-health', 'Aguardando segmentos', 'degraded');
        $('gateway-detail').textContent = 'A fonte ainda não publicou segmentos para o live.';
        setDependency('dep-hls', 'Sem segmentos', 'degraded');
      } else {
        setPill('gateway-health', 'Indisponível', 'failed');
        $('gateway-detail').textContent = `Resposta HTTP ${response.status}.`;
        setDependency('dep-hls', `HTTP ${response.status}`, 'failed');
      }
    } catch {
      setPill('gateway-health', 'Sem conexão', 'failed');
      $('gateway-detail').textContent = 'Não foi possível consultar a playlist local.';
      setDependency('dep-hls', 'Sem conexão', 'failed');
    } finally {
      clearTimeout(timeout);
      gatewayCheck = undefined;
    }
  })();
  return gatewayCheck;
}

function attachPlayer(url) {
  detachPlayer();
  const video = $('live');
  video.dataset.url = url;
  $('player-toggle').disabled = false;
  $('player-sync').disabled = false;
  updatePlaybackControl(false);
  setPlayerState('Preparando player', 'pending');
  if (window.Hls && Hls.isSupported()) {
    hls = new Hls({
      lowLatencyMode: false,
      maxBufferLength: 12,
      backBufferLength: 6,
      maxMaxBufferLength: 18,
      xhrSetup: xhr => { xhr.withCredentials = true }
    });
    hls.loadSource(url);
    hls.attachMedia(video);
    hls.on(Hls.Events.MANIFEST_PARSED, () => {
      setPlayerState('Playlist recebida · pressione play', 'healthy');
      updateLiveSyncControl();
    });
    hls.on(Hls.Events.ERROR, (_, data) => {
      if (!data.fatal) return;
      const networkFailure = data.type === Hls.ErrorTypes.NETWORK_ERROR;
      setPlayerState(networkFailure ? 'Falha de rede no player' : 'Falha ao decodificar vídeo', 'failed');
      const safe = [data.type, data.details,
        Number.isInteger(data.response?.code) && data.response.code > 0 ? `HTTP ${data.response.code}` : '']
        .filter(Boolean).join(' / ');
      $('live-message').textContent = `Falha HLS (${safe || 'causa desconhecida'}).`;
      if (networkFailure) hls.startLoad();
      else hls.recoverMediaError();
    });
  } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
    video.src = url;
  } else {
    $('player-toggle').disabled = true;
    $('player-sync').disabled = true;
    setPlayerState('Formato não suportado neste navegador', 'failed');
    $('live-message').textContent = 'Este navegador não oferece reprodução HLS/MSE compatível.';
  }
}

$('player-toggle').addEventListener('click', async () => {
  const video = $('live');
  if (video.paused) {
    try { await video.play(); }
    catch { $('live-message').textContent = 'Não foi possível iniciar a reprodução. Tente novamente pelo controle do vídeo.'; }
  } else video.pause();
});
$('player-sync').addEventListener('click', () => void syncToLive());
$('live').addEventListener('playing', () => {
  updatePlaybackControl(true);
  updateLiveSyncControl();
  setPlayerState('Reproduzindo', 'healthy');
  $('live-message').textContent = 'Reprodução HLS ativa.';
});
$('live').addEventListener('loadedmetadata', updateLiveSyncControl);
$('live').addEventListener('timeupdate', updateLiveSyncControl);
$('live').addEventListener('progress', updateLiveSyncControl);
$('live').addEventListener('waiting', () => setPlayerState('Bufferizando', 'degraded'));
$('live').addEventListener('pause', () => {
  updatePlaybackControl(false);
  if ($('live').currentTime > 0) setPlayerState('Reprodução pausada', 'pending');
});
$('live').addEventListener('error', () => setPlayerState('Mídia indisponível', 'failed'));

async function updateSystemStatus(camera) {
  const [livenessResult, readinessResult] = await Promise.allSettled([
    api('/health/live'),
    api('/health/ready')
  ]);

  if (livenessResult.status === 'fulfilled' && livenessResult.value.ok) {
    setPill('api-health', 'Operacional', 'healthy');
    setDependency('dep-api', 'Operacional', 'healthy');
  } else {
    setPill('api-health', 'Indisponível', 'failed');
    setDependency('dep-api', 'Indisponível', 'failed');
  }

  let capture;
  let spool;
  if (readinessResult.status === 'fulfilled') {
    try {
      const readiness = await readinessResult.value.json();
      capture = readiness.capture;
      spool = readiness.capture?.spool;
    } catch { capture = undefined; }
  }
  capture ??= camera ? {
    state: camera.status,
    adapter: camera.adapter,
    streamEpoch: camera.streamEpoch,
    segments: camera.segments,
    gaps: camera.gaps
  } : undefined;

  if (capture) {
    const [label, statusClass] = captureLabel(capture.state);
    setPill('capture-health', label, statusClass);
    setDependency('dep-capture', label, statusClass);
    $('capture-adapter-summary').textContent = capture.adapter || 'Adaptador não informado';
    $('capture-adapter-detail').textContent = capture.adapter || 'Não informado';
    $('capture-adapter-version').textContent = capture.adapterVersion
      ? `Versão ${capture.adapterVersion}` : 'Versão não informada';
    $('capture-capabilities').textContent = Array.isArray(capture.capabilities) && capture.capabilities.length
      ? capture.capabilities.join(' · ') : 'Não informadas';
    $('stream-epoch').textContent = capture.streamEpoch
      ? `…${capture.streamEpoch.slice(-8)}` : 'Sem epoch ativo';
    $('capture-failure').textContent = captureErrorLabel(capture.lastErrorCode);
    $('spool-segments').textContent = Number.isFinite(spool?.segments) ? spool.segments : '—';
    $('spool-limit').textContent = Number.isFinite(spool?.segmentLimit) ? spool.segmentLimit : '—';
    $('spool-gaps').textContent = Number.isFinite(capture.gaps) ? capture.gaps : '—';
    $('segments').textContent = Number.isFinite(spool?.segments) ? spool.segments : '—';
    $('gaps').textContent = Number.isFinite(capture.gaps) ? capture.gaps : '—';
    const spoolState = spool?.status === 'available' ? ['Disponível', 'healthy']
      : spool?.status === 'unavailable' ? ['Indisponível', 'failed'] : ['Sem dados', 'pending'];
    setDependency('dep-spool', ...spoolState);
  } else {
    setPill('capture-health', 'Sem resposta', 'failed');
    setDependency('dep-capture', 'Sem resposta', 'failed');
    $('capture-adapter-summary').textContent = 'Não foi possível consultar a captura.';
    $('capture-failure').textContent = 'Detalhes indisponíveis; nenhum erro interno foi exposto.';
    $('spool-segments').textContent = '—';
    $('spool-limit').textContent = '—';
    $('spool-gaps').textContent = '—';
    $('segments').textContent = '—';
    $('gaps').textContent = '—';
    setDependency('dep-spool', 'Sem dados', 'pending');
  }

  if (!window.__garageWatchProfile && Date.now() >= profileRetryAt) {
    profileRetryAt = Date.now() + 15000;
    try {
      const response = await api('/api/v1/system-profile');
      if (response.ok) {
        const profile = await response.json();
        window.__garageWatchProfile = profile;
        $('app-service').textContent = profile.serviceName;
        $('app-service-detail').textContent = profile.serviceName;
        $('app-environment').textContent = profile.environment;
        $('app-version').textContent = profile.applicationVersion;
        $('app-build').textContent = `${profile.environment} · versão ${profile.applicationVersion}`;
      }
    } catch { /* a versão do perfil continua indisponível, sem bloquear o painel */ }
  }

  if (camera?.liveUrl && (camera.liveUrl !== lastGatewayUrl || Date.now() >= gatewayRetryAt)) {
    lastGatewayUrl = camera.liveUrl;
    void checkGateway(camera.liveUrl);
  }
  lastSuccessfulRefresh = new Date();
  $('last-updated').textContent = `Atualizado ${lastSuccessfulRefresh.toLocaleTimeString('pt-BR')}`;
}

async function refresh() {
  const session = await api('/api/v1/auth/session');
  if (!session.ok) throw new Error('Não foi possível consultar a sessão local.');
  if (!(await session.json()).authenticated) {
    stopRefresh();
    stopClientPresence();
    detachPlayer();
    show('login', true);
    show('dashboard', false);
    show('logout', false);
    return false;
  }

  const response = await api('/api/v1/cameras');
  if (response.status === 401) {
    stopRefresh();
    stopClientPresence();
    detachPlayer();
    show('login', true);
    show('dashboard', false);
    show('logout', false);
    return false;
  }
  if (!response.ok) throw new Error('Não foi possível consultar o estado local.');
  const [camera] = await response.json();
  show('login', false);
  show('dashboard', true);
  show('logout', true);
  $('camera-name').textContent = camera.cameraId;
  $('state').textContent = captureLabel(camera.status)[0];
  $('state').className = `badge rounded-pill status-badge ${captureLabel(camera.status)[1]}`;
  $('adapter').textContent = camera.adapter;
  if ($('live').dataset.url !== camera.liveUrl) attachPlayer(camera.liveUrl);
  await updateSystemStatus(camera);
  return true;
}

$('login-form').addEventListener('submit', async event => {
  event.preventDefault();
  const formElement = event.currentTarget;
  const submit = formElement.querySelector('button[type="submit"]');
  $('login-error').textContent = '';
  submit.disabled = true;
  try {
    const form = new FormData(formElement);
    const response = await api('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username: form.get('username'), password: form.get('password') })
    });
    if (!response.ok) {
      $('login-error').textContent = 'Acesso não autorizado. Confira usuário e senha.';
      return;
    }
    formElement.reset();
    if (await refresh()) {
      startClientPresence();
      startRefresh();
    }
  } catch {
    $('login-error').textContent = 'Não foi possível acessar o serviço local. Tente novamente.';
  } finally {
    submit.disabled = false;
  }
});

$('logout').addEventListener('click', async () => {
  stopRefresh();
  stopClientPresence();
  await api('/api/v1/auth/logout', { method: 'POST' });
  detachPlayer();
  window.__garageWatchProfile = undefined;
  profileRetryAt = 0;
  lastGatewayUrl = undefined;
  gatewayRetryAt = 0;
  setPlayerState('Player ainda não iniciado', 'pending');
  show('dashboard', false);
  show('logout', false);
  show('login', true);
});

refresh().then(authenticated => {
  if (authenticated) {
    startClientPresence();
    startRefresh();
  }
})
  .catch(error => { $('message').textContent = error.message; });
