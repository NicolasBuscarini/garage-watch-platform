import { $, setPill } from './api.js';

let hls;
let currentUrl;
let lastNonZeroVolume = 1;

function setPlayerState(text, state = 'pending') {
  setPill('player-health', text, state);
}

function updatePlaybackControl(playing) {
  $('player-toggle-label').textContent = playing ? 'Pausar' : 'Reproduzir';
  $('player-toggle').setAttribute('aria-label', playing ? 'Pausar vídeo' : 'Reproduzir vídeo');
  $('player-play-icon').classList.toggle('hidden', playing);
  $('player-pause-icon').classList.toggle('hidden', !playing);
}

function formatTime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '—';
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`
    : `${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`;
}

function seekWindow() {
  const video = $('live');
  if (video.seekable.length) {
    const last = video.seekable.length - 1;
    return { start: video.seekable.start(last), end: video.seekable.end(last) };
  }
  if (Number.isFinite(video.duration) && video.duration > 0) return { start: 0, end: video.duration };
  return undefined;
}

function updateSeekControls() {
  const video = $('live');
  const window = seekWindow();
  const seek = $('player-seek');
  if (!window || !Number.isFinite(video.currentTime) || window.end <= window.start) {
    seek.disabled = true;
    $('player-backward').disabled = true;
    $('player-forward').disabled = true;
    $('player-current-time').textContent = '00:00';
    $('player-duration').textContent = '—';
    return;
  }
  const length = window.end - window.start;
  const fraction = Math.min(1, Math.max(0, (video.currentTime - window.start) / length));
  seek.disabled = false;
  $('player-backward').disabled = video.currentTime <= window.start;
  $('player-forward').disabled = video.currentTime >= window.end;
  seek.value = String(Math.round(fraction * 1000));
  $('player-current-time').textContent = formatTime(video.currentTime - window.start);
  $('player-duration').textContent = formatTime(length);
}

function seekTo(time) {
  const window = seekWindow();
  if (!window) return;
  $('live').currentTime = Math.max(window.start, Math.min(window.end, time));
  updateSeekControls();
}

function updateMuteControl() {
  if ($('live').volume > 0) lastNonZeroVolume = $('live').volume;
  const muted = $('live').muted || $('live').volume === 0;
  $('player-mute').textContent = muted ? 'Mudo' : 'Áudio';
  $('player-mute').setAttribute('aria-label', muted ? 'Ativar áudio' : 'Silenciar áudio');
  $('player-mute').title = muted ? 'Ativar áudio' : 'Silenciar áudio';
  $('player-volume').value = String($('live').volume);
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

export function detachPlayer() {
  if (hls) hls.destroy();
  hls = undefined;
  currentUrl = undefined;
  const video = $('live');
  video.removeAttribute('src');
  delete video.dataset.url;
  video.load();
  $('player-toggle').disabled = true;
  $('player-sync').disabled = true;
  for (const id of ['player-backward', 'player-forward', 'player-seek', 'player-mute', 'player-volume', 'player-fullscreen'])
    $(id).disabled = true;
  updatePlaybackControl(false);
  updateSeekControls();
}

export function attachPlayer(url, forceRestart = false) {
  if (url === currentUrl && !forceRestart) return;
  const video = $('live');
  const resumeAfterReconnect = forceRestart && !video.paused && !video.ended;
  detachPlayer();
  currentUrl = url;
  video.dataset.url = url;
  $('player-toggle').disabled = false;
  $('player-sync').disabled = false;
  for (const id of ['player-backward', 'player-forward', 'player-mute', 'player-volume', 'player-fullscreen'])
    $(id).disabled = false;
  $('player-seek').disabled = false;
  updatePlaybackControl(false);
  updateSeekControls();
  updateMuteControl();
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
      if (resumeAfterReconnect) void syncToLive();
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
    if (resumeAfterReconnect)
      video.addEventListener('loadedmetadata', () => void syncToLive(), { once: true });
  } else {
    for (const id of ['player-toggle', 'player-sync', 'player-backward', 'player-forward', 'player-seek',
      'player-mute', 'player-volume', 'player-fullscreen']) $(id).disabled = true;
    setPlayerState('Formato não suportado neste navegador', 'failed');
    $('live-message').textContent = 'Este navegador não oferece reprodução HLS/MSE compatível.';
  }
}

export function setCameraAvailability(state) {
  const element = $('camera-availability');
  const message = ({
    starting: ['Iniciando a captura da câmera…', 'pending'],
    connecting: ['Conectando à câmera…', 'pending'],
    reconnecting: ['Câmera sem sinal. Tentando reconectar automaticamente…', 'degraded'],
    'spool-unavailable': ['Captura temporariamente indisponível. Tentando recuperar o armazenamento local…', 'failed'],
    stopped: ['Captura parada. O vídeo ao vivo está indisponível.', 'failed'],
    unknown: ['Não foi possível confirmar o estado da câmera. Verifique a conexão com a plataforma.', 'failed']
  })[state] || (state === 'capturing' ? undefined : ['Captura indisponível. O player será atualizado quando a fonte voltar.', 'failed']);
  if (!message) {
    element.textContent = '';
    element.classList.add('hidden');
    element.classList.remove('pending', 'degraded', 'failed');
    return;
  }
  const [text, severity] = message;
  if (element.textContent !== text) element.textContent = text;
  element.classList.remove('hidden', 'pending', 'degraded', 'failed');
  element.classList.add(severity);
}

export function setPlayerPending() {
  setPlayerState('Player ainda não iniciado', 'pending');
}

export function initializePlayerControls() {
  $('player-toggle').addEventListener('click', async () => {
    const video = $('live');
    if (video.paused) {
      try { await video.play(); }
      catch { $('live-message').textContent = 'Não foi possível iniciar a reprodução. Tente novamente pelo controle do vídeo.'; }
    } else video.pause();
  });
  $('player-sync').addEventListener('click', () => void syncToLive());
  $('player-backward').addEventListener('click', () => seekTo($('live').currentTime - 10));
  $('player-forward').addEventListener('click', () => seekTo($('live').currentTime + 10));
  $('player-seek').addEventListener('input', event => {
    const window = seekWindow();
    if (!window) return;
    const position = window.start + (Number(event.currentTarget.value) / 1000) * (window.end - window.start);
    $('player-current-time').textContent = formatTime(position - window.start);
  });
  $('player-seek').addEventListener('change', event => {
    const window = seekWindow();
    if (window) seekTo(window.start + (Number(event.currentTarget.value) / 1000) * (window.end - window.start));
  });
  $('player-mute').addEventListener('click', () => {
    const video = $('live');
    if (video.muted || video.volume === 0) {
      if (video.volume === 0) video.volume = lastNonZeroVolume;
      video.muted = false;
    } else video.muted = true;
    updateMuteControl();
  });
  $('player-volume').addEventListener('input', event => {
    const volume = Number(event.currentTarget.value);
    $('live').volume = volume;
    if (volume > 0) lastNonZeroVolume = volume;
    $('live').muted = volume === 0;
    updateMuteControl();
  });
  $('player-fullscreen').addEventListener('click', async () => {
    const frame = document.querySelector('.player-shell');
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else if (frame.requestFullscreen) await frame.requestFullscreen();
      else $('live-message').textContent = 'Tela cheia não é suportada neste navegador.';
    } catch {
      $('live-message').textContent = 'Não foi possível abrir o vídeo em tela cheia.';
    }
  });
  document.addEventListener('fullscreenchange', () => {
    const fullscreen = Boolean(document.fullscreenElement);
    $('player-fullscreen').setAttribute('aria-label', fullscreen ? 'Sair da tela cheia' : 'Abrir em tela cheia');
    $('player-fullscreen').title = fullscreen ? 'Sair da tela cheia' : 'Tela cheia';
  });
  $('live').addEventListener('playing', () => {
    updatePlaybackControl(true);
    updateLiveSyncControl();
    setPlayerState('Reproduzindo', 'healthy');
    $('live-message').textContent = 'Reprodução HLS ativa.';
  });
  $('live').addEventListener('loadedmetadata', updateLiveSyncControl);
  $('live').addEventListener('loadedmetadata', updateSeekControls);
  $('live').addEventListener('durationchange', updateSeekControls);
  $('live').addEventListener('timeupdate', () => {
    updateLiveSyncControl();
    updateSeekControls();
  });
  $('live').addEventListener('progress', () => {
    updateLiveSyncControl();
    updateSeekControls();
  });
  $('live').addEventListener('volumechange', updateMuteControl);
  $('live').addEventListener('waiting', () => setPlayerState('Bufferizando', 'degraded'));
  $('live').addEventListener('pause', () => {
    updatePlaybackControl(false);
    if ($('live').currentTime > 0) setPlayerState('Reprodução pausada', 'pending');
  });
  $('live').addEventListener('error', () => setPlayerState('Mídia indisponível', 'failed'));
}
