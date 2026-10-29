import { $ } from './api.js';

const tabs = [...document.querySelectorAll('[data-view-tab]')];
const views = [...document.querySelectorAll('[data-view-panel]')];

function activate(tab, moveFocus = false) {
  const target = tab.dataset.viewTab;
  if (tab.dataset.adminOnly === 'true' && document.body.dataset.isAdmin !== 'true') return;

  for (const candidate of tabs) {
    const selected = candidate === tab;
    candidate.setAttribute('aria-selected', String(selected));
    candidate.tabIndex = selected ? 0 : -1;
  }
  for (const view of views) view.hidden = view.id !== target;
  if (moveFocus) tab.focus();
}

export function configureNavigation(isAdmin) {
  document.body.dataset.isAdmin = String(isAdmin);
  for (const tab of tabs) {
    const restricted = tab.dataset.adminOnly === 'true';
    tab.hidden = restricted && !isAdmin;
    tab.disabled = restricted && !isAdmin;
    tab.tabIndex = tab.getAttribute('aria-selected') === 'true' && !tab.hidden ? 0 : -1;
  }
  const active = tabs.find(tab => tab.getAttribute('aria-selected') === 'true');
  if (!active || active.hidden) activate(tabs.find(tab => !tab.hidden));
}

for (const tab of tabs) tab.addEventListener('click', () => activate(tab));

$('view-tabs').addEventListener('keydown', event => {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
  const available = tabs.filter(tab => !tab.hidden && !tab.disabled);
  const currentIndex = available.indexOf(document.activeElement);
  if (currentIndex < 0) return;
  event.preventDefault();
  const nextIndex = event.key === 'Home' ? 0
    : event.key === 'End' ? available.length - 1
      : (currentIndex + (event.key === 'ArrowRight' ? 1 : available.length - 1)) % available.length;
  activate(available[nextIndex], true);
});
