/* ══════════════════════════════════════════════════════════════════
   Jellyflix frontend
   Credential resolution order:
     1. window.ApiClient (set when embedded as a Jellyfin dashboard page)
     2. URL query params (?server=&token=&userId= — explicit / dev override)
     3. Jellyfin's own localStorage 'jellyfin_credentials' (same-origin shared
        with /web/, so any tab signed in to Jellyfin authorises this page too)
     4. None — render a "Sign in to Jellyfin" prompt with a return-redirect
   ══════════════════════════════════════════════════════════════════ */

// ── Bootstrap ───────────────────────────────────────────────────────────────

const Api = (() => {
  const client = window.ApiClient ?? window.parent?.ApiClient ?? null;
  const params = new URLSearchParams(location.search);

  function fromLocalStorage() {
    try {
      const raw = localStorage.getItem('jellyfin_credentials');
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      const server = (parsed.Servers || []).find(s => s.AccessToken && s.UserId);
      if (!server) return null;
      const addr = server.ManualAddress || server.LocalAddress || location.origin;
      return {
        base:   addr.replace(/\/$/, ''),
        token:  server.AccessToken,
        userId: server.UserId
      };
    } catch {
      return null;
    }
  }

  const ls = !client && !params.get('userId') ? fromLocalStorage() : null;

  const base = client
    ? client.serverAddress().replace(/\/$/, '')
    : (params.get('server') ?? ls?.base ?? location.origin).replace(/\/$/, '');

  const token = client
    ? client.accessToken()
    : (params.get('token') ?? ls?.token ?? '');

  const userId = client
    ? client.getCurrentUserId()
    : (params.get('userId') ?? ls?.userId ?? '');

  function headers() {
    const h = { 'Content-Type': 'application/json' };
    if (token) h['X-Emby-Token'] = token;
    return h;
  }

  async function request(method, path, signal) {
    const res = await fetch(`${base}${path}`, { method, headers: headers(), signal });
    if (!res.ok) {
      const msg = res.status === 401 || res.status === 403
        ? 'Sign in to Jellyfin to continue.'
        : res.status === 404
          ? 'Not found.'
          : `Server error (${res.status}).`;
      const err = new Error(msg);
      err.status = res.status;
      throw err;
    }
    if (res.status === 204) return null;
    return res.json();
  }

  return {
    base, token, userId,
    get:    (path, signal)    => request('GET',    path, signal),
    delete: (path, signal)    => request('DELETE', path, signal),
  };
})();

// ── State ────────────────────────────────────────────────────────────────────

let activeMoods      = new Set();
let inflightLoad     = null;  // AbortController for the current rails request
let moodReloadTimer  = null;  // debounce handle for rapid mood chip clicks

// ── Entry point ──────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', () => {
  wireProfileButton();
  wireMoodChips();
  wireModal();
  wireUiSwitch();
  loadRails();
});

function wireUiSwitch() {
  const btn = document.getElementById('ui-switch');
  if (!btn) return;
  btn.addEventListener('click', () => {
    const url = `${Api.base}/web/index.html`;
    // If embedded inside the Jellyfin dashboard, navigate the parent;
    // otherwise navigate the current window.
    const target = window.parent && window.parent !== window ? window.parent : window;
    target.location.href = url;
  });
}

// ── Rails loading ────────────────────────────────────────────────────────────

async function loadRails() {
  const root = document.getElementById('rails');
  if (!Api.userId) {
    renderSignInPrompt(root);
    return;
  }

  // Preserve per-rail scroll positions so a mood toggle doesn't lose the user's place.
  const scrollSnapshot = snapshotRailScroll(root);

  // Cancel any in-flight request so rapid reloads don't race.
  inflightLoad?.abort();
  inflightLoad = new AbortController();
  const signal = inflightLoad.signal;

  // Show a spinner only if this is the initial load (empty root) — otherwise
  // keep the previous rails visible until the new data arrives to avoid flicker.
  if (!root.firstChild || root.firstChild.classList?.contains('rail-error')) {
    root.innerHTML = '<div class="rails-loading" role="status" aria-label="Loading recommendations"><span class="spinner"></span></div>';
  }

  try {
    const rails = await Api.get(`/Jellyflix/Rails/${Api.userId}`, signal);

    if (!Array.isArray(rails) || rails.length === 0) {
      renderMessage(root,
        'Your library is empty, or no recommendations are ready yet. Watch a few things and check back.');
      return;
    }

    const allIds = [...new Set(rails.flatMap(r => r.itemIds ?? []))];
    const meta   = allIds.length ? await fetchItemMeta(allIds, signal) : {};

    if (signal.aborted) return;

    root.innerHTML = '';
    for (const rail of rails) {
      const ids = (rail.itemIds ?? []).filter(id => meta[id]);
      if (ids.length === 0) continue;
      root.appendChild(renderRail(rail, ids.map(id => meta[id])));
    }

    // Edge case: rails came back with item IDs, but every metadata lookup
    // failed (deleted items, parental-control filter, etc.). Don't leave
    // the user staring at a blank screen.
    if (root.childElementCount === 0) {
      renderMessage(root,
        'Recommendations were generated but no item details could be loaded. ' +
        'Some items may have been removed from the library.');
      return;
    }

    restoreRailScroll(root, scrollSnapshot);

    const firstId = rails.flatMap(r => r.itemIds ?? [])[0];
    if (firstId && meta[firstId]) wireHero(meta[firstId]);

  } catch (err) {
    if (err.name === 'AbortError') return;
    console.error('[Jellyflix] Failed to load rails:', err);
    renderErrorWithRetry(root, err.message || 'Could not load recommendations.');
  }
}

function snapshotRailScroll(root) {
  const map = new Map();
  root.querySelectorAll('.rail').forEach(rail => {
    const key = rail.dataset.key;
    const scroller = rail.querySelector('.rail-scroller');
    if (key && scroller) map.set(key, scroller.scrollLeft);
  });
  return map;
}

function restoreRailScroll(root, snapshot) {
  if (!snapshot || snapshot.size === 0) return;
  root.querySelectorAll('.rail').forEach(rail => {
    const saved = snapshot.get(rail.dataset.key);
    if (saved === undefined) return;
    const scroller = rail.querySelector('.rail-scroller');
    if (scroller) scroller.scrollLeft = saved;
  });
}

async function fetchItemMeta(ids, signal) {
  const chunkSize = 50;
  const out = {};

  for (let i = 0; i < ids.length; i += chunkSize) {
    if (signal?.aborted) return out;

    const chunk = ids.slice(i, i + chunkSize).join(',');
    const path  = `/Users/${Api.userId}/Items?ids=${chunk}&Fields=Genres,Tags,Overview,RunTimeTicks,ProductionYear,CommunityRating,PrimaryImageAspectRatio&EnableImages=true`;

    let result;
    try {
      result = await Api.get(path, signal);
    } catch (err) {
      if (err.name === 'AbortError') return out;
      // A single chunk failure shouldn't wipe out the whole rail — log and continue.
      console.warn('[Jellyflix] Metadata chunk failed:', err);
      continue;
    }

    for (const item of result.Items ?? []) {
      const imgBase     = `${Api.base}/Items/${item.Id}/Images`;
      const hasBackdrop = (item.BackdropImageTags ?? []).length > 0;
      const hasPrimary  = Boolean(item.ImageTags?.Primary);

      out[item.Id] = {
        id:          item.Id,
        name:        item.Name ?? '',
        year:        item.ProductionYear ?? '',
        overview:    item.Overview ?? '',
        genres:      (item.Genres ?? []).slice(0, 2).join(', '),
        rating:      item.CommunityRating ? item.CommunityRating.toFixed(1) : '',
        runtime:     runtimeLabel(item.RunTimeTicks),
        heroUrl:     hasBackdrop ? `${imgBase}/Backdrop/0?width=1600&quality=80` : null,
        // Poster srcset — 1x (220px card) and 2x (440px for Retina/4K).
        posterUrl:   hasPrimary ? `${imgBase}/Primary?width=220&quality=80` : null,
        poster2xUrl: hasPrimary ? `${imgBase}/Primary?width=440&quality=80` : null,
        progress:    item.UserData?.PlayedPercentage
                       ? item.UserData.PlayedPercentage / 100
                       : 0
      };
    }
  }

  return out;
}

function renderMessage(root, message) {
  root.innerHTML = '';
  const p = document.createElement('p');
  p.className   = 'rail-error';
  p.textContent = message;
  root.appendChild(p);
}

function renderSignInPrompt(root) {
  root.innerHTML = '';
  const wrap = document.createElement('div');
  wrap.className = 'rail-error';

  const p = document.createElement('p');
  p.textContent = "You're not signed in to Jellyfin in this browser. Sign in there first, then come back.";
  wrap.appendChild(p);

  const link = document.createElement('a');
  link.className   = 'rail-retry';
  link.textContent = 'Sign in to Jellyfin →';
  link.href        = `${location.origin}/web/index.html`;
  // After signing in, send them straight back here.
  try {
    const ret = encodeURIComponent(location.pathname + location.search);
    link.href = `${location.origin}/web/index.html?returnUrl=${ret}`;
  } catch { /* keep plain href */ }
  wrap.appendChild(link);

  root.appendChild(wrap);
}

function renderErrorWithRetry(root, message) {
  root.innerHTML = '';

  const wrap = document.createElement('div');
  wrap.className = 'rail-error';

  const p = document.createElement('p');
  p.textContent = message;
  wrap.appendChild(p);

  const retry = document.createElement('button');
  retry.type        = 'button';
  retry.className   = 'rail-retry';
  retry.textContent = 'Try again';
  retry.addEventListener('click', () => loadRails());
  wrap.appendChild(retry);

  root.appendChild(wrap);
}

// ── Hero ─────────────────────────────────────────────────────────────────────

function wireHero(item) {
  document.getElementById('hero')?.classList.remove('hero-loading');
  document.querySelector('.hero-title').textContent = item.name;
  document.querySelector('.hero-meta').textContent  =
    [item.year, item.runtime, item.genres, item.rating ? `★ ${item.rating}` : '']
      .filter(Boolean).join(' · ');

  const desc = document.querySelector('.hero-desc');
  if (item.overview) {
    desc.textContent = item.overview;
    desc.style.display = '';
  } else {
    desc.style.display = 'none';
  }

  const bg = document.getElementById('hero-bg');
  if (item.heroUrl) {
    // Preload to avoid a flash of the gradient → image transition.
    const probe = new Image();
    probe.onload  = () => { bg.style.backgroundImage = `url(${item.heroUrl})`; };
    probe.onerror = () => { /* keep the gradient fallback */ };
    probe.src = item.heroUrl;
  }

  document.querySelector('.eyebrow').textContent = "Tonight's pick";

  document.getElementById('hero-play').onclick = () => playItem(item.id);
  document.getElementById('hero-info').onclick = () => openItemDetails(item.id);
}

function openItemDetails(itemId) {
  const apiClient = window.ApiClient ?? window.parent?.ApiClient ?? null;
  if (apiClient) {
    // Navigate the parent dashboard to Jellyfin's native details page.
    window.parent.location.href = `${Api.base}/web/index.html#!/details?id=${itemId}`;
  } else {
    window.open(`${Api.base}/web/index.html#!/details?id=${itemId}`, '_blank', 'noopener');
  }
}

// ── Rail rendering ───────────────────────────────────────────────────────────

function renderRail(rail, items) {
  const el = document.createElement('section');
  el.className    = 'rail';
  el.dataset.key  = rail.key;

  const head = document.createElement('div');
  head.className = 'rail-head';

  const h2 = document.createElement('h2');
  h2.className   = 'rail-title';
  h2.textContent = rail.title;
  head.appendChild(h2);

  if (rail.badge) {
    const b = document.createElement('span');
    b.className   = 'rail-badge';
    b.textContent = rail.badge;
    head.appendChild(b);
  }

  if (rail.explanation) {
    const why = document.createElement('button');
    why.className    = 'rail-why';
    why.type         = 'button';
    why.textContent  = 'why this?';
    why.setAttribute('aria-label', `Why this row: ${rail.title}`);
    why.addEventListener('click', () => openWhy(rail.title, rail.explanation));
    head.appendChild(why);
  }

  el.appendChild(head);

  const scrollWrap = document.createElement('div');
  scrollWrap.className = 'rail-scroll-wrap';

  const scroller = document.createElement('div');
  scroller.className = 'rail-scroller';
  scroller.setAttribute('role', 'list');
  items.forEach(item =>
    scroller.appendChild(renderCard(item, rail.key === 'continue-watching')));
  scrollWrap.appendChild(scroller);

  // Scroll arrow buttons (shown on hover via CSS, hidden at edges via state).
  scrollWrap.appendChild(makeScrollArrow(scroller, 'left'));
  scrollWrap.appendChild(makeScrollArrow(scroller, 'right'));
  el.appendChild(scrollWrap);

  // Initial edge state + update on scroll.
  queueMicrotask(() => updateArrowState(scrollWrap, scroller));
  scroller.addEventListener('scroll', () => updateArrowState(scrollWrap, scroller), { passive: true });

  return el;
}

function makeScrollArrow(scroller, dir) {
  const btn = document.createElement('button');
  btn.type          = 'button';
  btn.className     = `rail-arrow rail-arrow-${dir}`;
  btn.setAttribute('aria-label', dir === 'left' ? 'Scroll left' : 'Scroll right');
  btn.textContent   = dir === 'left' ? '‹' : '›';
  btn.addEventListener('click', () => {
    const delta = Math.max(scroller.clientWidth * 0.8, 230);
    scroller.scrollBy({ left: dir === 'left' ? -delta : delta, behavior: 'smooth' });
  });
  return btn;
}

function updateArrowState(wrap, scroller) {
  const max  = scroller.scrollWidth - scroller.clientWidth;
  const left = scroller.scrollLeft;
  // A few px tolerance to handle subpixel rounding.
  wrap.classList.toggle('at-start', left <= 2);
  wrap.classList.toggle('at-end',   left >= max - 2);
  wrap.classList.toggle('no-overflow', max <= 2);
}

function renderCard(item, showProgress) {
  const card = document.createElement('article');
  card.className = 'card';
  card.setAttribute('role', 'listitem');
  card.setAttribute('tabindex', '0');
  card.setAttribute('aria-label', `Play ${item.name}`);

  const activate = () => playItem(item.id);
  card.addEventListener('click', activate);
  card.addEventListener('keydown', ev => {
    if (ev.key === 'Enter' || ev.key === ' ') {
      ev.preventDefault();
      activate();
      return;
    }
    if (ev.key === 'ArrowRight' || ev.key === 'ArrowLeft') {
      const next = ev.key === 'ArrowRight' ? card.nextElementSibling : card.previousElementSibling;
      if (next?.classList.contains('card')) {
        ev.preventDefault();
        next.focus();
        next.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
      }
    }
  });

  const art = document.createElement('div');
  art.className = 'card-art';

  if (item.posterUrl) {
    const img = document.createElement('img');
    img.src      = item.posterUrl;
    if (item.poster2xUrl) {
      img.srcset = `${item.posterUrl} 1x, ${item.poster2xUrl} 2x`;
    }
    img.alt      = '';           // decorative — label provides the name
    img.width    = 220;
    img.height   = 330;          // matches 2:3 aspect ratio, prevents layout shift
    img.loading  = 'lazy';
    img.decoding = 'async';
    img.onerror  = () => { img.remove(); };
    art.appendChild(img);
  }

  if (item.year) {
    const year = document.createElement('span');
    year.className   = 'card-year';
    year.textContent = item.year;
    art.appendChild(year);
  }

  const label = document.createElement('span');
  label.className   = 'card-label';
  label.textContent = item.name;
  art.appendChild(label);

  if (showProgress && item.progress > 0) {
    const p = document.createElement('div');
    p.className = 'card-progress';
    p.style.setProperty('--progress', `${item.progress * 100}%`);
    art.appendChild(p);
  }

  card.appendChild(art);

  const meta = document.createElement('div');
  meta.className = 'card-meta';

  const title = document.createElement('span');
  title.className   = 'card-title';
  title.textContent = item.name;
  title.title       = item.name;  // native tooltip when truncated
  meta.appendChild(title);

  const sub = document.createElement('span');
  sub.className   = 'card-sub';
  sub.textContent = item.genres || item.year || '';
  meta.appendChild(sub);

  card.appendChild(meta);

  return card;
}

// ── Mood chips ────────────────────────────────────────────────────────────────

function wireMoodChips() {
  document.querySelectorAll('.mood').forEach(chip => {
    chip.addEventListener('click', () => {
      chip.classList.toggle('active');
      const mood = chip.dataset.mood;
      chip.setAttribute('aria-pressed', chip.classList.contains('active') ? 'true' : 'false');

      if (chip.classList.contains('active')) {
        activeMoods.add(mood);
      } else {
        activeMoods.delete(mood);
      }

      // Debounce: coalesce rapid chip toggles into a single reload.
      clearTimeout(moodReloadTimer);
      moodReloadTimer = setTimeout(() => {
        if (!Api.userId) return;
        Api.delete(`/Jellyflix/Rails/${Api.userId}/cache`)
           .catch(() => { /* cache bust is best-effort */ })
           .finally(() => loadRails());
      }, 250);
    });

    // Initialize ARIA state.
    chip.setAttribute('aria-pressed', 'false');
  });
}

// ── Profile button ───────────────────────────────────────────────────────────

function wireProfileButton() {
  // Not signed in → swap the profile pill out for a Sign-in button so the
  // user has a clear, always-visible way to authenticate.
  if (!Api.userId) {
    showSignInButton();
    return;
  }

  Api.get(`/Users/${Api.userId}`).then(user => {
    const pill   = document.querySelector('.profile-pill span:not(.avatar):not(.caret)');
    const avatar = document.querySelector('.avatar');
    if (pill && user.Name) {
      pill.textContent = user.Name;
    }
    if (avatar && user.Name) {
      avatar.textContent = user.Name[0].toUpperCase();
      const hue = [...(user.Id ?? '')].reduce((a, c) => a + c.charCodeAt(0), 0) % 360;
      avatar.style.setProperty('--avatar', `hsl(${hue} 60% 45%)`);
    }
  }).catch(err => {
    // 401/403 here means the credentials we found are stale or invalid —
    // surface the same Sign-in path so the user can recover.
    if (err.status === 401 || err.status === 403) {
      showSignInButton();
    }
  });
}

function showSignInButton() {
  const wrap = document.getElementById('profile-switcher');
  if (!wrap) return;

  const ret = encodeURIComponent(location.pathname + location.search);
  const href = `${location.origin}/web/index.html?returnUrl=${ret}`;

  wrap.innerHTML = '';
  const a = document.createElement('a');
  a.href      = href;
  a.className = 'sign-in-btn';
  a.textContent = 'Sign in';
  wrap.appendChild(a);
}

// ── Playback ─────────────────────────────────────────────────────────────────

function playItem(itemId) {
  const apiClient = window.ApiClient ?? window.parent?.ApiClient ?? null;

  if (apiClient && typeof window.require === 'function') {
    window.require(['playbackManager'], pm => {
      pm.play({ ids: [itemId], userId: Api.userId, serverId: apiClient.serverId() });
    });
    return;
  }

  // Standalone: hand off to Jellyfin's web player.
  window.open(`${Api.base}/web/index.html#!/item?id=${itemId}`, '_blank', 'noopener');
}

// ── Why modal ─────────────────────────────────────────────────────────────────

let whyModal, whyTitle, whyBody;

function wireModal() {
  whyModal = document.getElementById('why-modal');
  whyTitle = document.getElementById('why-title');
  whyBody  = document.getElementById('why-body');

  // Close button
  document.querySelector('.why-close').addEventListener('click', () => whyModal.close());

  // Click outside the modal content closes it (dialog backdrop).
  whyModal.addEventListener('click', (e) => {
    if (e.target === whyModal) whyModal.close();
  });
  // Escape is handled natively by <dialog>.
}

function openWhy(title, explanation) {
  whyTitle.textContent = title;
  whyBody.textContent  = explanation;
  whyModal.showModal();
}

// ── Utilities ────────────────────────────────────────────────────────────────

function runtimeLabel(ticks) {
  if (!ticks) return '';
  const mins = Math.round(ticks / 10_000_000 / 60);
  if (mins < 60) return `${mins}m`;
  const h = Math.floor(mins / 60), m = mins % 60;
  return m > 0 ? `${h}h ${m}m` : `${h}h`;
}
