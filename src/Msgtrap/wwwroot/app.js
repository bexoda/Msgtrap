/* ==========================================================================
   Msgtrap UI logic - vanilla JS, no build step.
   The control API (/api/control/*) drives the inbox; the Arkesel API
   (/api/v2/*) is what your application talks to.
   ========================================================================== */
'use strict';

const state = {
  channel: 'sms',
  messages: [],
  emails: [],
  providers: [],
  selectedId: null,
  selectedEmailId: null,
  emailTab: 'html',
  filters: { q: '', status: '', provider: '' },
  settings: null,
  smtp: null,
};

const $ = (id) => document.getElementById(id);

/* ---------- helpers ---------- */

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatRelative(iso) {
  const diff = (Date.now() - new Date(iso).getTime()) / 1000;
  if (diff < 45) return 'just now';
  if (diff < 3600) return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)}h ago`;
  return new Date(iso).toLocaleDateString();
}

function formatExact(iso) {
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

function shortId(id) {
  return id && id.length > 8 ? id.slice(0, 8) : (id || '');
}

function formatBytes(bytes) {
  if (!bytes) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function pill(status) {
  return `<span class="pill pill-${escapeHtml(status)}">${escapeHtml(status)}</span>`;
}

/** Display name for a provider id, falling back to the raw id. */
function providerName(id) {
  const provider = state.providers.find((entry) => entry.id === id);
  return provider ? provider.displayName : (id || 'unknown');
}

let toastTimer;
function toast(message, kind = 'ok') {
  const el = $('toast');
  el.textContent = message;
  el.dataset.kind = kind;
  el.dataset.show = 'true';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.dataset.show = 'false'; }, 2600);
}

async function api(path, options = {}) {
  const response = await fetch(path, options);
  const text = await response.text();
  let body = null;
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }
  if (!response.ok) {
    const error = new Error((body && body.message) || response.statusText || 'Request failed');
    error.status = response.status;
    error.body = body;
    throw error;
  }
  return body;
}

/* ---------- inbox ---------- */

let reloadTimer;
function scheduleReload() {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(loadMessages, 60);
}

async function loadMessages() {
  const params = new URLSearchParams();
  if (state.filters.q) params.set('q', state.filters.q);
  if (state.filters.status) params.set('status', state.filters.status);
  if (state.filters.provider) params.set('provider', state.filters.provider);

  try {
    const data = await api(`/api/control/messages?${params}`);
    state.messages = data.messages || [];
    if (state.selectedId && !state.messages.some((m) => m.id === state.selectedId)) {
      state.selectedId = null;
    }
    renderList();
    renderDetail();
  } catch (error) {
    console.error('Failed to load messages', error);
  }
}

function renderList() {
  const list = $('list');
  const scrollTop = list.parentElement.scrollTop;
  const count = state.messages.length;

  $('sms-count').textContent = count;
  if (state.channel === 'sms') {
    $('count').textContent = `${count} message${count === 1 ? '' : 's'}`;
  }
  $('empty').hidden = count > 0 || state.channel !== 'sms';

  list.innerHTML = state.messages.map((m) => {
    const first = m.recipients[0]?.number ?? 'no valid numbers';
    const extra = m.recipients.length > 1 ? ` +${m.recipients.length - 1}` : '';
    const selected = m.id === state.selectedId ? 'true' : 'false';
    return `
      <li class="msg-row" data-id="${escapeHtml(m.id)}" aria-selected="${selected}" tabindex="0">
        <div class="row-top">
          <span class="sender">${escapeHtml(m.sender || '(no sender)')}</span>
          <time datetime="${escapeHtml(m.createdAt)}" title="${escapeHtml(formatExact(m.createdAt))}">${escapeHtml(formatRelative(m.createdAt))}</time>
        </div>
        <p class="snippet">${escapeHtml(m.body)}</p>
        <div class="row-foot">
          ${pill(m.status)}
          <span class="rcpts">${escapeHtml(first)}${escapeHtml(extra)}</span>
          <span class="tag tag-provider" data-provider="${escapeHtml(m.provider)}">${escapeHtml(providerName(m.provider))}</span>
          ${m.otp ? '<span class="tag tag-otp">OTP</span>' : ''}
          ${m.sandbox ? '<span class="tag">sandbox</span>' : ''}
        </div>
      </li>`;
  }).join('');

  list.parentElement.scrollTop = scrollTop;
}

function formatDuration(ms) {
  const total = Math.max(0, Math.round(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return minutes > 0 ? `${minutes}m ${String(seconds).padStart(2, '0')}s` : `${seconds}s`;
}

/** Renders the OTP panel: big copyable code, expiry countdown and verify state. */
function renderOtpCard(otp) {
  const expiresAt = new Date(otp.expiresAt).getTime();
  const remaining = expiresAt - Date.now();
  const state = otp.verified ? 'used' : (remaining <= 0 ? 'expired' : 'active');
  const badgeText = otp.verified ? 'verified' : (remaining <= 0 ? 'expired' : 'active');

  return `
    <div class="otp-card">
      <div class="otp-head">
        <span class="otp-label">One-time code</span>
        <span class="otp-badge" data-state="${state}">${badgeText}</span>
      </div>
      <div class="otp-code-row">
        <span class="otp-code" data-copy="${escapeHtml(otp.code)}" title="Click to copy">${escapeHtml(otp.code)}</span>
        <button type="button" class="otp-copy" data-copy="${escapeHtml(otp.code)}">Copy</button>
      </div>
      <p class="otp-foot">
        ${escapeHtml(otp.type)} &middot; via ${escapeHtml(otp.medium)}
        &middot; <span id="otp-remaining" data-expires="${escapeHtml(otp.expiresAt)}">${remaining > 0 ? `expires in ${formatDuration(remaining)}` : 'expired'}</span>
        &middot; ${escapeHtml(formatExact(otp.expiresAt))}
        ${otp.attempts > 0 ? `&middot; ${otp.attempts} failed attempt${otp.attempts === 1 ? '' : 's'}` : ''}
      </p>
    </div>`;
}

/** Keeps the countdown honest without re-rendering the whole panel. */
function tickOtpRemaining() {
  const el = document.getElementById('otp-remaining');
  if (!el) return;
  const remaining = new Date(el.dataset.expires).getTime() - Date.now();
  el.textContent = remaining > 0 ? `expires in ${formatDuration(remaining)}` : 'expired';
}

function renderDetail() {
  if (state.channel !== 'sms') return;

  const host = $('detail');
  const message = state.messages.find((m) => m.id === state.selectedId);

  if (!message) {
    host.innerHTML = '<div class="detail-empty"><p>Select a message to inspect the request, segments and delivery timeline.</p></div>';
    return;
  }

  const recipients = message.recipients.length
    ? `<table class="recipient-table">
         <thead><tr><th>Number</th><th>Message id</th><th>Status</th></tr></thead>
         <tbody>
           ${message.recipients.map((r) => `
             <tr>
               <td>${escapeHtml(r.number)}</td>
               <td data-copy="${escapeHtml(r.id)}" title="Click to copy">${escapeHtml(r.id)}</td>
               <td>${pill(r.status)}</td>
             </tr>`).join('')}
         </tbody>
       </table>`
    : '<p class="hint">No valid recipients were supplied.</p>';

  const invalid = message.invalid.length
    ? `<p class="notice"><span>&#9888;</span><span>Rejected as invalid: ${message.invalid.map(escapeHtml).join(', ')}</span></p>`
    : '';

  const timeline = (message.deliveryLog || []).map((entry) => `
    <li data-status="${escapeHtml(entry.status)}">
      <div class="t-head">
        <span class="t-status">${escapeHtml(entry.status)}</span>
        <span class="t-time">${escapeHtml(formatExact(entry.at))}</span>
      </div>
      <div class="t-msg">${escapeHtml(entry.message)}</div>
    </li>`).join('');

  const metaItem = (label, value) => `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd></div>`;

  const otpCard = message.otp ? renderOtpCard(message.otp) : '';

  host.innerHTML = `
    <div class="detail-head">
      <div>
        <h2>${escapeHtml(message.sender || '(no sender)')}</h2>
        <p class="detail-sub">${escapeHtml(message.id)}</p>
      </div>
      <div style="text-align:right">
        ${pill(message.status)}
        <p class="detail-sub" style="margin-top:6px">${escapeHtml(formatExact(message.createdAt))}</p>
      </div>
    </div>

    <div class="chips">
      ${message.recipients.map((r) => `<span class="chip">${escapeHtml(r.number)}</span>`).join('') || '<span class="chip">no recipients</span>'}
    </div>

    <dl class="meta">
      ${metaItem('Provider', providerName(message.provider))}
      ${metaItem('Encoding', message.encoding)}
      ${metaItem('Segments', message.segments)}
      ${metaItem('Characters', message.chars)}
      ${metaItem('Delivery delay', `${message.deliveryDelayMs} ms`)}
      ${message.scheduledFor ? metaItem('Scheduled for', message.scheduledFor) : ''}
      ${message.callbackUrl ? metaItem('Callback', message.callbackUrl) : ''}
    </dl>

    ${invalid}

    ${otpCard}

    <div class="bubble">${escapeHtml(message.body)}</div>

    <h3 class="section-title">Recipients</h3>
    ${recipients}

    <h3 class="section-title">Delivery timeline</h3>
    <ul class="timeline">${timeline || '<li><div class="t-msg">No events recorded.</div></li>'}</ul>

    <details class="raw">
      <summary>Raw request body</summary>
      <pre>${escapeHtml(JSON.stringify(message.request ?? {}, null, 2))}</pre>
    </details>`;
}

/* selection */
$('list').addEventListener('click', (event) => {
  const row = event.target.closest('.msg-row');
  if (!row) return;
  state.selectedId = row.dataset.id;
  renderList();
  renderDetail();
});

$('list').addEventListener('keydown', (event) => {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  const row = event.target.closest('.msg-row');
  if (!row) return;
  event.preventDefault();
  state.selectedId = row.dataset.id;
  renderList();
  renderDetail();
});

/* copy message ids and OTP codes */
$('detail').addEventListener('click', async (event) => {
  const cell = event.target.closest('[data-copy]');
  if (!cell) return;
  try {
    await navigator.clipboard.writeText(cell.dataset.copy);
    toast(event.target.closest('.otp-card') ? 'Code copied' : 'Message id copied');
  } catch {
    toast('Clipboard unavailable', 'error');
  }
});

/* filters */
let searchTimer;
/** Refreshes whichever channel is on screen. */
function refreshActiveChannel() {
  return state.channel === 'sms' ? loadMessages() : loadEmails();
}

$('search').addEventListener('input', (event) => {
  state.filters.q = event.target.value;
  clearTimeout(searchTimer);
  searchTimer = setTimeout(refreshActiveChannel, 180);
});

$('status-filter').addEventListener('change', (event) => {
  state.filters.status = event.target.value;
  loadMessages();
});

$('provider-filter').addEventListener('change', (event) => {
  state.filters.provider = event.target.value;
  loadMessages();
});

document.addEventListener('keydown', (event) => {
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || '');
  if (event.key === '/' && !typing) {
    event.preventDefault();
    $('search').focus();
  }
});

/* clear all - only the channel you are looking at */
$('clear-btn').addEventListener('click', async () => {
  const isSms = state.channel === 'sms';
  const count = isSms ? state.messages.length : state.emails.length;
  if (count === 0) {
    toast(`${isSms ? 'SMS inbox' : 'Email inbox'} is already empty`);
    return;
  }

  $('confirm-copy').textContent = isSms
    ? 'Every captured SMS will be removed, along with outstanding OTPs. This cannot be undone.'
    : 'Every captured email will be removed. This cannot be undone.';

  const dialog = $('confirm');
  dialog.returnValue = ''; // a previous "confirm" must not leak into a cancelled dialog
  dialog.showModal();
  const result = await new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue), { once: true });
  });
  if (result !== 'confirm') return;

  try {
    if (isSms) {
      await api('/api/control/messages', { method: 'DELETE' });
      state.selectedId = null;
      toast('SMS inbox cleared');
      await loadMessages();
    } else {
      await api('/api/control/emails', { method: 'DELETE' });
      state.selectedEmailId = null;
      toast('Email inbox cleared');
      await loadEmails();
    }
  } catch (error) {
    toast(error.message, 'error');
  }
});

/* ---------- email ---------- */

async function loadEmails() {
  const params = new URLSearchParams();
  if (state.filters.q) params.set('q', state.filters.q);

  try {
    const data = await api(`/api/control/emails?${params}`);
    state.emails = data.emails || [];
    if (state.selectedEmailId && !state.emails.some((e) => e.id === state.selectedEmailId)) {
      state.selectedEmailId = null;
    }
    renderEmailList();
    renderEmailDetail();
  } catch (error) {
    console.error('Failed to load email', error);
  }
}

function renderEmailList() {
  const list = $('email-list');
  const scrollTop = list.parentElement.scrollTop;
  const count = state.emails.length;

  $('email-count').textContent = count;
  $('email-empty').hidden = count > 0 || state.channel !== 'email';
  if (state.channel === 'email') {
    $('count').textContent = `${count} email${count === 1 ? '' : 's'}`;
    if (state.smtp) $('smtp-address-empty').textContent = state.smtp.address;
  }

  list.innerHTML = state.emails.map((e) => {
    const selected = e.id === state.selectedEmailId ? 'true' : 'false';
    const to = e.to[0] ?? (e.bcc[0] ?? '(no recipients)');
    const extra = e.to.length > 1 ? ` +${e.to.length - 1}` : '';
    const attachments = e.attachments.length
      ? `<span class="tag tag-attachment" title="${escapeHtml(e.attachments.map((a) => a.fileName).join(', '))}">&#128206; ${e.attachments.length}</span>`
      : '';
    return `
      <li class="msg-row" data-id="${escapeHtml(e.id)}" aria-selected="${selected}" tabindex="0">
        <div class="row-top">
          <span class="sender">${escapeHtml(e.from || '(no sender)')}</span>
          <time datetime="${escapeHtml(e.receivedAt)}" title="${escapeHtml(formatExact(e.receivedAt))}">${escapeHtml(formatRelative(e.receivedAt))}</time>
        </div>
        <p class="snippet">${escapeHtml(e.subject || '(no subject)')}</p>
        <div class="row-foot">
          <span class="rcpts">${escapeHtml(to)}${escapeHtml(extra)}</span>
          ${attachments}
          ${e.hasHtml ? '<span class="tag tag-html">HTML</span>' : '<span class="tag">text</span>'}
          <span class="size">${escapeHtml(formatBytes(e.sizeBytes))}</span>
        </div>
      </li>`;
  }).join('');

  list.parentElement.scrollTop = scrollTop;
}

/** Detail panel for one captured email, with HTML/Text/Headers/Raw tabs. */
function renderEmailDetail() {
  if (state.channel !== 'email') return;

  const host = $('detail');
  const email = state.emails.find((e) => e.id === state.selectedEmailId);

  if (!email) {
    host.innerHTML = '<div class="detail-empty"><p>Select an email to read it, check its headers or copy the raw source.</p></div>';
    return;
  }

  const tabs = [];
  if (email.hasHtml) tabs.push(['html', 'HTML']);
  if (email.textBody) tabs.push(['text', 'Text']);
  tabs.push(['headers', 'Headers'], ['raw', 'Raw source']);

  const active = tabs.some(([tab]) => tab === state.emailTab) ? state.emailTab : tabs[0][0];
  state.emailTab = active;

  const metaItem = (label, value) =>
    `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd></div>`;

  const addresses = (label, list) => (list && list.length
    ? `<div><dt>${escapeHtml(label)}</dt><dd>${list.map(escapeHtml).join('<br>')}</dd></div>`
    : '');

  const attachments = email.attachments.length
    ? `<div class="attachments">
         ${email.attachments.map((a) => `
           <span class="attachment" title="${escapeHtml(a.contentType)}">
             <strong>${escapeHtml(a.fileName)}</strong>
             <span>${escapeHtml(a.contentType)} &middot; ${escapeHtml(formatBytes(a.sizeBytes))}</span>
           </span>`).join('')}
       </div>`
    : '';

  const panes = {
    html: '<iframe class="email-frame" sandbox="" referrerpolicy="no-referrer" title="HTML preview"></iframe>',
    text: `<pre class="email-body">${escapeHtml(email.textBody || '(no text part)')}</pre>`,
    headers: `<table class="header-table"><tbody>
        ${email.headers.map((h) => `<tr><th>${escapeHtml(h.name)}</th><td>${escapeHtml(h.value)}</td></tr>`).join('')}
      </tbody></table>`,
    raw: `<pre class="email-body raw-body">${escapeHtml(email.rawSource)}</pre>`,
  };

  host.innerHTML = `
    <div class="detail-head">
      <div>
        <h2>${escapeHtml(email.subject || '(no subject)')}</h2>
        <p class="detail-sub">${escapeHtml(email.from || '(no sender)')}</p>
      </div>
      <div style="text-align:right">
        <p class="detail-sub">${escapeHtml(formatExact(email.receivedAt))}</p>
        <p class="detail-sub" style="margin-top:6px">${escapeHtml(formatBytes(email.sizeBytes))}</p>
      </div>
    </div>

    <dl class="meta">
      ${addresses('To', email.to)}
      ${addresses('Cc', email.cc)}
      ${addresses('Bcc', email.bcc)}
      ${metaItem('Envelope from', email.envelopeFrom || '(none)')}
      ${metaItem('Envelope to', email.envelopeTo.join(', ') || '(none)')}
      ${metaItem('Received', formatExact(email.receivedAt))}
      ${metaItem('Size', formatBytes(email.sizeBytes))}
    </dl>

    ${attachments}

    <div class="tabs" role="tablist">
      ${tabs.map(([tab, label]) => `
        <button type="button" role="tab" class="tab" data-email-tab="${tab}" aria-selected="${tab === active}">${label}</button>`).join('')}
    </div>

    <div class="tab-pane" data-pane="${active}">${panes[active]}</div>

    <details class="raw">
      <summary>Identifiers</summary>
      <pre>${escapeHtml(JSON.stringify({
        id: email.id,
        envelopeFrom: email.envelopeFrom,
        envelopeTo: email.envelopeTo,
        headers: email.headers.length,
        attachments: email.attachments,
      }, null, 2))}</pre>
    </details>`;

  // The HTML part is untrusted markup: it runs in a sandboxed frame with no
  // script execution, so previewing a newsletter can never touch the UI.
  const frame = host.querySelector('.email-frame');
  if (frame) frame.srcdoc = email.htmlBody;
}

/* email row selection */
$('email-list').addEventListener('click', (event) => {
  const row = event.target.closest('.msg-row');
  if (!row) return;
  state.selectedEmailId = row.dataset.id;
  state.emailTab = 'html';
  renderEmailList();
  renderEmailDetail();
});

$('email-list').addEventListener('keydown', (event) => {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  const row = event.target.closest('.msg-row');
  if (!row) return;
  event.preventDefault();
  state.selectedEmailId = row.dataset.id;
  state.emailTab = 'html';
  renderEmailList();
  renderEmailDetail();
});

/* email detail tabs */
$('detail').addEventListener('click', (event) => {
  const tab = event.target.closest('[data-email-tab]');
  if (!tab) return;
  state.emailTab = tab.dataset.emailTab;
  renderEmailDetail();
});

/* ---------- channels ---------- */

function setChannel(channel) {
  state.channel = channel;

  document.querySelectorAll('[data-channel]').forEach((button) => {
    button.setAttribute('aria-selected', String(button.dataset.channel === channel));
  });
  document.querySelectorAll('[data-channel-only]').forEach((element) => {
    element.hidden = element.dataset.channelOnly !== channel;
  });

  const isSms = channel === 'sms';
  $('page-title').textContent = isSms ? 'SMS' : 'Email';
  $('page-sub').textContent = isSms
    ? 'Arkesel, Hubtel, Twilio and mNotify routes feeding one inbox.'
    : `SMTP capture${state.smtp ? ` on ${state.smtp.address}` : ''} - every message your app sends.`;
  $('list').hidden = !isSms;
  $('email-list').hidden = isSms;
  $('empty').hidden = isSms ? state.messages.length > 0 : true;
  $('email-empty').hidden = isSms ? true : state.emails.length > 0;
  $('search').placeholder = isSms
    ? 'Search sender, body, number or id'
    : 'Search sender, subject or body';
  $('compose-btn').hidden = !isSms;

  if (isSms) {
    $('count').textContent = `${state.messages.length} message${state.messages.length === 1 ? '' : 's'}`;
    renderDetail();
  } else {
    $('count').textContent = `${state.emails.length} email${state.emails.length === 1 ? '' : 's'}`;
    renderEmailDetail();
  }
}

document.querySelectorAll('[data-channel]').forEach((button) => {
  button.addEventListener('click', () => {
    setChannel(button.dataset.channel);
    closeSidebarOnMobile();
  });
});

/* ---------- sidebar ---------- */

const isDesktop = () => window.matchMedia('(min-width: 992px)').matches;

function closeSidebarOnMobile() {
  if (isDesktop()) return;
  document.body.classList.remove('sidebar-open');
  $('sidebar-overlay').hidden = true;
  $('sidebar-toggle').setAttribute('aria-expanded', 'false');
}

$('sidebar-toggle').addEventListener('click', () => {
  if (isDesktop()) {
    const collapsed = document.body.classList.toggle('sidebar-collapsed');
    $('sidebar-toggle').setAttribute('aria-expanded', String(!collapsed));
    return;
  }
  const open = document.body.classList.toggle('sidebar-open');
  $('sidebar-overlay').hidden = !open;
  $('sidebar-toggle').setAttribute('aria-expanded', String(open));
});

$('sidebar-overlay').addEventListener('click', closeSidebarOnMobile);

/* Sidebar shortcuts open the same dialogs as the page-header buttons. */
document.querySelectorAll('[data-open]').forEach((item) => {
  item.addEventListener('click', () => {
    $(item.dataset.open).click();
    closeSidebarOnMobile();
  });
});

/* Crossing the breakpoint resets the drawer to its default per-mode state. */
window.matchMedia('(min-width: 992px)').addEventListener('change', (event) => {
  document.body.classList.remove('sidebar-open', 'sidebar-collapsed');
  $('sidebar-overlay').hidden = true;
  $('sidebar-toggle').setAttribute('aria-expanded', String(event.matches));
});

/* ---------- compose ---------- */

const GSM_BASIC = '@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&\'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà';
const GSM_EXTENDED = '^{}\\[~]|€';

/** Mirrors the server-side analysis so the counter is accurate before sending. */
function analyze(text) {
  let septets = 0;
  for (const char of text) {
    if (GSM_BASIC.includes(char)) septets += 1;
    else if (GSM_EXTENDED.includes(char)) septets += 2;
    else {
      const units = text.length; // UTF-16 code units, same as UCS-2 billing
      return { encoding: 'UCS-2', chars: units, segments: units <= 70 ? 1 : Math.ceil(units / 67) };
    }
  }
  return { encoding: 'GSM-7', chars: septets, segments: septets <= 160 ? 1 : Math.ceil(septets / 153) };
}

function updateCounter() {
  const text = $('c-message').value;
  const result = analyze(text);
  const el = $('c-counter');
  el.textContent = `${result.chars} characters - ${result.segments} segment${result.segments === 1 ? '' : 's'} (${result.encoding})`;
  el.dataset.over = result.segments > 1 ? 'true' : 'false';
}

$('c-message').addEventListener('input', updateCounter);

$('compose-btn').addEventListener('click', () => {
  $('compose-result').hidden = true;
  updateCounter();
  $('compose').showModal();
  $('c-message').focus();
});

$('compose-form').addEventListener('submit', async (event) => {
  event.preventDefault();

  const payload = {
    sender: $('c-sender').value.trim(),
    recipients: $('c-recipients').value.split(',').map((s) => s.trim()).filter(Boolean),
    message: $('c-message').value,
    sandbox: $('c-sandbox').checked,
  };

  const scheduled = $('c-scheduled').value.trim();
  const callback = $('c-callback').value.trim();
  if (scheduled) payload.scheduled_date = scheduled;
  if (callback) payload.callback_url = callback;

  const result = $('compose-result');
  const submit = event.target.querySelector('button[type="submit"]');
  submit.disabled = true;

  try {
    const response = await api('/api/control/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
    result.hidden = false;
    result.textContent = JSON.stringify(response, null, 2);
    toast('Message captured');
    await loadMessages();
  } catch (error) {
    result.hidden = false;
    result.textContent = JSON.stringify({ status: error.status ?? 500, body: error.body ?? error.message }, null, 2);
    toast(`Send failed: ${error.message}`, 'error');
  } finally {
    submit.disabled = false;
  }
});

/* ---------- providers ---------- */

/** Shows where to point an SMTP client; also used by the empty state. */
async function loadSmtp() {
  try {
    state.smtp = await api('/api/control/smtp');
    const address = state.smtp.address;
    $('smtp-address').textContent = address;
    $('smtp-address').dataset.copy = address;
    $('smtp-address-empty').textContent = address;
    $('smtp-hint').textContent = state.smtp.hint;
    $('smtp-hint-empty').textContent = state.smtp.enabled ? '' : state.smtp.hint;
    $('smtp-empty-line').hidden = !state.smtp.enabled;
  } catch (error) {
    console.error('Failed to load SMTP status', error);
  }
}

async function loadProviders() {
  try {
    const data = await api('/api/control/providers');
    state.providers = data.providers || [];
    state.baseUrl = data.baseUrl;
    state.startupRestricted = !!data.startupRestricted;

    const select = $('provider-filter');
    const current = select.value;
    select.innerHTML = '<option value="">All providers</option>' +
      state.providers.map((p) => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.displayName)}</option>`).join('');
    select.value = current;

    renderProviders();
  } catch (error) {
    toast(error.message, 'error');
  }
}

function renderProviders() {
  const host = $('provider-list');
  if (!state.providers.length) {
    host.innerHTML = '<p class="hint">No providers loaded.</p>';
    return;
  }

  $('providers-note').innerHTML = state.startupRestricted
    ? 'Only the providers below were loaded (<code>--provider</code>). Restart without it to load the rest.'
    : 'Point your app\'s base URL at this server and use the credentials below. Toggle a provider off to make its routes answer <code>404</code>.';

  host.innerHTML = state.providers.map((p) => `
    <section class="provider-card" data-id="${escapeHtml(p.id)}">
      <div class="provider-head">
        <div>
          <h3>${escapeHtml(p.displayName)}</h3>
          <p class="provider-summary">${escapeHtml(p.summary)}</p>
        </div>
        <label class="switch" title="Serve this provider's routes">
          <input type="checkbox" data-toggle="${escapeHtml(p.id)}" ${p.enabled ? 'checked' : ''}>
          <span>${p.enabled ? 'serving' : 'off'}</span>
        </label>
      </div>

      <h4 class="provider-label">Base URL</h4>
      <p class="provider-base"><code data-copy="${escapeHtml(state.baseUrl)}" title="Click to copy">${escapeHtml(state.baseUrl)}</code></p>

      <h4 class="provider-label">Endpoints</h4>
      <ul class="provider-endpoints">
        ${p.endpoints.map((e) => `
          <li>
            <span class="verb">${escapeHtml(e.method)}</span>
            <code data-copy="${escapeHtml(state.baseUrl + e.path)}" title="Click to copy">${escapeHtml(e.path)}</code>
            <span class="endpoint-desc">${escapeHtml(e.description)}</span>
          </li>`).join('')}
      </ul>

      <h4 class="provider-label">Credentials</h4>
      <ul class="provider-creds">
        ${Object.entries(p.credentials).map(([field, value]) => {
          const meta = (p.credentials || {}) && field;
          return `
            <li>
              <span class="cred-field">${escapeHtml(field)}</span>
              <code data-copy="${escapeHtml(value)}" title="Click to copy">${escapeHtml(value)}</code>
            </li>`;
        }).join('')}
      </ul>

      <p class="provider-docs">
        <a href="${escapeHtml(p.docsUrl)}" target="_blank" rel="noreferrer noopener">${escapeHtml(p.docsUrl)}</a>
      </p>
    </section>`).join('');

  host.querySelectorAll('[data-toggle]').forEach((input) => {
    input.addEventListener('change', async (event) => {
      const id = event.target.dataset.toggle;
      const provider = state.providers.find((entry) => entry.id === id);
      if (provider) provider.enabled = event.target.checked;
      event.target.closest('.switch').querySelector('span').textContent =
        event.target.checked ? 'serving' : 'off';

      const disabled = state.providers.filter((entry) => !entry.enabled).map((entry) => entry.id);
      try {
        await api('/api/control/settings', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ disabledProviders: disabled }),
        });
        toast(`${providerName(id)} ${event.target.checked ? 'enabled' : 'disabled'}`);
        await loadMessages();
      } catch (error) {
        toast(error.message, 'error');
        await loadProviders();
      }
    });
  });
}

$('providers-btn').addEventListener('click', async () => {
  await loadProviders();
  await loadSmtp();
  $('providers').showModal();
});

/* copy any [data-copy] element inside dialogs */
document.addEventListener('click', async (event) => {
  const cell = event.target.closest('[data-copy]');
  if (!cell || cell.closest('#detail')) return; // handled by the detail listener
  try {
    await navigator.clipboard.writeText(cell.dataset.copy);
    toast('Copied');
  } catch {
    toast('Clipboard unavailable', 'error');
  }
});

/* ---------- settings ---------- */

async function loadSettings() {
  try {
    state.settings = await api('/api/control/settings');
    $('s-enforce').checked = !!state.settings.enforceCredentials;
    $('s-error').value = state.settings.forceError || '';
    $('s-delay').value = state.settings.deliverAfterMs;
    $('s-sms-balance').value = state.settings.smsBalance;
    $('s-main-balance').value = state.settings.mainBalance;
  } catch (error) {
    toast(error.message, 'error');
  }
}

$('settings-btn').addEventListener('click', async () => {
  await loadSettings();
  $('settings').showModal();
});

$('settings-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    state.settings = await api('/api/control/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        enforceCredentials: $('s-enforce').checked,
        forceError: $('s-error').value,
        deliverAfterMs: Number($('s-delay').value),
        smsBalance: Number($('s-sms-balance').value),
        mainBalance: $('s-main-balance').value,
      }),
    });
    toast('Settings saved');
    $('settings').close();
    await loadMessages();
  } catch (error) {
    toast(error.message, 'error');
  }
});

/* close buttons inside dialogs */
document.querySelectorAll('[data-close]').forEach((button) => {
  button.addEventListener('click', () => button.closest('dialog')?.close());
});

/* ---------- live updates ---------- */

function connectEvents() {
  const source = new EventSource('/api/control/events');
  const live = $('live');
  const label = $('live-label');

  source.onopen = () => {
    live.dataset.state = 'online';
    label.textContent = 'live';
  };

  source.onerror = () => {
    live.dataset.state = 'offline';
    label.textContent = 'reconnecting';
  };

  source.onmessage = (event) => {
    let payload;
    try { payload = JSON.parse(event.data); } catch { return; }

    if (payload.type === 'delete' && payload.id) {
      state.messages = state.messages.filter((m) => m.id !== payload.id);
      if (state.selectedId === payload.id) state.selectedId = null;
      renderList();
      renderDetail();
      return;
    }
    if (payload.type === 'settings') {
      loadSettings();
      return;
    }
    // message / update / clear: refetch so active filters stay honest
    scheduleReload();
  };
}

/** Separate stream: mail arrives over SMTP, not through a provider API call. */
function connectEmailEvents() {
  const source = new EventSource('/api/control/emails/events');

  source.onmessage = (event) => {
    let payload;
    try { payload = JSON.parse(event.data); } catch { return; }

    if (payload.type === 'delete' && payload.id && state.emails.some((e) => e.id === payload.id)) {
      state.emails = state.emails.filter((e) => e.id !== payload.id);
      if (state.selectedEmailId === payload.id) state.selectedEmailId = null;
      renderEmailList();
      renderEmailDetail();
      return;
    }

    clearTimeout(emailReloadTimer);
    emailReloadTimer = setTimeout(loadEmails, 60);
  };
}

let emailReloadTimer;

/* ---------- boot ---------- */

updateCounter();
loadProviders();
loadSmtp();
loadMessages();
loadEmails();
loadSettings();
connectEvents();
connectEmailEvents();
setChannel('sms');
setInterval(tickOtpRemaining, 1000);
