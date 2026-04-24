/* ══════════════════════════════════════════════════════════════════
   Jellyflix frontend bootstrap
   Renders rails using mocked data. To wire to a real Jellyfin server:
   1. Set JELLYFIN_URL and a user access token
   2. Replace MOCK_RAILS with a call to /Jellyflix/Rails/{userId}
   3. Hydrate item metadata via /Users/{userId}/Items?ids=…
   ══════════════════════════════════════════════════════════════════ */

const JELLYFIN_URL = null;   // e.g. 'https://jellyfin.example.com'
const USER_TOKEN   = null;   // obtained via Jellyfin auth
const USER_ID      = null;

// ─── mock data — simulates what /Jellyflix/Rails would return ──────

const MOCK_RAILS = [
  {
    key: 'continue-watching',
    title: 'Continue watching',
    explanation: "Items you've started but haven't finished. Anything abandoned more than 30 days ago is hidden — you can find it under History.",
    items: [
      mkItem('Severance S2E4', 2024, 'drama', '#1e3a5f', 0.42),
      mkItem('The Bear', 2022, 'comedy/drama', '#3a1f1a', 0.71),
      mkItem('Chernobyl', 2019, 'historical', '#2a2e1f', 0.15),
    ]
  },
  {
    // THIS is the interesting one — counter-seasonal detection fires
    key: 'right-now',
    title: 'Your Valentine\u2019s vibe',
    badge: 'COUNTER-SEASONAL',
    explanation: "It's Valentine's week. Most people want romance right now — you never do. Across the last three Februarys you've leaned into psychological thrillers and cold case documentaries. These match that.",
    items: [
      mkItem('Prisoners',      2013, 'thriller',    '#2b1a1a'),
      mkItem('The Vanishing',  1988, 'thriller',    '#18202a'),
      mkItem('Zodiac',         2007, 'thriller',    '#1f1a15'),
      mkItem('Wind River',     2017, 'thriller',    '#263236'),
      mkItem('Gone Girl',      2014, 'mystery',     '#1a1e22'),
      mkItem('Nightcrawler',   2014, 'thriller',    '#201815'),
    ]
  },
  {
    key: 'because-fargo',
    title: 'Because you finished Fargo',
    explanation: "Similar in tone, writing and visual sensibility — pulled from your library's metadata tags.",
    items: [
      mkItem('No Country for Old Men', 2007, 'thriller', '#2e251c'),
      mkItem('Blood Simple',            1984, 'noir',     '#1a1310'),
      mkItem('A Serious Man',           2009, 'drama',    '#24201a'),
      mkItem('True Detective S1',       2014, 'thriller', '#1c1814'),
    ]
  },
  {
    key: 'hidden-gems',
    title: 'Hidden gems in your library',
    explanation: 'Highly rated films you\u2019ve owned for months and never played.',
    items: [
      mkItem('The Florida Project', 2017, 'drama',   '#3d2a1a'),
      mkItem('A Ghost Story',        2017, 'drama',   '#1f2a2d'),
      mkItem('Paterson',             2016, 'drama',   '#2a2620'),
      mkItem('Burning',              2018, 'mystery', '#1a1f24'),
    ]
  }
];

function mkItem(title, year, sub, color, progress = 0) {
  return { title, year, sub, color, progress };
}

// ─── rendering ────────────────────────────────────────────────────

const railsRoot = document.getElementById('rails');

MOCK_RAILS.forEach(rail => {
  railsRoot.appendChild(renderRail(rail));
});

function renderRail(rail) {
  const el = document.createElement('section');
  el.className = 'rail';
  el.dataset.key = rail.key;

  const head = document.createElement('div');
  head.className = 'rail-head';

  const h2 = document.createElement('h2');
  h2.className = 'rail-title';
  h2.textContent = rail.title;
  head.appendChild(h2);

  if (rail.badge) {
    const b = document.createElement('span');
    b.className = 'rail-badge';
    b.textContent = rail.badge;
    head.appendChild(b);
  }

  const why = document.createElement('button');
  why.className = 'rail-why';
  why.textContent = 'why this?';
  why.addEventListener('click', () => openWhy(rail));
  head.appendChild(why);

  el.appendChild(head);

  const scroller = document.createElement('div');
  scroller.className = 'rail-scroller';
  rail.items.forEach(it => scroller.appendChild(renderCard(it)));
  el.appendChild(scroller);

  return el;
}

function renderCard(item) {
  const card = document.createElement('article');
  card.className = 'card';

  const art = document.createElement('div');
  art.className = 'card-art';
  // synthesize a two-stop poster so every card has a distinct mood
  const stop2 = shade(item.color, -30);
  art.style.setProperty('--poster', `linear-gradient(160deg, ${item.color}, ${stop2})`);

  const year = document.createElement('span');
  year.className = 'card-year';
  year.textContent = item.year;
  art.appendChild(year);

  const label = document.createElement('span');
  label.className = 'card-label';
  label.textContent = item.title;
  art.appendChild(label);

  if (item.progress > 0) {
    const p = document.createElement('div');
    p.className = 'card-progress';
    p.style.setProperty('--progress', `${item.progress * 100}%`);
    art.appendChild(p);
  }

  card.appendChild(art);

  const meta = document.createElement('div');
  meta.className = 'card-meta';
  meta.innerHTML = `
    <span class="card-title">${item.title}</span>
    <span class="card-sub">${item.sub}</span>
  `;
  card.appendChild(meta);

  return card;
}

// darken/lighten hex color by a percentage
function shade(hex, pct) {
  const n = parseInt(hex.slice(1), 16);
  let r = (n >> 16) & 0xff, g = (n >> 8) & 0xff, b = n & 0xff;
  const f = 1 + pct / 100;
  r = Math.max(0, Math.min(255, Math.round(r * f)));
  g = Math.max(0, Math.min(255, Math.round(g * f)));
  b = Math.max(0, Math.min(255, Math.round(b * f)));
  return '#' + ((r << 16) | (g << 8) | b).toString(16).padStart(6, '0');
}

// ─── why modal ────────────────────────────────────────────────────

const whyModal = document.getElementById('why-modal');
const whyTitle = document.getElementById('why-title');
const whyBody  = document.getElementById('why-body');
document.querySelector('.why-close').addEventListener('click', () => whyModal.close());

function openWhy(rail) {
  whyTitle.textContent = rail.title;
  whyBody.textContent  = rail.explanation;
  whyModal.showModal();
}

// ─── mood chips ───────────────────────────────────────────────────

document.querySelectorAll('.mood').forEach(chip => {
  chip.addEventListener('click', () => {
    chip.classList.toggle('active');
    // TODO: POST selected moods to /Jellyflix/Rails/{userId}?mood=… and re-render
  });
});
