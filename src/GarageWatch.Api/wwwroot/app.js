import { $, api, captureLabel, show } from './js/api.js';
import { startPresence, stopPresence } from './js/presence.js';
import { attachPlayer, detachPlayer, initializePlayerControls, setCameraAvailability, setPlayerPending } from './js/player.js';
import { configureNavigation } from './js/navigation.js';
import { markOperationsStale, resetOperations, updateOperations } from './js/operations.js';

let refreshTimer;
let refreshInProgress = false;
let lastSuccessfulRefresh;
let currentRole;
let previousCaptureState;
let previousStreamEpoch;

function stopRefresh() {
  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = undefined;
}

function returnToLogin() {
  stopRefresh();
  stopPresence();
  detachPlayer();
  currentRole = undefined;
  previousCaptureState = undefined;
  previousStreamEpoch = undefined;
  configureNavigation(false);
  show('login', true);
  show('register', false);
  show('user-landing', false);
  show('dashboard', false);
  show('logout', false);
}

async function loadRole() {
  const response = await api('/api/v1/auth/me');
  if (!response.ok) throw new Error('Não foi possível confirmar o papel da sessão.');
  const user = await response.json();
  if (!['Administrator', 'Viewer', 'User'].includes(user.role)) throw new Error('Papel de acesso não reconhecido.');
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
  if (currentRole === 'User') {
    stopPresence();
    detachPlayer();
    show('login', false);
    show('register', false);
    show('dashboard', false);
    show('user-landing', true);
    show('logout', true);
    return true;
  }
  show('user-landing', false);
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
  const recovered = camera.status === 'capturing' && (
    (previousCaptureState && previousCaptureState !== 'capturing') ||
    (previousStreamEpoch && camera.streamEpoch && previousStreamEpoch !== camera.streamEpoch)
  );
  previousCaptureState = camera.status;
  previousStreamEpoch = camera.streamEpoch;
  setCameraAvailability(camera.status);
  attachPlayer(camera.liveUrl, Boolean(recovered));
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
      setCameraAvailability('unknown');
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
  $('login-message').textContent = '';
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
      if (currentRole !== 'User') startPresence();
      startRefresh();
    }
  } catch {
    $('login-error').textContent = 'Não foi possível acessar o serviço local. Tente novamente.';
  } finally {
    submit.disabled = false;
  }
});

$('show-register').addEventListener('click', () => {
  $('register-message').textContent = '';
  show('login', false);
  show('register', true);
  $('register-username').focus();
});

$('show-login').addEventListener('click', () => {
  show('register', false);
  show('login', true);
  $('username').focus();
});

$('register-form').addEventListener('submit', async event => {
  event.preventDefault();
  const formElement = event.currentTarget;
  const submit = formElement.querySelector('button[type="submit"]');
  const form = new FormData(formElement);
  const username = form.get('username');
  $('register-message').textContent = '';
  $('register-message').className = 'feedback mt-3 mb-0';
  submit.disabled = true;
  try {
    const response = await api('/api/v1/auth/register', {
      method: 'POST',
      body: JSON.stringify({
        username,
        password: form.get('password'),
        confirmPassword: form.get('confirmPassword')
      })
    });
    if (response.status === 202) {
      formElement.reset();
      $('username').value = username;
      $('login-message').textContent = 'Se os dados forem válidos e o usuário estiver disponível, o cadastro foi processado. Tente entrar; não informamos se o nome já existe.';
      show('register', false);
      show('login', true);
      $('username').focus();
      return;
    }
    $('register-message').className = 'feedback error-text mt-3 mb-0';
    $('register-message').textContent = response.status === 400
      ? 'Confira o nome de usuário e os requisitos de senha.'
      : response.status === 403
        ? 'O cadastro só pode ser enviado pela interface local.'
        : response.status === 429
          ? 'Muitas tentativas. Aguarde um minuto e tente novamente.'
          : 'Não foi possível processar o cadastro local.';
  } catch {
    $('register-message').className = 'feedback error-text mt-3 mb-0';
    $('register-message').textContent = 'Não foi possível acessar o serviço local. Tente novamente.';
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
    if (currentRole !== 'User') startPresence();
    startRefresh();
  }
}).catch(error => { $('message').textContent = error.message; });
