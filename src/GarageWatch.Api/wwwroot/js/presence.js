import { $, api, show } from './api.js';

let timer;
let clientId;
let heartbeatInFlight = false;

function createClientId() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID();
  const bytes = new Uint8Array(16);
  if (typeof globalThis.crypto?.getRandomValues === 'function') globalThis.crypto.getRandomValues(bytes);
  else for (let index = 0; index < bytes.length; index++) bytes[index] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function setPresence(value, title) {
  show('client-count', true);
  $('connected-client-count').textContent = value;
  $('client-count').title = title;
}

export function stopPresence() {
  if (timer) clearInterval(timer);
  timer = undefined;
  clientId = undefined;
  heartbeatInFlight = false;
  show('client-count', false);
  $('connected-client-count').textContent = '—';
}

async function heartbeat() {
  if (!clientId || heartbeatInFlight) return;
  const currentClientId = clientId;
  heartbeatInFlight = true;
  try {
    const response = await api('/api/v1/clients/presence', {
      method: 'PUT',
      body: JSON.stringify({ clientId: currentClientId })
    });
    if (currentClientId !== clientId) return;
    if (response.status === 401) {
      stopPresence();
      return;
    }
    const body = await response.json();
    const activeClients = body.activeClients ?? body.ActiveClients;
    if (response.ok && Number.isInteger(activeClients)) {
      setPresence(activeClients, 'Abas autenticadas com heartbeat recente; uma conta pode ter vários clientes.');
    } else if (response.status === 429) {
      setPresence('64+', 'Limite de clientes ativos atingido; esta aba não foi incluída.');
    } else {
      setPresence('—', `Presença indisponível (HTTP ${response.status}).`);
    }
  } catch {
    if (currentClientId === clientId) setPresence('—', 'Contagem temporariamente indisponível; nova tentativa agendada.');
  } finally {
    heartbeatInFlight = false;
  }
}

export function startPresence() {
  stopPresence();
  try {
    clientId = createClientId();
  } catch {
    setPresence('—', 'Este navegador não disponibiliza geração segura de identificadores.');
    return;
  }
  setPresence('…', 'Conectando ao contador de clientes…');
  void heartbeat();
  timer = setInterval(() => void heartbeat(), 10000);
}
