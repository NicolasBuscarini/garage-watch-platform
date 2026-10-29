import { $, api, captureLabel, show } from './js/api.js';
import { startPresence, stopPresence } from './js/presence.js';
import { attachPlayer, detachPlayer, initializePlayerControls, setPlayerPending } from './js/player.js';
import { configureNavigation } from './js/navigation.js';
import { markOperationsStale, resetOperations, updateOperations } from './js/operations.js';

let refreshTimer;
let refreshInProgress = false;
let lastSuccessfulRefresh;
let currentRole;

function stopRefresh() {
  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = undefined;
}

function returnToLogin() {
  stopRefresh();
  stopPresence();
  detachPlayer();
  currentRole = undefined;
  configureNavigation(false);
  show('login', true);
  show('dashboard', false);
  show('logout', false);
}

async function loadRole() {
  const response = await api('/api/v1/auth/me');
  if (!response.ok) throw new Error('Não foi possível confirmar o papel da sessão.');
  const user = await response.json();
  if (!['Administrator', 'Viewer'].includes(user.role)) throw new Error('Papel de acesso não reconhecido.');
  currentRole = user.role;
  configureNavigation(currentRole === 'Administrator');
}

async function refresh() {
  const session = await api('/api/v1/auth/session');
  if (!session.ok) throw new Error('Não foi possível consultar a sessão local.');
  if (!(await session.json()).authenticated) {
    returnToLogin();
    return false;
  }

  if (!currentRole) await loadRole();
  const response = await api('/api/v1/cameras');
  if (response.status === 401) {
    returnToLogin();
    return false;
  }
  if (!response.ok) throw new Error('Não foi possível consultar o estado local.');
  const [camera] = await response.json();
  show('login', false);
  show('dashboard', true);
  show('logout', true);
  $('camera-name').textContent = camera.cameraId;
  const [label, state] = captureLabel(camera.status);
  $('state').textContent = label;
  $('state').className = `badge rounded-pill status-badge ${state}`;
  attachPlayer(camera.liveUrl);
  if (currentRole === 'Administrator') await updateOperations(camera);
  lastSuccessfulRefresh = new Date();
  $('last-updated').textContent = currentRole === 'Administrator'
    ? `Atualizado ${lastSuccessfulRefresh.toLocaleTimeString('pt-BR')}` : '';
  return true;
}

function startRefresh() {
  stopRefresh();
  const poll = async () => {
    if (refreshInProgress) return;
    refreshInProgress = true;
    try {
      await refresh();
    } catch {
      if (currentRole === 'Administrator') {
        markOperationsStale();
        const last = lastSuccessfulRefresh
          ? `Última atualização válida ${lastSuccessfulRefresh.toLocaleTimeString('pt-BR')}`
          : 'Ainda sem leitura válida';
        $('last-updated').textContent = `Sem resposta · ${last}`;
      }
    } finally {
      refreshInProgress = false;
    }
  };
  void poll();
  refreshTimer = setInterval(() => void poll(), 3000);
}

initializePlayerControls();
configureNavigation(false);
setPlayerPending();

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
    await loadRole();
    if (await refresh()) {
      startPresence();
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
  stopPresence();
  await api('/api/v1/auth/logout', { method: 'POST' });
  detachPlayer();
  resetOperations();
  returnToLogin();
});

void refresh().then(authenticated => {
  if (authenticated) {
    startPresence();
    startRefresh();
  }
}).catch(error => { $('message').textContent = error.message; });
