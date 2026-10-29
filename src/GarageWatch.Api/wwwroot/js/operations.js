import { $, api, captureErrorLabel, captureLabel, setDependency, setPill } from './api.js';

let profileRetryAt = 0;
let gatewayRetryAt = 0;
let gatewayCheck;

async function checkGateway(url) {
  if (!url || gatewayCheck || Date.now() < gatewayRetryAt) return gatewayCheck;
  gatewayRetryAt = Date.now() + 15000;
  gatewayCheck = (async () => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2500);
    const started = performance.now();
    try {
      const response = await fetch(url, {
        credentials: 'same-origin', cache: 'no-store', signal: controller.signal
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

async function updateSystemStatus(camera, readinessResult) {
  let capture;
  let spool;
  if (readinessResult.status === 'fulfilled') {
    try {
      const readiness = await readinessResult.value.json();
      capture = readiness.capture;
      spool = capture?.spool;
    } catch { capture = undefined; }
  }
  capture ??= camera ? {
    state: camera.status, adapter: camera.adapter, streamEpoch: camera.streamEpoch,
    segments: camera.segments, gaps: camera.gaps
  } : undefined;

  if (capture) {
    const segmentCount = Number.isFinite(spool?.segments) ? spool.segments
      : Number.isFinite(capture.segments) ? capture.segments : camera?.segments;
    const gapCount = Number.isFinite(capture.gaps) ? capture.gaps : camera?.gaps;
    const [label, statusClass] = captureLabel(capture.state);
    setPill('capture-health', label, statusClass);
    setDependency('dep-capture', label, statusClass);
    $('capture-adapter-summary').textContent = capture.adapter || 'Adaptador não informado';
    $('capture-adapter-detail').textContent = capture.adapter || 'Não informado';
    $('capture-adapter-version').textContent = capture.adapterVersion
      ? `Versão ${capture.adapterVersion}` : 'Versão não informada';
    $('capture-capabilities').textContent = Array.isArray(capture.capabilities) && capture.capabilities.length
      ? capture.capabilities.join(' · ') : 'Não informadas';
    $('stream-epoch').textContent = capture.streamEpoch ? `…${capture.streamEpoch.slice(-8)}` : 'Sem epoch ativo';
    $('capture-failure').textContent = captureErrorLabel(capture.lastErrorCode);
    const hasSegmentLimit = Number.isFinite(spool?.segmentLimit);
    $('spool-limit').textContent = hasSegmentLimit ? spool.segmentLimit : '';
    $('spool-limit-container').classList.toggle('hidden', !hasSegmentLimit);
    $('segments').textContent = Number.isFinite(segmentCount) ? segmentCount : '—';
    $('gaps').textContent = Number.isFinite(gapCount) ? gapCount : '—';
    const spoolState = spool?.status === 'available' ? ['Disponível', 'healthy']
      : spool?.status === 'unavailable' ? ['Indisponível', 'failed'] : ['Sem dados', 'pending'];
    setDependency('dep-spool', ...spoolState);
  } else {
    setPill('capture-health', 'Sem resposta', 'failed');
    setDependency('dep-capture', 'Sem resposta', 'failed');
    $('capture-adapter-summary').textContent = 'Não foi possível consultar a captura.';
    $('capture-failure').textContent = 'Detalhes indisponíveis; nenhum erro interno foi exposto.';
    $('spool-limit').textContent = '';
    $('spool-limit-container').classList.add('hidden');
    for (const id of ['segments', 'gaps']) $(id).textContent = '—';
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
    } catch { /* perfil continua indisponível sem bloquear a UI */ }
  }

  if (camera?.liveUrl) void checkGateway(camera.liveUrl);
  $('last-updated').textContent = `Atualizado ${new Date().toLocaleTimeString('pt-BR')}`;
}

export async function updateOperations(camera) {
  const [livenessResult, readinessResult] = await Promise.allSettled([
    api('/health/live'), api('/health/ready')
  ]);
  if (livenessResult.status === 'fulfilled' && livenessResult.value.ok) {
    setPill('api-health', 'Operacional', 'healthy');
    setDependency('dep-api', 'Operacional', 'healthy');
  } else {
    setPill('api-health', 'Indisponível', 'failed');
    setDependency('dep-api', 'Indisponível', 'failed');
  }
  await updateSystemStatus(camera, readinessResult);
}

export function markOperationsStale() {
  setPill('api-health', 'Sem atualização', 'pending');
  setDependency('dep-api', 'Sem atualização', 'pending');
  setPill('capture-health', 'Sem atualização', 'pending');
  setDependency('dep-capture', 'Sem atualização', 'pending');
  setPill('gateway-health', 'Não verificado', 'pending');
  setDependency('dep-hls', 'Não verificado', 'pending');
}

export function resetOperations() {
  profileRetryAt = 0;
  gatewayRetryAt = 0;
  gatewayCheck = undefined;
  window.__garageWatchProfile = undefined;
}
