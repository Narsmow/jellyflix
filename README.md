# Jellyflix — a Netflix-style frontend for Jellyfin

A Jellyfin plugin with a Netflix-style UI, smarter recommendations, and **counter-seasonal awareness** — if you're the kind of person who watches slashers on Valentine's Day or action movies on Christmas Eve, Jellyflix notices and leans into it instead of fighting it.

**One click to install. Everything is bundled — no separate downloads, no extra hosting.**

---

## Install (for users)

You do this **once**. Future updates are automatic via Jellyfin's plugin catalog.

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**.
2. Click the **+** button and add a new repository:
   - **Name:** `Jellyflix`
   - **Repository URL:** `https://raw.githubusercontent.com/Narsmow/jellyflix/main/manifest.json`
3. Go to **Dashboard → Plugins → Catalog**. "Jellyflix" will be in the list under General.
4. Click **Install**. Wait for it to download.
5. Restart Jellyfin when prompted.
6. After restart, go to **Dashboard → Plugins → Jellyflix**. You'll see a **green RUNNING badge** and a list of self-checks. If anything isn't healthy, it's listed there with a red ✗.
7. Click **Open Jellyflix →**. That's the new UI.

That's it. The web UI is served by the plugin itself at `/Jellyflix/Web/` on your existing Jellyfin instance — no separate process, no Node.js, no Docker container.

### Why no manual download step?

The web UI (HTML/CSS/JS) is compiled directly into the plugin DLL as embedded resources (see `Jellyfin.Plugin.Jellyflix.csproj` → `<EmbeddedResource>`). When Jellyfin downloads the plugin zip from the catalog, it gets the whole thing. The plugin then serves its own web UI over HTTP from inside the Jellyfin process. One artifact, one click.

If a future version ever needs a large optional resource that can't reasonably ship in the zip (say, an ML model), the bootstrapper has a hook for downloading it on first run into Jellyfin's `ApplicationPaths.DataPath`. It's currently empty because we don't need it.

---

## For you — the maintainer

You're the one deploying this. Flow:

1. Create a GitHub repo named `jellyflix` and push this code.
2. Edit `manifest.json` — replace `YOUR_GITHUB_USERNAME` with your real username.
3. Push a tag: `git tag v1.0.0 && git push origin v1.0.0`
4. GitHub Actions will:
   - Build the plugin
   - Zip the DLL + meta.json
   - Create a GitHub Release with the zip attached
   - Append a new version entry to `manifest.json` and commit it back to `main`
5. Your users' Jellyfin servers auto-refresh the manifest and offer the new version in the catalog.

The workflow is in `.github/workflows/release.yml`. First-run setup: the repo needs **Settings → Actions → General → Workflow permissions** set to "Read and write permissions" so it can push the manifest update.

### Local development

```bash
# Prerequisites: .NET 9 SDK
dotnet restore
dotnet build --configuration Release

# Run the unit tests
dotnet test

# Grab the built DLL + a hand-written meta.json
mkdir dist
cp bin/Release/net9.0/Jellyfin.Plugin.Jellyflix.dll dist/
cat > dist/meta.json <<'EOF'
{"guid":"f2c9d8e4-3a1b-4c6d-9e7f-8a2b1c3d4e5f","name":"Jellyflix","version":"0.1.0.0","targetAbi":"10.11.0.0"}
EOF

# Copy into Jellyfin's plugin directory and restart
# Linux default:   ~/.local/share/jellyfin/plugins/Jellyflix_0.1.0.0/
# Docker default:  /config/data/plugins/Jellyflix_0.1.0.0/
```

The web UI calls the plugin's REST endpoints and Jellyfin's native API — to iterate on it against a running Jellyfin server, open `/Jellyflix/Web/?server=<url>&token=<access-token>&userId=<guid>` in standalone mode, or rebuild the DLL to test embedded-dashboard mode.

---

## Architecture

```
┌────────────────────────────────────────────────────────┐
│  Browser                                               │
│  /Jellyflix/Web/       ← embedded SPA                  │
│     ↓ fetches                                          │
│  /Users/…              ← native Jellyfin (items, auth) │
│  /Jellyflix/Rails/…    ← this plugin                   │
│  /Jellyflix/Health     ← this plugin                   │
└────────────────────────────────────────────────────────┘
                            │ HTTP
┌───────────────────────────▼────────────────────────────┐
│  Jellyfin server                                       │
│  ┌─────────────────────────────────────────────────┐   │
│  │  Jellyflix plugin DLL                           │   │
│  │  ├─ Plugin.cs               config page reg     │   │
│  │  ├─ PluginServiceRegistrator   DI wiring        │   │
│  │  ├─ Services/                                   │   │
│  │  │   ├─ Bootstrapper        startup + health    │   │
│  │  │   ├─ HealthState                             │   │
│  │  │   ├─ SeasonalContext                         │   │
│  │  │   ├─ AntiPatternDetector                     │   │
│  │  │   └─ RecommendationEngine                    │   │
│  │  ├─ Api/                                        │   │
│  │  │   ├─ RecommendationController                │   │
│  │  │   ├─ HealthController                        │   │
│  │  │   └─ StaticFilesController                   │   │
│  │  └─ Embedded resources: configPage.html,        │   │
│  │       web/index.html, app.css, app.js           │   │
│  └─────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────┘
```

---

## The recommendation engine

Three layers:

1. **Item-to-item similarity** — "Because you watched X" rails using Jellyfin's item metadata.
2. **Seasonal boost** — when you're inside a seasonal window (Halloween, Christmas, Valentine's, etc.), push items with matching tags.
3. **Counter-seasonal detection** — _the novel layer_. For each seasonal window, check the user's historical tag overlap during that window. Four outcomes:
   - **Pro-seasonal** (overlap ≥ 0.6) — lean hard into expected tags.
   - **Counter-seasonal** (overlap ≤ 0.15) — pivot. Extract what they actually watch during this window, subtract their year-round baseline, and recommend items matching the remainder (their "period signature").
   - **Neutral** — blend expected and baseline.
   - **Not enough history** — mild seasonal nudge, degrade gracefully.

See `src/Services/AntiPatternDetector.cs` for the full implementation. Thresholds are user-tunable on the admin config page.

### Rails

- **Continue Watching** — in-progress items; abandoned (idle > 30 days) ones hidden.
- **Right Now** — the adaptive seasonal/counter-seasonal rail.
- **Because You Watched X** — per recent completion.
- **New to your library** — recently added items filtered by your genre affinity.
- **Hidden Gems** — high-rated unwatched items in your library.
- **For You** — long-tail preferences across all history.

A **Mood** strip above the rails (cozy/thrilling/mindless/challenging/short/epic) busts the per-user cache on selection. Server-side mood-aware filtering is a future enhancement.

### UX over Netflix

- No autoplay on hover by default (configurable)
- "Why this?" on every rail — transparency
- Finished vs. abandoned distinguished properly
- Mood selector — session-scoped, no profile contamination
- Library-aware — no upsell, no "leaving soon"
- No dark patterns

---

## Project layout

```
JellyfinNetflix/
├── README.md                                   ← this file
├── Jellyfin.Plugin.Jellyflix.csproj            ← project definition
├── manifest.json                               ← Jellyfin repo manifest
├── .gitignore
├── .github/workflows/release.yml               ← automated builds on tag push
│
├── src/
│   ├── Plugin.cs                               plugin entry point
│   ├── PluginServiceRegistrator.cs             DI wiring
│   ├── Configuration/
│   │   ├── PluginConfiguration.cs              user-tunable settings
│   │   └── configPage.html                     admin UI
│   ├── Services/
│   │   ├── Bootstrapper.cs                     startup self-checks
│   │   ├── HealthState.cs                      thread-safe status store
│   │   ├── SeasonalContext.cs                  window detection
│   │   ├── AntiPatternDetector.cs              counter-seasonal brain
│   │   └── RecommendationEngine.cs             rail composition
│   ├── Api/
│   │   ├── RecommendationController.cs         /Jellyflix/Rails/…
│   │   ├── HealthController.cs                 /Jellyflix/Health
│   │   └── StaticFilesController.cs            /Jellyflix/Web/…
│   └── Models/
│       └── Rail.cs                             rail DTO
│
└── web/                                        source files (compiled into DLL)
    ├── index.html
    └── assets/
        ├── app.css
        └── app.js
```

---

## Status

All rails are implemented and wired end-to-end. The plugin is production-ready for Jellyfin 10.11.

- ✅ `AntiPatternDetector` + `SeasonalContext` — core algorithm, unit-tested
- ✅ All rail builders populated from `ILibraryManager` / `IUserDataManager`
- ✅ Frontend calls the live REST API (no more mock data), loading + error states
- ✅ 10-minute per-user cache (`DELETE /Jellyflix/Rails/{userId}/cache` to force-refresh)
- ✅ `GET /Jellyflix/Profile/Insights/{userId}` for the dashboard
- ✅ Authorization enforced — non-admins can only query their own userId
- ✅ `CancellationToken` plumbed through the engine

Known limitations (tracked as future enhancements, not blockers):

- Mood chips bust the cache but don't yet pass mood state to the server — server-side mood filtering is a future enhancement.
- Continue Watching surfaces the episode you left off on, not the next unwatched one — matches typical player behaviour but could be smarter.
- History is capped at the most-recent 500 played items for per-request analysis; users with very long viewing histories may have older patterns underweighted.
