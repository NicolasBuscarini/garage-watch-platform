export const $ = id => document.getElementById(id);

export function show(id, visible) {
  $(id).classList.toggle('hidden', !visible);
}

export async function api(path, options = {}) {
  return fetch(path, {
    ...options,
    credentials: 'same-origin',
    cache: 'no-store',
    signal: options.signal || AbortSignal.timeout(3000),
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
}

export function setPill(id, text, state) {
  const element = $(id);
  element.textContent = text;
  element.className = `health-pill ${state}`;
}

export function setDependency(id, text, state) {
  const element = $(id);
  element.textContent = text;
  element.className = `dependency-state ${state}`;
}

export function captureLabel(state) {
  return ({
    capturing: ['Capturando', 'healthy'],
    connecting: ['Conectando à fonte', 'pending'],
    reconnecting: ['Reconectando', 'degraded'],
    'spool-unavailable': ['Spool indisponível', 'failed'],
    starting: ['Iniciando', 'pending'],
    stopped: ['Parada', 'failed']
  })[state] || ['Indisponível', 'failed'];
}

export function captureErrorLabel(code) {
  return ({
    capture_source_unavailable: 'Fonte sem resposta; nova tentativa agendada.',
    capture_segment_timeout: 'Nenhum segmento novo no prazo; captura reiniciando a conexão.',
    capture_codec_unsupported: 'Formato de vídeo não aceito pelo adaptador.',
    capture_audio_unsupported: 'Formato de áudio não aceito pelo adaptador.',
    capture_segment_size_limit: 'Segmento excedeu o limite configurado.',
    local_spool_unavailable: 'Armazenamento local temporário indisponível.'
  })[code] || '';
}
