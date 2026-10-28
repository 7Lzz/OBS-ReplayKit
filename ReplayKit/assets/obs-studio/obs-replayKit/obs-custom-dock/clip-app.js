const CLIP_HOVER_PROFILES = {
    control: ".tab, .btn, .tab-action, .bulk-icon-btn, .bulk-clear, .icon-btn, .play-toggle-btn, .volume-btn, .settings-btn, .speed-step-btn, .speed-pill, .compress-mode-card, .icon-mini-btn, .favorite-btn, .select-clip-btn",
    row: ".menu-item, .clip-sort-item",
    accent: ".share-menu-item",
    danger: ".delete-menu-item",
    surface: ".volume-control",
  };
  function applyClipHovers(root) { if (window.RKHover) RKHover.apply(root, CLIP_HOVER_PROFILES); }
  applyClipHovers(document);
  // dock lockdown: stop accidental right-click menu / zoom shortcuts. cef treats this popup like a regular web page, so users can right-click and hit zoom in / refresh / back / inspect / etc. -- all of which are surprises in a clip-browser context. mirrored in controls.html and controls_app.html.
  (function lockdown() {
    const blockedCommandKeys = new Set(["+", "=", "-", "_", "0", "r", "R", "f", "F", "l", "L", "o", "O", "p", "P", "s", "S", "u", "U"]);
    const blockedCommandCodes = new Set(["Equal", "Minus", "Digit0", "NumpadAdd", "NumpadSubtract", "Numpad0"]);
    const blockedDevToolsCodes = new Set(["KeyI", "KeyJ", "KeyC"]);
    document.addEventListener("contextmenu", function (e) { e.preventDefault(); }, true);
    document.addEventListener("wheel", function (e) {
      if (e.ctrlKey || e.metaKey) e.preventDefault();
    }, { passive: false, capture: true });
    document.addEventListener("keydown", function (e) {
      const command = e.ctrlKey || e.metaKey;
      if (command && (blockedCommandKeys.has(e.key) || blockedCommandCodes.has(e.code) || (e.shiftKey && blockedDevToolsCodes.has(e.code)))) {
        e.preventDefault();
        return;
      }
      if (e.key === "F5" || e.code === "F5" || e.key === "F12" || e.code === "F12") {
        e.preventDefault();
        return;
      }
      if (e.altKey && (e.key === "ArrowLeft" || e.key === "ArrowRight" || e.code === "ArrowLeft" || e.code === "ArrowRight")) {
        e.preventDefault();
      }
      if (e.key === "BrowserBack" || e.key === "BrowserForward" || e.code === "BrowserBack" || e.code === "BrowserForward") {
        e.preventDefault();
      }
    }, true);
  })();

  // self-style the title bar from inside the popup. previously only the parent dock fired /style-window on a 5-retry schedule; if all 5 hits missed (window not yet visible to enumwindows, or its title hadnt settled to "clips" yet), the popup ended up with a defualt white windows chrome and the generic app icon. triggering from inside the popup -- on domcontentloaded, load, and a few late timeouts -- is the reliable path becuase the popup knows exactly when its own title and visibility are set. we also observe the <title> element so a transient title change (e.g. a fullscreen api setting "clips - fullscreen") would still re-style.
  (function selfStyle() {
    if (window.RKEmbedded) return;
    const origin = (location.protocol === "http:")
      ? location.origin
      : "http://127.0.0.1:8767";
    const url = origin + "/style-window?title=Clips&taskbar=1";
    function poke() {
      try {
        fetch(url + "&t=" + Date.now(), { cache: "no-store" }).catch(function () {});
      } catch (e) {}
    }
    // fire immediately, on domcontentloaded, on load, and a few late retries to cover slow systems where enumwindows still doesnt see the window when the earlier calls landed.
    poke();
    document.addEventListener("DOMContentLoaded", poke);
    window.addEventListener("load", poke);
    [400, 900, 1800, 3500].forEach(function (d) { setTimeout(poke, d); });
    // long-tail self-heal: every 15 s for the lifetime of this popup, ping /style-window again. this recovers the title bar even if the popup happened to load during a helper restart (e.g. user reloaded tools -> scripts so the helper handed off between two obs replaykit processes) and all the bursty retries above fired against a not-yet-bound helper. the endpoint is idempotent server-side so the periodic poke is essentially free.
    setInterval(poke, 15000);
    try {
      const t = document.querySelector("title");
      if (t) new MutationObserver(poke).observe(t, { childList: true });
    } catch (e) {}
  })();

  const HELPER = location.protocol === "http:"
    ? location.origin
    : "http://127.0.0.1:8767";
  // the home view of the obs window is this same page with a big player for the newest clip and two shelves of clips, under the header that RKHomeDashboard draws
  const HOME_MODE = document.body.dataset.view === "home" || new URLSearchParams(location.search).get("home") === "1";
  const HOME_CLIP_LIMIT = 12;
  // a shelf is one row, as many cards as the width has room for; a card is at least HOME_MIN_CARD wide, and HOME_CLIP_LIMIT clips are read, enough for the big player and the widest row
  const HOME_MIN_CARD = 250;
  const HOME_GRID_GAP = 16;
  if (HOME_MODE) document.documentElement.classList.add("rk-home");
  // home shows players and nothing else, so the right-click menu of a card, which belongs to the clips view, never opens there
  if (HOME_MODE) document.addEventListener("contextmenu", (e) => { e.preventDefault(); e.stopPropagation(); }, true);
  // resolves once the home view has drawn its clips for the first time (or found the helper down), so the shell page can hold its reveal for it
  let resolveFirstRender = () => {};
  const firstRender = new Promise((resolve) => { resolveFirstRender = resolve; });

  const POLL_FAST_MS = 2500;
  const POLL_UPLOAD_MS = 250;
  const POLL_IDLE_MS = 1000;
  const CLIPS_FORCE_REFRESH_EVERY = 4;
  const TWO_DAYS_SEC = 2 * 24 * 60 * 60;
  const STREAMABLE_PROCESSING_TIMEOUT_SEC = 30 * 60;
  const VIDEO_SEEK_STEP_SECONDS = 5;
  const PENDING_LINK_CREATION_MS = 20 * 60 * 1000;
  // after a cancel the helper can still report the old job as active (or as error=Cancelled) for a poll or two -- ignore that jobs status this long so the instant card repaint isnt undone.
  const CANCEL_GRACE_MS = 8000;
  const replayKitSettings = { showCodecTags: false, editorAutoplay: false, editorLoopPreview: false, editorSeekStep: 5, editorWheelZoom: true, editorZoomScaling: "smooth", clipAntialiasing: "none" };

  async function loadReplayKitSettings() {
    try {
      const res = await fetch(HELPER + "/settings?t=" + Date.now(), { cache: "no-store" });
      const data = await res.json().catch(() => ({}));
      if (res.ok && data.ok && data.settings) {
        replayKitSettings.editorAutoplay = !!data.settings.editorAutoplay;
        replayKitSettings.editorLoopPreview = !!data.settings.editorLoopPreview;
        replayKitSettings.editorSeekStep = Number(data.settings.editorSeekStep) > 0 ? Number(data.settings.editorSeekStep) : 5;
        replayKitSettings.editorWheelZoom = data.settings.editorWheelZoom !== false;
        replayKitSettings.editorZoomScaling = data.settings.editorZoomScaling === "sharp" ? "sharp" : "smooth";
        document.documentElement.dataset.editorZoom = replayKitSettings.editorZoomScaling;
        replayKitSettings.clipAntialiasing = ["msaa", "fxaa"].includes(data.settings.clipAntialiasing) ? data.settings.clipAntialiasing : "none";
        if (window.RKVideoFx) RKVideoFx.setMode(replayKitSettings.clipAntialiasing);
        const showCodecTags = !!data.settings.showCodecTags;
        if (showCodecTags !== replayKitSettings.showCodecTags) {
          replayKitSettings.showCodecTags = showCodecTags;
          if (loadedClips.length) renderLoadedClips(loadedClips);
        }
      }
    } catch (_) {}
  }
  loadReplayKitSettings();
  window.addEventListener("focus", loadReplayKitSettings);

  // material-style icon path data. ships as .svg files under assets/obs-studio/obs-replayKit/icons/ and arrives as window.RK_ICONS (helper InjectDockIcons); ICON.* holds the inner markup so `<svg viewBox="0 0 24 24">${ICON.X}</svg>` call sites and the VOLUME_ICON assembly below are unchanged
  const _RKI = window.RK_ICONS || {};
  const _svgInner = (s) => (s || "").replace(/^<svg[^>]*>/i, "").replace(/<\/svg>\s*$/i, "");
  const ICON = {
    VOL_MUTED: _svgInner(_RKI["player/volume-muted"]),
    FULLSCREEN: _svgInner(_RKI["player/fullscreen"]),
    THEATER: _svgInner(_RKI["player/theater"]),
    THEATER_EXIT: _svgInner(_RKI["player/theater-exit"]),
    MORE: _svgInner(_RKI["action/more"]),
    EDIT: _svgInner(_RKI["action/edit"]),
    LINK: _svgInner(_RKI["action/link"]),
    TRASH: _svgInner(_RKI["action/trash"]),
    STAR: _svgInner(_RKI["toggle/star"]),
    STAR_BORDER: _svgInner(_RKI["toggle/star-border"]),
    GRID: _svgInner(_RKI["toggle/grid"]),
    CONTENT_CUT: _svgInner(_RKI["action/content-cut"]),
    CONTENT_COPY: _svgInner(_RKI["action/content-copy"]),
    COMPRESS: _svgInner(_RKI["action/compress"]),
    DUPLICATE: _svgInner(_RKI["action/duplicate"]),
    CLIPBOARD: _svgInner(_RKI["action/clipboard"]),
    CHECK_CIRCLE: _svgInner(_RKI["action/check-circle"]),
    SELECT: _svgInner(_RKI["action/select"]),
    FOLDER_OPEN: _svgInner(_RKI["action/folder-open"]),
    PLAY: _svgInner(_RKI["player/play"]),
    PAUSE: _svgInner(_RKI["player/pause"]),
    EDITOR_PLAY: _svgInner(_RKI["editor/play"]),
    EDITOR_PAUSE: _svgInner(_RKI["editor/pause"]),
    FULLSCREEN_EXIT: _svgInner(_RKI["player/fullscreen-exit"]),
    SETTINGS: _svgInner(_RKI["action/gear"]),
    OPEN_EDITOR: _svgInner(_RKI["action/open-editor"]),
    RESET: _svgInner(_RKI["action/reset"]),
  };
  // fill the <svg data-i="..."> placeholders from the same icon map, the way settings.html does
  document.querySelectorAll("svg[data-i]").forEach((el) => { el.innerHTML = _svgInner(_RKI[el.dataset.i]); });

  const COPY_ICON = _svgInner(_RKI["action/copy"]);
  const CANCEL_ICON = _svgInner(_RKI["action/cancel-x"]);

  // one static volume icon: the fixed speaker body plus every wave arc and the mute X, all present at once. syncVolumeUi never rebuilds it -- it just sets data-level on the button and css fades/scales the individual arcs (or the X) in and out, so raising 24%->50% animates only the 2nd wave appearing, not a full redraw.
  const _volMuteParts = ICON.VOL_MUTED.match(/<path[^>]*\/>/g) || [];
  const VOL_SPEAKER = (_volMuteParts[0] || "") + (_volMuteParts[1] || "");
  const VOL_X = (_volMuteParts[2] || "").replace("<path ", '<path class="vol-x" ');
  const VOL_WAVE_ATTRS = 'fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.75"';
  const VOLUME_ICON =
    '<svg viewBox="0 0 24 24">' + VOL_SPEAKER +
    '<path class="vol-wave vol-wave-1" d="M15.5 9.5a3.5 3.5 0 0 1 0 5" ' + VOL_WAVE_ATTRS + '/>' +
    '<path class="vol-wave vol-wave-2" d="M18 7a7 7 0 0 1 0 10" ' + VOL_WAVE_ATTRS + '/>' +
    '<path class="vol-wave vol-wave-3" d="M20.5 4.5a10.5 10.5 0 0 1 0 15" ' + VOL_WAVE_ATTRS + '/>' +
    VOL_X + '</svg>';

  const bulkCompressIcon = document.getElementById("bulk-compress-icon");
  if (bulkCompressIcon) bulkCompressIcon.innerHTML = ICON.COMPRESS;

  // volume state is global so its "sticky" across cards -- youtube-like ux. defualt muted (autoplay wont work otherwise on hover).
  const DEFAULT_VOLUME = 0.6;
  const VolumeState = { muted: true, volume: DEFAULT_VOLUME };
  function applyVolume(video) {
    // a clip that plays its separate audio tracks through the page keeps its own sound off, and the tracks follow the same volume
    if (video._tracksActive) {
      video.muted = true;
      if (video._applyTrackMaster) video._applyTrackMaster();
      return;
    }
    video.muted  = VolumeState.muted;
    video.volume = VolumeState.volume;
  }
  // 0 = muted (show the X), 1/2/3 = that many speaker waves. matches the old <0.4 / <0.85 thresholds.
  function volumeLevel() {
    if (VolumeState.muted || VolumeState.volume <= 0) return 0;
    if (VolumeState.volume < 0.4) return 1;
    if (VolumeState.volume >= 0.85) return 3;
    return 2;
  }
  // paint the icon + slider on one card to reflect volumestate. the svg is built once, then only data-level changes -- css transitions each wave (or the X) in/out on its own, no full redraw.
  function syncVolumeUi(btn, slider) {
    if (btn) {
      if (!btn.querySelector(".vol-wave-1")) btn.innerHTML = VOLUME_ICON;
      btn.dataset.level = String(volumeLevel());
      btn.dataset.tooltip = volumeLevel() === 0 ? "Unmute (M)" : "Mute (M)";
    }
    if (slider) slider.value = Math.round(VolumeState.volume * 100);
  }
  // broadcast: rewrite every cards volume ui + push the new volume to the currently-playing preview (if any). this is what makes the "global volume" ux work -- toggle mute on one card and every other card shows the unmuted icon too.
  function notifyVolumeSync() {
    document.querySelectorAll(".card").forEach((c) => {
      syncVolumeUi(c.querySelector(".volume-btn"), c.querySelector(".volume-slider"));
    });
    syncVolumeUi(document.getElementById("trim-volume-btn"),
                 document.getElementById("trim-volume-slider"));
    const trimVideoEl = document.getElementById("trim-video");
    if (trimVideoEl) applyVolume(trimVideoEl);
    if (typeof activePreview !== "undefined" && activePreview) {
      applyVolume(activePreview);
    }
  }
  // the volume buttons of the cards and of the editor and the m key all flip the same switch; a slider that was dragged to the bottom would stay silent after an unmute, so the volume comes back to the default one
  function toggleMute() {
    VolumeState.muted = !VolumeState.muted;
    if (!VolumeState.muted && VolumeState.volume <= 0) VolumeState.volume = DEFAULT_VOLUME;
    notifyVolumeSync();
  }

  function styleNativeChrome() {
    if (window.RKEmbedded) return;
    fetch(HELPER + "/style-window?title=Clips&taskbar=1&t=" + Date.now(), { cache: "no-store" })
      .catch(() => {});
  }
  styleNativeChrome();
  [500, 1500, 3000, 5000].forEach((delay) => {
    setTimeout(styleNativeChrome, delay);
  });

  const scrollAreaEl = document.querySelector(".scroll-area");
  const favoritesHeadingEl = document.getElementById("favorites-heading");
  const favoritesGridEl    = document.getElementById("favorites-grid");
  const restHeadingEl      = document.getElementById("rest-heading");
  const restHeadingIconEl  = document.getElementById("rest-heading-icon");
  const restHeadingLabelEl = document.getElementById("rest-heading-label");
  // content for the heading above the main grid. "all" is the existing all-tab-plus-favorites case (grid icon, paired with the favorites split below); the other three give each single-filter tab its own header the same way.
  const FILTER_SECTION_HEADING = {
    all:       { icon: ICON.GRID, label: "All clips" },
    favorites: { icon: ICON.STAR, label: "Favorites" },
    linked:    { icon: ICON.LINK, label: "Linked" },
    local:     { icon: ICON.GRID, label: "All clips" },
    drafts:    { icon: ICON.OPEN_EDITOR, label: "Edit drafts" },
  };
  const gridEl = document.getElementById("grid");
  const topSpacerEl = document.getElementById("top-spacer");
  const bottomSpacerEl = document.getElementById("bottom-spacer");
  const emptyEl = document.getElementById("empty");
  const toastEl = document.getElementById("toast");
  const popupVeil = document.getElementById("popup-veil");
  const popupTitle = document.getElementById("popup-title");
  const popupBody = document.getElementById("popup-body");
  const popupOk = document.getElementById("popup-ok");
  const resizeGripEl = document.querySelector(".resize-grip");
  const tabBtns = Array.from(document.querySelectorAll(".tab[data-filter]"));
  const sortWrap = document.getElementById("clip-sort");
  const sortButton = document.getElementById("clip-sort-button");
  const sortMenu = document.getElementById("clip-sort-menu");
  const sortOptionBtns = Array.from(document.querySelectorAll(".clip-sort-item[data-sort]"));
  const clipSearchInput = document.getElementById("clip-search-input");

  const FAVORITES_STORAGE_KEY = "replaykit.favoriteClips.v1";
  const LEGACY_FAVORITES_STORAGE_KEY = "streamable.favoriteClips.v1";
  const CLIP_SORT_VALUES = new Set(["newest", "oldest", "shortest", "longest", "smallest", "biggest"]);
  const CLIP_SORT_LABELS = {
    newest: "Newest",
    oldest: "Oldest",
    shortest: "Shortest",
    longest: "Longest",
    smallest: "Smallest",
    biggest: "Biggest",
  };
  // sort values that live under a collapsible row in the menu -- the row shows "Length: Shortest" etc. when one is active.
  const CLIP_SORT_GROUPS = {
    length: { base: "Length", members: ["shortest", "longest"] },
    filesize: { base: "File size", members: ["smallest", "biggest"] },
  };
  const CLIP_STATE_URL = HELPER + "/clips/state";
  const localState = new Map();
  // name -> Date.now() when a UI action set localState optimistically, before the helper job it starts shows in /status -- the reconcile reap loops honour this so a poll landing in that gap cant flash the card back to "Create Link" before the job appears; covers Create Link, bulk upload and every compress path, and self-heals once the grace window passes
  const optimisticStateAt = new Map();
  const OPTIMISTIC_STATE_GRACE_MS = 10000;
  function markLocalState(name, st) {
    localState.set(name, st);
    optimisticStateAt.set(name, Date.now());
  }
  function optimisticStateFresh(name) {
    return localState.has(name) && Date.now() - (optimisticStateAt.get(name) || 0) < OPTIMISTIC_STATE_GRACE_MS;
  }
  const selectedClipNames = new Set();
  let favoriteNames = new Set();
  let favoriteFolderKey = "", favoriteRevision = "", favoriteChange = 0, favoriteHydration = 0;
  let migratingFavorites = false;
  let favoriteSaving = false;
  let resolvedFavoriteClips = [];
  let favoriteResolveKey = "";
  let favoriteResolveVersion = 0;
  // the edits of the editor that were left unsaved: kept by the helper, one per clip, so a closed obs or another view does not take them away
  const draftsByName = new Map();
  let draftsLoaded = false;
  let resolvedDraftClips = new Map();
  let draftResolveKey = "";
  let draftResolveInFlight = false;
  let draftResolveRetries = 0;
  let favoriteResolveInFlight = false;
  let favoriteResolveTimer = 0;
  let activeFilter = "all";
  let clipSearchQuery = "";
  // never persisted (session-only, unlike favorites) -- always starts fresh at newest so a closed-and-reopened clips window or an obs restart never comes back to whatever sort was last picked.
  let activeSort = "newest";
  let loadedClips = [];
  const COLLAPSED_SECTIONS_KEY = "replaykit.collapsedSections.v1";
  const SECTION_KEYS = new Set(["favorites", "screenshots", "clips", "linked", "local", "drafts"]);
  const collapsedSections = new Set();
  if (!HOME_MODE) {
    try {
      const saved = localStorage.getItem(COLLAPSED_SECTIONS_KEY);
      if (saved !== null) {
        if (saved.length > 256) throw new Error("Invalid section preferences");
        const sections = JSON.parse(saved);
        if (!Array.isArray(sections) || sections.length > SECTION_KEYS.size || sections.some(key => !SECTION_KEYS.has(key))) throw new Error("Invalid section preferences");
        sections.filter(key => ["favorites", "screenshots", "clips"].includes(key)).forEach(key => collapsedSections.add(key));
      }
    } catch {
      setTimeout(() => showToast("Could not read your saved section preferences.", "error", "Clips"), 0);
    }
  }
  const screenshotGallery = !HOME_MODE && window.RKScreenshots ? RKScreenshots.create({helper:HELPER, formatSize, formatTimeAgo, icons:ICON, openMenu:openCardMenuAt, closeMenus:closeAllMenus, armDelete:armDeleteButton, resetDeletes:resetAllArmedDeleteButtons, hovers:applyClipHovers, toast:(message) => { showToast(escapeHtml(message), "info"); scheduleToastHide(2400); }, collapsed:() => activeFilter === "all" && collapsedSections.has("screenshots"), notify:message => showToast(escapeHtml(message), "bad", "Screenshots"), changed:() => renderLoadedClips(loadedClips)}) : null;
  let allClips = [];
  let allClipsSignature = "";
  let lastClipsByName = new Map();
  let pollTimer = null;
  let pollInFlight = false;
  let lastPollAt = 0;
  // polls that failed in a row; the popup comes with the third one and not again until the helper has answered, so a helper that is only slow once does not raise it and a popup that was read does not come back
  let helperFailures = 0;
  const HELPER_FAILURES_BEFORE_POPUP = 3;
  let anyUploading = false;
  let activePreview = null;
  let lastUploadStatus = null;
  let lastUploadJobsByName = new Map();
  const pendingLinkCreations = new Map();
  const seenUploadErrors = new Set();
  const seenUploadCompletions = new Set();
  // clips accepted into the bulk-upload pipeline but not yet handed to the helper -- drives the "Queued" card label while runSelectedUploads waits out the concurrency cap.
  const bulkUploadQueued = new Set();
  // clips this bulk run handed to the helper that are still uploading -- keeps the "Stop uploads" button alive past the scheduling phase so one click can cancel the whole batch.
  const bulkUploadInFlight = new Set();
  // clipName -> ms timestamp of a just-issued cancel. syncUploadJobState ignores that clips job status until CANCEL_GRACE_MS passes so the optimistic repaint holds.
  const cancelledUploads = new Map();
  // clipName -> a display-only progress value. It eases between helper reports so the bar does not jump at the poll cadence.
  const progressVisuals = new Map();
  // clipName -> the in-flight /cancel-upload fetch. a new upload for that clip waits on it first, otherwise the helper still sees the old job active and rejects the retry as busy.
  const inFlightCancels = new Map();
  // per-clip re-entrancy guard + debounce so spam-clicking Create Link / the cancel X cant interleave their async steps or act on a click that landed on a freshly-swapped button.
  const linkTransitionBusy = new Set();
  const lastLinkOpAt = new Map();
  const LINK_OP_COOLDOWN_MS = 300;
  function linkOpAllowed(name) {
    if (!name || linkTransitionBusy.has(name)) return false;
    const last = lastLinkOpAt.get(name) || 0;
    if (Date.now() - last < LINK_OP_COOLDOWN_MS) return false;
    lastLinkOpAt.set(name, Date.now());
    return true;
  }
  let didInitialClipRender = false;
  let clipsPageVersion = "";
  // the folder id the helper gives with each list; empty until the first list
  let clipsFolder = "";
  let clipsTotal = 0;
  // false from the moment the total is reset until the helper has answered with the real one, so a count made of the rows that happen to be loaded is never what the tabs show
  let clipsTotalKnown = false;
  // the last counts the helper gave, kept across windows and sessions: what the tabs show until the first answer of this load comes in
  const TAB_COUNTS_KEY = "rk-clips-tab-counts";
  let tabCountsShown = (() => {
    try {
      const raw = JSON.parse(localStorage.getItem(TAB_COUNTS_KEY) || "null");
      if (raw && typeof raw === "object") return { all: Number(raw.all) || 0, linked: Number(raw.linked) || 0 };
    } catch {}
    return null;
  })();
  // linked count for the whole clip set (not just the loaded page) -- the backend /clips page response carries it. lets the Linked tab badge be right before you click it.
  let clipsLinkedTotal = 0;
  let clipsNextOffset = 0;
  let clipsLoadingMore = false;
  let clipSortPreparing = false;
  let clipStateSaveTimer = 0;
  let clipStateHydrated = false;
  let clipsRefreshCounter = 0;
  let virtualRangeStart = -1;
  let virtualRangeEnd = -1;
  let virtualColumns = 1;
  // the slots in front of the first clip, one while a clip is being saved
  let virtualLead = 0;
  // how many clips the grid holds once every page has arrived; the height of the scroll bar is made for all of them from the start, so it does not change size when a page comes in
  let allClipsExpected = 0;
  let virtualCardWidth = 330;
  let virtualRowStride = 318;
  let virtualRenderQueued = false;
  let virtualRenderForceQueued = false;
  let virtualSettling = false;
  let resizeRenderRaf = 0;
  const recycledCards = [];
  // set to true when a render is suppressed becuase a card inside the grid is fullscreened. onfullscreenchange re-queues the render once the user exits. declared with the rest of the virtual-window state so its lifecycle is visible alongside its siblings.
  let pendingRenderOnFsExit = false;
  let fullscreenHostTransition = false;
  let fullscreenHostTransitionSeq = 0;
  let fullscreenHostTransitionTimer = 0;
  // scroll offset captured the instant a clip goes fullscreen, before the helper resizes the host window to monitor size. the resize shrinks maxScrollTop, chromium clamps scrollTop down to fit, and rendervirtualwindow would otherwise bake that clamped value in permanently -- so we stash the real offset here and put it back once the window is restored.
  let fullscreenSavedScrollTop = null;
  let deferredFullscreenRender = false;
  let deferredFullscreenRenderForce = false;
  let pendingLoadedRenderAfterFullscreen = false;
  let theaterPreview = null;
  let pendingRenderOnTheaterExit = false;
  let pendingLoadedRenderAfterTheater = false;
  let pendingLoadedRenderAfterRename = false;
  // the gap between cards is the one the css grid has, so the list that places its own cards stands them the same distance apart
  const GRID_GAP = parseFloat(getComputedStyle(gridEl).rowGap);
  // the dotted corner of a card sets the width of every card; null is no choice, and the cards then fill the row as they do by default. 260 is the size they had before the default grew, and 520 still leaves two to a row on a window of an ordinary width
  const CARD_SIZE_KEY = "rk-clips-card-width";
  const CARD_MIN_WIDTH = 260;
  const CARD_MAX_WIDTH = 520;
  // the triangle of dots of the corner of the window: three along the bottom, two above them, one above those, all flush to the right
  const CARD_GRIP_SVG = '<svg viewBox="0 0 14 14" aria-hidden="true"><circle cx="11" cy="11" r="1.2"/><circle cx="7" cy="11" r="1.2"/><circle cx="3" cy="11" r="1.2"/><circle cx="11" cy="7" r="1.2"/><circle cx="7" cy="7" r="1.2"/><circle cx="11" cy="3" r="1.2"/></svg>';
  function loadCardWidth() {
    try {
      const value = Number(localStorage.getItem(CARD_SIZE_KEY));
      return value >= CARD_MIN_WIDTH && value <= CARD_MAX_WIDTH ? Math.round(value) : null;
    } catch { return null; }
  }
  // the home view keeps its own layout
  let cardWidthPref = HOME_MODE ? null : loadCardWidth();
  function paintCardWidth() {
    const root = document.documentElement;
    root.classList.toggle("rk-card-sized", cardWidthPref != null);
    if (cardWidthPref != null) root.style.setProperty("--rk-card-w", cardWidthPref + "px");
    else root.style.removeProperty("--rk-card-w");
  }
  paintCardWidth();
  const MAX_RECYCLED_CARDS = 96;
  // rows of cards kept above/below the viewport -- in a maximized CEF popup each row can be 8-10 heavy cards, so keep overscan tight and rely on thumbnail predecode instead of rendering many hidden rows.
  const VIRTUAL_OVERSCAN_ROWS = 1;
  const CLIPS_PAGE_LIMIT = HOME_MODE ? HOME_CLIP_LIMIT : 200;
  const CLIPS_LOAD_AHEAD_ROWS = 8;
  const CLIPS_EAGER_LIMIT = 4000;
  const EMPTY_THUMB = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
  const THUMB_PRELOAD_MARGIN_PX = 900;
  const MAX_THUMB_LOADS = 6;
  const MAX_DURATION_PROBES = 2;
  const HOVER_PREVIEW_DELAY_MS = 120;
  const SCROLL_PREVIEW_SETTLE_MS = 180;
  let lastScrollActivityAt = 0;
  let thumbLoadsActive = 0;
  const thumbLoadQueue = [];
  let durationProbesActive = 0;
  const durationProbeQueue = [];
  const durationPending = new Set();
  const thumbObserver = ("IntersectionObserver" in window)
    ? new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const img = entry.target;
          thumbObserver.unobserve(img);
          loadThumb(img);
        }
      }, { root: document.querySelector(".scroll-area"), rootMargin: THUMB_PRELOAD_MARGIN_PX + "px 0px", threshold: 0.01 })
    : null;

  // per-session duration cache, keyed by clip name. we dont bother with localstorage becuase the browser http cache already holds the metadata bytes -- a fresh probe is cheap on reload (~10 kb moov-atom range read), but in-memory is instant for the common case of scrolling the same dock window all session.
  const durationCache = new Map();

  function formatDuration(seconds) {
    if (!isFinite(seconds) || seconds <= 0) return "";
    const total = Math.round(seconds);
    const m = Math.floor(total / 60);
    const s = total % 60;
    if (m >= 60) {
      const h = Math.floor(m / 60);
      const mm = m % 60;
      return h + ":" + String(mm).padStart(2, "0") + ":" + String(s).padStart(2, "0");
    }
    return m + ":" + String(s).padStart(2, "0");
  }

  // read just the duration of a video url without affecting the cards main <video>. a transient element with preload="metadata" makes the browser fetch only the moov atom (10-50 kb for a properly faststart-encoded mp4) and fires loadedmetadata; we grab the duration and dispose. the promise never rejects -- failures resolve to 0 so the badge just stays hidden.
  function probeDuration(url) {
    return new Promise((resolve) => {
      const probe = document.createElement("video");
      probe.preload = "metadata";
      probe.muted = true;
      // crossorigin not needed -- the helper serves with permissive cors already (same-origin in practice) and the metadata read doesnt trigger corb.
      let settled = false;
      const finish = (val) => {
        if (settled) return;
        settled = true;
        try { probe.removeAttribute("src"); probe.load(); } catch {}
        resolve(val);
      };
      probe.addEventListener("loadedmetadata", () => {
        finish(isFinite(probe.duration) ? probe.duration : 0);
      }, { once: true });
      probe.addEventListener("error", () => finish(0), { once: true });
      // safety timeout -- a stuck request shouldnt leave the probe tying up a connection forever.
      setTimeout(() => finish(0), 8000);
      probe.src = url;
    });
  }

  function setDurationBadge(card, seconds) {
    const badge = card && card.querySelector ? card.querySelector(".duration-badge") : null;
    if (!badge || badge.classList.contains("is-ready") || !(seconds > 0)) return;
    badge.textContent = formatDuration(seconds);
    badge.classList.add("is-ready");
  }

  function setDurationForVisibleCards(name, seconds) {
    if (!(seconds > 0)) return;
    const selector = '.card[data-name="' + CSS.escape(name) + '"]';
    document.querySelectorAll(selector).forEach((card) => setDurationBadge(card, seconds));
  }

  function pumpDurationProbes() {
    while (durationProbesActive < MAX_DURATION_PROBES && durationProbeQueue.length) {
      const job = durationProbeQueue.shift();
      if (!job || !job.name || !job.url) continue;
      durationProbesActive++;
      probeDuration(job.url).then((dur) => {
        // the clip may have been written over while the probe ran, and then this is the length of the file before
        const current = lastClipsByName.get(job.name);
        if (current && clipPreviewUrlFor(current) !== job.url) return;
        durationCache.set(job.name, dur);
        setDurationForVisibleCards(job.name, dur);
      }).finally(() => {
        durationPending.delete(job.name);
        durationProbesActive--;
        pumpDurationProbes();
      });
    }
  }

  function prioritizeDurationProbe(name) {
    const idx = durationProbeQueue.findIndex(job => job && job.name === name);
    if (idx <= 0) return;
    const job = durationProbeQueue.splice(idx, 1)[0];
    job.kind = "badge";
    durationProbeQueue.unshift(job);
  }

  function queueDurationProbe(name, url, kind) {
    kind = kind || "badge";
    if (durationCache.has(name)) return;
    if (durationPending.has(name)) {
      if (kind === "badge") prioritizeDurationProbe(name);
      return;
    }
    durationPending.add(name);
    const job = { name, url, kind };
    if (kind === "badge") durationProbeQueue.unshift(job);
    else durationProbeQueue.push(job);
    pumpDurationProbes();
  }

  // fill in a cards duration badge. cached results are instant; first lookups kick off a probe. idempotent -- safe to call multiple times on the same card (e.g. when scroll re-triggers the observer after a filter switch).
  function fillDurationBadge(card) {
    const badge = card.querySelector(".duration-badge");
    if (!badge || badge.classList.contains("is-ready")) return;
    const name = card.dataset.name;
    const url  = card.dataset.previewUrl;
    if (!name || !url) return;

    const indexed = Number(card.dataset.duration || "0") || 0;
    if (indexed > 0) {
      durationCache.set(name, indexed);
      setDurationBadge(card, indexed);
      return;
    }

    const cached = durationCache.get(name);
    if (cached !== undefined) {
      setDurationBadge(card, cached);
      return;
    }
    queueDurationProbe(name, url, "badge");
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function displayClipName(filename) {
    return String(filename || "").replace(/\.(mp4|mkv|mov)$/i, "");
  }

  function loadFavoriteNames() {
    try {
      if (!favoriteFolderKey) return new Set();
      const raw = localStorage.getItem("replaykit.favoriteClips.v2:" + favoriteFolderKey);
      const parsed = JSON.parse(raw || "[]");
      return new Set(Array.isArray(parsed) ? parsed.filter(Boolean) : []);
    } catch {
      return new Set();
    }
  }

  function saveFavoriteNames() {
    try {
      if (favoriteFolderKey) localStorage.setItem("replaykit.favoriteClips.v2:" + favoriteFolderKey, JSON.stringify(Array.from(favoriteNames)));
    } catch {}
  }

  function isFavorite(name) {
    return favoriteNames.has(String(name || ""));
  }

  function favoriteNamesList() {
    return Array.from(favoriteNames).filter(Boolean);
  }

  function currentFavoriteResolveKey() {
    return activeSort + "|" + favoriteResolveVersion + "|" + favoriteNamesList().slice().sort().join("\n");
  }

  function scheduleFavoriteResolve(delay) {
    if (favoriteResolveTimer) clearTimeout(favoriteResolveTimer);
    favoriteResolveTimer = setTimeout(resolveFavoriteClips, Math.max(0, delay || 0));
  }

  function refreshFavoriteClips() {
    favoriteResolveVersion++;
    favoriteResolveKey = "";
    scheduleFavoriteResolve(0);
  }

  function ensureFavoriteResolve() {
    const names = favoriteNamesList();
    if (!names.length) {
      if (resolvedFavoriteClips.length) {
        resolvedFavoriteClips = [];
        favoriteResolveKey = currentFavoriteResolveKey();
      }
      return;
    }
    if (!favoriteResolveInFlight && favoriteResolveKey !== currentFavoriteResolveKey()) {
      scheduleFavoriteResolve(0);
    }
  }

  async function resolveFavoriteClips() {
    favoriteResolveTimer = 0;
    const names = favoriteNamesList();
    const requestSort = activeSort;
    const requestKey = currentFavoriteResolveKey();
    if (!names.length) {
      resolvedFavoriteClips = [];
      favoriteResolveKey = requestKey;
      return;
    }
    if (favoriteResolveInFlight) {
      scheduleFavoriteResolve(80);
      return;
    }
    favoriteResolveInFlight = true;
    try {
      const res = await fetch(HELPER + "/clips/by-name?sort=" + encodeURIComponent(requestSort) + "&t=" + Date.now(), {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ names }),
      });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      if (requestSort !== activeSort || requestKey !== currentFavoriteResolveKey()) return;
      resolvedFavoriteClips = (Array.isArray(data && data.clips) ? data.clips : []).filter(c => c && isFavorite(c.name));
      favoriteResolveKey = requestKey;
      const byName = new Map(lastClipsByName);
      for (const clip of resolvedFavoriteClips) byName.set(clip.name, clip);
      lastClipsByName = byName;
      if (activeFilter === "all" || activeFilter === "favorites") {
        renderLoadedClips(loadedClips);
      } else {
        updateTabs(loadedClips);
      }
    } catch {
      // the regular poll path will retry this; favorites should not block clips from rendering.
    } finally {
      favoriteResolveInFlight = false;
      if (favoriteNamesList().length && favoriteResolveKey !== currentFavoriteResolveKey()) {
        scheduleFavoriteResolve(100);
      }
    }
  }

  function hasDraft(name) {
    return draftsByName.has(String(name || ""));
  }

  // draft writes reach the helper one after another, so a save still on its way cannot land after the delete that followed it and bring the draft back
  let draftWrites = Promise.resolve();
  let draftEpoch = 0;
  let draftReadSeq = 0;
  function queueDraftWrite(send) {
    draftEpoch++;
    const run = draftWrites.then(send, send);
    draftWrites = run.then(() => {}, () => {});
    return run;
  }

  // the list the helper keeps, read when the page starts, when it is shown again and after anything that changes a draft; it is read after the writes of this page and only the newest read counts, and a draft kept or dropped while it was on its way makes it old, so it is read again
  async function refreshDrafts() {
    const seq = ++draftReadSeq;
    try {
      await draftWrites;
      const epoch = draftEpoch;
      const res = await fetch(HELPER + "/drafts?t=" + Date.now(), { cache: "no-store" });
      if (!res.ok || seq !== draftReadSeq) return;
      const data = await res.json();
      if (seq !== draftReadSeq) return;
      if (epoch !== draftEpoch) { refreshDrafts(); return; }
      draftsByName.clear();
      for (const row of (Array.isArray(data && data.drafts) ? data.drafts : [])) {
        if (row && row.name) draftsByName.set(String(row.name), { savedAt: Number(row.savedAt) || 0, duration: Number(row.duration) || 0 });
      }
      draftsChanged();
    } catch {}
    draftsLoaded = true;
    updateTabs(loadedClips);
  }

  // whatever shows drafts follows the list: the count of the tab, the menus of the cards, and the tab itself while it is open
  function draftsChanged() {
    draftResolveKey = "";
    draftResolveRetries = 0;
    draftsLoaded = true;
    // the count of the tab waits for the drafts to be found in this folder
    ensureDraftResolve();
    updateTabs(loadedClips);
    syncAllDraftUi();
    if (activeFilter === "drafts") renderLoadedClips(loadedClips);
  }

  function currentDraftResolveKey() {
    return Array.from(draftsByName.keys()).sort().join("\n");
  }

  function ensureDraftResolve() {
    if (draftResolveInFlight || draftResolveKey === currentDraftResolveKey()) return;
    resolveDraftClips();
  }

  // the clips of the drafts are asked for by name, so a draft of a clip that is far down the list shows too
  async function resolveDraftClips() {
    const requestKey = currentDraftResolveKey();
    const names = Array.from(draftsByName.keys());
    if (!names.length) {
      resolvedDraftClips = new Map();
      draftResolveKey = requestKey;
      return;
    }
    draftResolveInFlight = true;
    try {
      const res = await fetch(HELPER + "/clips/by-name?sort=newest&t=" + Date.now(), {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ names }),
      });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      if (requestKey !== currentDraftResolveKey()) return;
      const next = new Map();
      for (const clip of (Array.isArray(data && data.clips) ? data.clips : [])) {
        if (clip && draftsByName.has(clip.name)) next.set(clip.name, clip);
      }
      resolvedDraftClips = next;
      draftResolveKey = requestKey;
      const byName = new Map(lastClipsByName);
      for (const clip of next.values()) byName.set(clip.name, clip);
      lastClipsByName = byName;
      if (activeFilter === "drafts") renderLoadedClips(loadedClips);
      else updateTabs(loadedClips);
    } catch {
      // asked for again in a moment, a few times at most
      if (draftResolveRetries++ < 3) setTimeout(ensureDraftResolve, 1500);
      else draftResolveKey = requestKey;
    } finally {
      draftResolveInFlight = false;
      if (draftResolveKey !== currentDraftResolveKey() && draftResolveRetries === 0) setTimeout(ensureDraftResolve, 50);
    }
  }

  // the clips that have a draft, the one that was edited last first
  function draftClipsForRender(clips) {
    const current = new Map();
    for (const clip of (Array.isArray(clips) ? clips : [])) {
      if (clip && draftsByName.has(clip.name)) current.set(clip.name, clip);
    }
    const names = Array.from(draftsByName.entries()).sort((a, b) => b[1].savedAt - a[1].savedAt).map((entry) => entry[0]);
    const out = [];
    for (const name of names) {
      const clip = current.get(name) || resolvedDraftClips.get(name);
      if (clip) out.push(clip);
    }
    return out;
  }

  async function fetchDraft(name) {
    try {
      const res = await fetch(HELPER + "/drafts/get?file=" + encodeURIComponent(name) + "&t=" + Date.now(), { cache: "no-store" });
      if (!res.ok) return null;
      const data = await res.json();
      return data && data.ok ? data : null;
    } catch {
      return null;
    }
  }

  // a draft is gone: told to the helper, and out of the list of the page. the helper is told even when the page did not know of it, since the list may not have been read yet
  function forgetDraft(name) {
    const known = draftsByName.delete(name);
    queueDraftWrite(() => fetch(HELPER + "/drafts/delete?file=" + encodeURIComponent(name) + "&t=" + Date.now(), { method: "POST", cache: "no-store" })).catch(() => {});
    if (!known) return;
    draftsChanged();
    rerenderCard(name);
  }

  // the card menu of a clip that has a draft says it continues the edit and offers to let it go
  function syncDraftUi(card, name) {
    if (!card) return;
    const drafted = hasDraft(name);
    const label = card.querySelector(".trim-clip-item > span");
    if (label) label.textContent = drafted ? "Continue editing" : "Edit clip";
    const discard = card.querySelector(".discard-draft-item");
    if (discard) discard.style.display = drafted ? "" : "none";
    // on the drafts tab the trash of a card lets go of the draft and leaves the clip alone, so it says so
    const trash = card.querySelector(".title-actions > .delete-btn");
    if (trash) {
      const inDrafts = activeFilter === "drafts" && drafted;
      trash.dataset.tooltip = inDrafts ? "Delete draft" : "Delete clip";
      trash.setAttribute("aria-label", inDrafts ? "Delete draft" : "Delete clip");
    }
  }

  function syncAllDraftUi() {
    document.querySelectorAll(".card").forEach((card) => syncDraftUi(card, card.dataset.name));
  }

  function favoriteClipsForRender(clips) {
    const result = [];
    const used = new Set();
    const currentByName = new Map();
    for (const clip of (Array.isArray(clips) ? clips : [])) {
      if (clip && isFavorite(clip.name)) currentByName.set(clip.name, clip);
    }
    const addClip = (clip) => {
      if (!clip || !isFavorite(clip.name) || used.has(clip.name)) return;
      result.push(clip);
      used.add(clip.name);
    };
    for (const clip of resolvedFavoriteClips) addClip(currentByName.get(clip.name) || clip);
    for (const clip of (Array.isArray(clips) ? clips : [])) addClip(clip);
    return result;
  }

  function setFavorite(name, enabled) {
    name = String(name || "");
    if (!name || !clipStateHydrated || !favoriteFolderKey) return;
    favoriteChange++;
    if (enabled) favoriteNames.add(name);
    else {
      favoriteNames.delete(name);
      resolvedFavoriteClips = resolvedFavoriteClips.filter(clip => clip && clip.name !== name);
    }
    scheduleClipUiStateSave();
    favoriteResolveKey = "";
    scheduleFavoriteResolve(0);
  }

  function transferFavorite(oldName, newName) {
    if (!isFavorite(oldName) || !newName || oldName === newName) return;
    favoriteChange++;
    favoriteNames.delete(oldName);
    favoriteNames.add(newName);
    resolvedFavoriteClips = resolvedFavoriteClips.filter(clip => clip && clip.name !== oldName);
    scheduleClipUiStateSave();
    favoriteResolveKey = "";
    scheduleFavoriteResolve(0);
  }

  function favoriteSetEquals(a, b) {
    if (!a || !b || a.size !== b.size) return false;
    for (const value of a) {
      if (!b.has(value)) return false;
    }
    return true;
  }

  function normalizeFavoriteList(values) {
    const out = [];
    const seen = new Set();
    for (const value of (Array.isArray(values) ? values : [])) {
      const name = String(value || "").trim();
      if (!name || seen.has(name)) continue;
      seen.add(name);
      out.push(name);
      if (out.length >= 10000) break;
    }
    return out;
  }

  function scheduleClipUiStateSave(delay) {
    if (clipStateSaveTimer) clearTimeout(clipStateSaveTimer);
    clipStateSaveTimer = setTimeout(saveClipUiState, Math.max(0, delay == null ? 150 : delay));
  }

  async function saveClipUiState() {
    clipStateSaveTimer = 0;
    if (!favoriteFolderKey) return;
    if (favoriteSaving) { scheduleClipUiStateSave(); return; }
    favoriteSaving = true;
    const folderKey = favoriteFolderKey, change = favoriteChange;
    let refresh = false;
    try {
      const res = await fetch(CLIP_STATE_URL + "?t=" + Date.now(), {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          folderKey,
          revision: favoriteRevision,
          favorites: favoriteNamesList(),
        }),
      });
      const data = await res.json();
      if (!res.ok || !data.ok) { refresh = true; throw new Error(data.message || "Could not save favorites"); }
      if (folderKey === favoriteFolderKey) favoriteRevision = data.revision;
      if (folderKey === favoriteFolderKey && change === favoriteChange) saveFavoriteNames();
      if (migratingFavorites && folderKey === favoriteFolderKey) {
        migratingFavorites = false;
        localStorage.removeItem(FAVORITES_STORAGE_KEY);
        localStorage.removeItem(LEGACY_FAVORITES_STORAGE_KEY);
      }
    } catch {
      showToast("Could not save favorites. Try again.", "error");
      scheduleToastHide(TOAST_ERROR_MS);
    } finally {
      favoriteSaving = false;
      if (refresh) { clearTimeout(clipStateSaveTimer); clipStateSaveTimer = 0; hydrateClipUiState(); }
      else if (change !== favoriteChange) scheduleClipUiStateSave(0);
    }
  }

  async function hydrateClipUiState() {
    if (clipStateSaveTimer || favoriteSaving) return;
    const run = ++favoriteHydration, change = favoriteChange;
    try {
      const res = await fetch(CLIP_STATE_URL + "?t=" + Date.now(), { cache: "no-store" });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      if (run !== favoriteHydration || change !== favoriteChange) return;
      if (!data || !data.ok || !/^[A-F0-9]{64}$/.test(data.folderKey || "")) throw new Error("Invalid favorites folder");
      const state = data && data.state ? data.state : {};
      const serverExists = !!(data && data.exists);
      const serverFavorites = normalizeFavoriteList(state.favorites);
      favoriteFolderKey = data.folderKey;
      favoriteRevision = data.revision;
      const nextFavorites = new Set(serverFavorites);
      if (!serverExists) {
        for (const name of loadFavoriteNames()) nextFavorites.add(name);
        try {
          const legacy = JSON.parse(localStorage.getItem(FAVORITES_STORAGE_KEY) || localStorage.getItem(LEGACY_FAVORITES_STORAGE_KEY) || "[]");
          for (const name of normalizeFavoriteList(legacy)) nextFavorites.add(name);
          migratingFavorites = true;
        } catch {}
      }
      const favoritesChanged = !favoriteSetEquals(favoriteNames, nextFavorites);

      favoriteNames = nextFavorites;
      saveFavoriteNames();
      clipStateHydrated = true;

      const needsSeed = !serverExists;
      if (needsSeed) scheduleClipUiStateSave(0);

      if (favoritesChanged) {
        favoriteResolveKey = "";
        clipsPageVersion = "";
        clipsTotal = 0;
        clipsTotalKnown = false;
        clipsNextOffset = 0;
        updateClipSortControl();
        renderLoadedClips(loadedClips);
        scheduleNextPoll(0);
      }
    } catch {
      clipStateHydrated = true;
    } finally {
      // the home view holds its first reveal for the favorites, so it draws again now that it knows them
      if (HOME_MODE && homeRenderedOnce) renderLoadedClips(loadedClips);
    }
  }

  window.addEventListener("storage", (event) => {
    if (favoriteFolderKey && event.key === "replaykit.favoriteClips.v2:" + favoriteFolderKey) hydrateClipUiState();
  });

  function clipSortLabel(value) {
    return CLIP_SORT_LABELS[value] || CLIP_SORT_LABELS.newest;
  }

  function setClipSortLoading(loading) {
    clipSortPreparing = !!loading;
    if (sortWrap) sortWrap.classList.toggle("is-loading", clipSortPreparing);
    if (sortButton) sortButton.setAttribute("aria-busy", clipSortPreparing ? "true" : "false");
  }

  function updateClipSortControl() {
    if (sortButton) {
      sortButton.setAttribute("aria-expanded", sortWrap && sortWrap.classList.contains("is-open") ? "true" : "false");
    }
    for (const btn of sortOptionBtns) {
      const isActive = btn.dataset.sort === activeSort;
      btn.classList.toggle("is-active", isActive);
      btn.setAttribute("aria-checked", isActive ? "true" : "false");
    }
    if (sortMenu) {
      for (const key of Object.keys(CLIP_SORT_GROUPS)) {
        const g = CLIP_SORT_GROUPS[key];
        const groupEl = sortMenu.querySelector('.clip-sort-group[data-group="' + key + '"]');
        if (!groupEl) continue;
        const labelEl = groupEl.querySelector(".clip-sort-grouprow .clip-sort-label");
        const activeMember = g.members.indexOf(activeSort) >= 0 ? activeSort : "";
        groupEl.classList.toggle("has-active", !!activeMember);
        if (labelEl) labelEl.textContent = activeMember ? (g.base + ": " + clipSortLabel(activeMember)) : g.base;
      }
    }
  }

  function setClipSortGroupsExpanded() {
    if (!sortMenu) return;
    // on open, disclose only the group that owns the current sort
    sortMenu.querySelectorAll(".clip-sort-group").forEach((g) => {
      const on = g.classList.contains("has-active");
      g.classList.toggle("is-expanded", on);
      const row = g.querySelector(".clip-sort-grouprow");
      if (row) row.setAttribute("aria-expanded", on ? "true" : "false");
    });
  }

  function closeClipSortMenu() {
    if (!sortWrap) return;
    sortWrap.classList.remove("is-open");
    updateClipSortControl();
  }

  function openClipSortMenu() {
    if (!sortWrap) return;
    RKTooltip.hide();
    sortWrap.classList.add("is-open");
    updateClipSortControl();
    setClipSortGroupsExpanded();
  }

  function updateTabs(source) {
    source = Array.isArray(source) ? source : [];
    // favorites + linked count the whole clip set, not just the loaded page -- favoriteNames is the full stored set, clipsLinkedTotal comes from the /clips page response. (they were being computed off `source`, i.e. only the loaded rows, so the badges read low until you opened the tab.) until the helper has answered the totals are the ones it gave last time; with none of those either, the tabs go without numbers instead of showing the number of rows that are loaded
    let total, linkedTotal;
    if (clipsTotalKnown) {
      total = clipsTotal;
      linkedTotal = clipsLinkedTotal;
      const next = { all: total, linked: linkedTotal };
      if (!tabCountsShown || tabCountsShown.all !== next.all || tabCountsShown.linked !== next.linked) {
        tabCountsShown = next;
        try { localStorage.setItem(TAB_COUNTS_KEY, JSON.stringify(next)); } catch {}
      }
    } else if (tabCountsShown) {
      total = tabCountsShown.all;
      linkedTotal = tabCountsShown.linked;
    }
    const known = total !== undefined;
    // favorites are kept by name for every folder, so the count is the favorites the helper found in this folder, not the length of that list
    const favoritesKnown = !favoriteNamesList().length || favoriteResolveKey === currentFavoriteResolveKey();
    const counts = {
      all: total === undefined ? undefined : total + (screenshotGallery ? screenshotGallery.count() || 0 : 0),
      favorites: favoritesKnown ? resolvedFavoriteClips.length : undefined,
      linked: linkedTotal,
      local: known ? Math.max(0, total - linkedTotal) : undefined,
      // the same for drafts: the ones whose clip is in this folder
      drafts: draftsLoaded && draftResolveKey === currentDraftResolveKey() ? resolvedDraftClips.size : undefined,
      screenshots: screenshotGallery ? screenshotGallery.count() : undefined,
    };
    for (const btn of tabBtns) {
      const key = btn.dataset.filter;
      btn.classList.toggle("is-active", key === activeFilter);
      const countEl = btn.querySelector(".count");
      countEl.hidden = counts[key] === undefined;
      countEl.textContent = counts[key] === undefined ? "" : String(counts[key]);
    }
  }

  function clipMatchesFilter(clip) {
    if (activeFilter === "favorites") return isFavorite(clip.name);
    if (activeFilter === "linked") return !!clip.streamable_url;
    if (activeFilter === "local") return !clip.streamable_url;
    if (activeFilter === "drafts") return draftsByName.has(clip.name);
    return true;
  }

  function applyClipFilter(clips) {
    const query = clipSearchQuery.trim().toLocaleLowerCase();
    return (Array.isArray(clips) ? clips : []).filter((clip) => {
      if (!clipMatchesFilter(clip)) return false;
      return !query || displayClipName(clip.name || "").toLocaleLowerCase().includes(query);
    });
  }

  function emptyMessageForFilter() {
    if (clipSearchQuery.trim()) return "No clips match your search.";
    if (activeFilter === "favorites") return "No favorite clips yet.";
    if (activeFilter === "linked") return "No linked clips yet.";
    if (activeFilter === "local") return "No local-only clips.";
    if (activeFilter === "drafts") return "No edit drafts yet. Edits you leave unfinished are kept here.";
    return "No clips found.";
  }

  // a clip that is being saved gets a card of its own at the front of the newest-first list, in the look of a card whose clip is still being processed, and the clip takes that very spot once it is written; the plugin says at once when the save starts and how it ended, and the helper keeps the save as well for a page that opened, came back or was not there when the plugin spoke
  const SAVE_GHOST_LOST_MS = 135000;
  const SAVE_GHOST_ARRIVE_MS = 10000;
  const SAVE_GHOST_POLL_MS = 300;
  let pendingClipSave = null;
  let saveGhostEl = null;
  // the save of the helper whose card has already come and gone, so a poll does not put it up again
  let finishedClipSaveStart = 0;
  function saveGhostSlots() {
    return pendingClipSave && !HOME_MODE && (activeFilter === "all" || activeFilter === "local") && activeSort === "newest" && !clipSearchQuery.trim() ? 1 : 0;
  }
  function endClipSave(save) {
    if (!save || pendingClipSave !== save) return;
    clearTimeout(save.timer);
    if (save.startedAt) finishedClipSaveStart = save.startedAt;
    pendingClipSave = null;
    queueVirtualRender(true);
  }
  function beginClipSave(startedAt) {
    if (pendingClipSave) clearTimeout(pendingClipSave.timer);
    const save = { name: "", startedAt: startedAt || 0, since: Date.now(), timer: 0 };
    // a save that never reports back does not leave the card up for good
    save.timer = setTimeout(() => endClipSave(save), SAVE_GHOST_LOST_MS);
    pendingClipSave = save;
    queueVirtualRender(true);
  }
  function clipSaveWritten(save, name) {
    if (!name) { endClipSave(save); return; }
    if (save.name) return;
    save.name = name;
    clearTimeout(save.timer);
    // the list can be a moment behind the file, so the card waits a little for the clip to show up in it
    save.timer = setTimeout(() => endClipSave(save), SAVE_GHOST_ARRIVE_MS);
  }
  // runs on every poll before the list is drawn, so a page that loads in the middle of a save has the card in place on its first frame
  function syncClipSave(info) {
    if (HOME_MODE || !info) return;
    const started = Number(info.startedAt) || 0;
    const ended = Number(info.endedAt) || 0;
    const save = pendingClipSave;
    if (info.active) {
      if (save) { if (!save.startedAt) save.startedAt = started; }
      else if (started && started !== finishedClipSaveStart) beginClipSave(started);
      return;
    }
    if (!save || !ended) return;
    // the save the helper ended last is this card when the helper started it, or when it ended after the card went up on the word of the plugin
    if (save.startedAt ? save.startedAt === started : ended >= save.since) clipSaveWritten(save, String(info.name || ""));
  }
  function buildSaveGhost() {
    const ghost = document.createElement("div");
    ghost.className = "save-ghost";
    ghost.setAttribute("aria-label", "Saving clip");
    ghost.innerHTML = `
      <div class="preview"><div class="processing-overlay" aria-hidden="true"><div class="processing-spinner"></div><span>Processing&hellip;</span></div></div>
      <div class="body">
        <div class="title-row"><span class="name">Saving clip&hellip;</span></div>
        <div class="meta"><span class="age">Just now</span></div>
      </div>`;
    return ghost;
  }
  window.__rkClipSaving = function (state, name) {
    if (HOME_MODE) return;
    if (state === "start") {
      beginClipSave(0);
      scheduleNextPoll(SAVE_GHOST_POLL_MS);
      return;
    }
    const save = pendingClipSave;
    if (!save) return;
    clipSaveWritten(save, state === "done" ? String(name || "") : "");
    scheduleNextPoll(0);
  };

  function renderLoadedClips(clips) {
    loadedClips = Array.isArray(clips) ? clips : [];
    if (pendingClipSave && pendingClipSave.name && loadedClips.some((c) => c && c.name === pendingClipSave.name)) endClipSave(pendingClipSave);
    updateTabs(loadedClips);
    syncClipView();
    if (activeFilter === "screenshots") {
      screenshotGallery.update(clipSearchQuery, activeSort);
      setClipSortLoading(false);
      updateClipSortControl();
      return;
    }
    if (isClipRenameActive()) {
      pendingLoadedRenderAfterRename = true;
      return;
    }
    if (isFullscreenOrHostTransition()) {
      pendingLoadedRenderAfterFullscreen = true;
      deferVirtualRenderUntilFullscreenSettles(true);
      return;
    }
    if (theaterPreview) {
      pendingLoadedRenderAfterTheater = true;
      return;
    }
    ensureFavoriteResolve();
    if (activeFilter === "drafts") ensureDraftResolve();
    const source = activeFilter === "favorites" ? favoriteClipsForRender(loadedClips)
      : activeFilter === "drafts" ? draftClipsForRender(loadedClips)
      : loadedClips;
    const filtered = applyClipFilter(source);
    setClipSortLoading(false);
    updateClipSortControl();
    render(filtered);
  }

  function syncClipView() {
    if (HOME_MODE) return;
    if (screenshotGallery) {
      screenshotGallery.show(activeFilter);
      screenshotGallery.update(clipSearchQuery, activeSort);
    }
    document.body.classList.toggle("clips-collapsed", activeFilter === "all" && collapsedSections.has("clips"));
    favoritesGridEl.classList.toggle("section-collapsed", activeFilter === "all" && collapsedSections.has("favorites"));
    document.querySelectorAll(".section-toggle").forEach(button => {
      const expanded = activeFilter !== "all" || !collapsedSections.has(button.dataset.section);
      button.hidden = activeFilter !== "all";
      const label = button.parentElement.querySelector("span").textContent;
      button.setAttribute("aria-expanded", String(expanded));
      button.setAttribute("aria-label", (expanded ? "Collapse " : "Expand ") + label);
    });
    if (clipSearchInput) clipSearchInput.setAttribute("aria-label", activeFilter === "screenshots" ? "Search screenshots" : "Search clips");
    const folder = document.getElementById("clip-folder-btn");
    if (folder) {
      const label = activeFilter === "screenshots" ? "Open screenshots folder" : "Open clips folder";
      folder.setAttribute("aria-label", label);
      folder.dataset.tooltip = label;
    }
  }

  document.querySelectorAll(".section-toggle").forEach(button => {
    button.addEventListener("click", () => {
      if (activeFilter !== "all") return;
      const key = button.dataset.section;
      const next = new Set(collapsedSections);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      try { localStorage.setItem(COLLAPSED_SECTIONS_KEY, JSON.stringify(Array.from(next))); }
      catch { showToast("Could not save your section preferences.", "error", "Clips"); return; }
      collapsedSections.clear();
      next.forEach(section => collapsedSections.add(section));
      stopActivePreviewForScroll();
      if (screenshotGallery) screenshotGallery.paint();
      renderLoadedClips(loadedClips);
      queueVirtualRender(true);
    });
  });

  // upload caps the helper actually enforces, read from /me rather than hardcoded so they follow the signed-in plan (free/anon: 1 GB + 10 min; paid plans raise or remove both). 0 on either field means "no limit".
  let uploadLimits = { maxUploadBytes:0, maxDurationSec:0 };
  async function refreshUploadLimits() {
    try {
      const res = await fetch(HELPER + "/me?t=" + Date.now(), { cache:"no-store" });
      if (!res.ok) return;
      const data = await res.json();
      uploadLimits = {
        maxUploadBytes:Number(data.maxUploadBytes) || 0,
        maxDurationSec:Number(data.maxDurationSec) || 0,
      };
    } catch (_) {}
  }

  // why this clip cannot be uploaded, or null when it can. deliberately silent when duration is unknown -- the clip index probes it asynchronously, and refusing on a missing value would grey out brand new clips.
  function uploadBlockReason(clip) {
    if (!clip) return null;
    const cap = uploadLimits.maxUploadBytes;
    if (cap > 0 && Number(clip.size) > cap) return "Over " + formatSize(cap);
    const maxSeconds = uploadLimits.maxDurationSec;
    const duration = Number(clip.duration);
    if (maxSeconds > 0 && isFinite(duration) && duration > maxSeconds) return "Longer than " + Math.round(maxSeconds / 60) + " min";
    return null;
  }

  function formatSize(bytes) {
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
    if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + " MB";
    return (bytes / 1024 / 1024 / 1024).toFixed(2) + " GB";
  }

  function formatTimeAgo(unixSec) {
    const diff = Math.max(0, Math.floor(Date.now() / 1000 - unixSec));
    if (diff < 5) return "just now";
    if (diff < 60) return diff + " seconds ago";
    if (diff < 120) return "a minute ago";
    if (diff < 3600) return Math.floor(diff / 60) + " minutes ago";
    if (diff < 7200) return "an hour ago";
    if (diff < 86400) return Math.floor(diff / 3600) + " hours ago";
    if (diff < 172800) return "a day ago";
    return Math.floor(diff / 86400) + " days ago";
  }

  // honors the per-clip retention sent by the helper. anonymous uploads are 1 days; signed-in uploads carry their own retention_days field.
  function formatExpiry(uploadedAt, retentionDays) {
    const retSec = (retentionDays && retentionDays > 0 ? retentionDays : 1) * 86400;
    const remaining = retSec - (Math.floor(Date.now() / 1000) - uploadedAt);
    if (remaining <= 0) return null;
    const h = Math.floor(remaining / 3600);
    if (h >= 24) return Math.floor(h / 24) + "d " + (h % 24) + "h";
    if (h >= 1) return h + "h " + Math.floor((remaining % 3600) / 60) + "m";
    return Math.max(1, Math.floor(remaining / 60)) + "m";
  }

  let toastTimer = 0;
  const TOAST_ERROR_MS = 4000;
  function showToast(html, kind, title) {
    if (kind !== "info") { showErrorPopup(html, title); return; }
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = 0; }
    toastEl.innerHTML = html;
    toastEl.classList.add("is-shown");
    toastEl.classList.toggle("is-info", kind === "info");
  }
  function hideToast() {
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = 0; }
    toastEl.classList.remove("is-shown");
  }
  function scheduleToastHide(delay) {
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastTimer = 0;
      toastEl.classList.remove("is-shown");
    }, delay);
  }

  // an error is told in a popup that waits to be read, the way the confirm sheets of obs do; what merely happened (saved, deleted, copied) stays a toast that goes by itself
  const POPUP_TAGS = new Set(["B", "I", "CODE", "BR"]);
  const POPUP_DROPPED = new Set(["SCRIPT", "STYLE", "TEMPLATE", "NOSCRIPT"]);
  const POPUP_QUEUE_MAX = 5;
  const popupQueue = [];
  let popupShown = null;
  let popupReturnFocus = null;

  // a message as a fragment of text and the few tags the messages use, so an error that carries markup of its own never changes the page
  function popupFragment(html) {
    const tpl = document.createElement("template");
    tpl.innerHTML = String(html == null ? "" : html);
    const out = document.createDocumentFragment();
    const walk = (from, to) => {
      from.childNodes.forEach((node) => {
        if (node.nodeType === 3) {
          to.appendChild(document.createTextNode(node.nodeValue));
        } else if (node.nodeType === 1 && POPUP_TAGS.has(node.tagName)) {
          const copy = document.createElement(node.tagName.toLowerCase());
          if (node.tagName !== "BR") walk(node, copy);
          to.appendChild(copy);
        } else if (node.nodeType === 1 && !POPUP_DROPPED.has(node.tagName)) {
          walk(node, to);
        }
      });
    };
    walk(tpl.content, out);
    return out;
  }

  // "Upload failed: reason" is a title and a body; a message that does not start that way gets the plain title. a helper that cannot be reached shows up as a failed fetch, which says nothing to the reader
  function popupParts(html, title) {
    let body = String(html == null ? "" : html);
    if (!title) {
      const m = /^([^:<>&]{3,48}): ([\s\S]+)$/.exec(body);
      if (m) { title = m[1]; body = m[2]; } else title = "Something went wrong";
    }
    body = body.replace(/Failed to fetch|NetworkError when attempting to fetch resource\.?|Load failed/gi, "ReplayKit's helper did not answer");
    return { title, body };
  }

  function sameProblem(a, b) {
    return (a.key && a.key === b.key) || (a.title === b.title && a.body === b.body);
  }

  function fillPopup(item) {
    popupTitle.textContent = item.title;
    popupBody.replaceChildren(popupFragment(item.body));
    popupBody.hidden = !popupBody.textContent.trim();
  }

  function openPopup(item) {
    popupShown = item;
    popupReturnFocus = document.activeElement;
    fillPopup(item);
    popupVeil.classList.add("is-open");
    popupOk.focus();
  }

  // one popup at a time: the next waits until this one is read, and the same message again is not stacked. a message with a key replaces the popup of that key in place
  function showErrorPopup(html, title, key) {
    const parts = popupParts(html, title);
    const item = { key: key || "", title: parts.title, body: parts.body };
    if (!popupShown) { openPopup(item); return; }
    if (item.key && popupShown.key === item.key) { popupShown = item; fillPopup(item); return; }
    if (sameProblem(popupShown, item) || popupQueue.some((queued) => sameProblem(queued, item))) return;
    if (popupQueue.length < POPUP_QUEUE_MAX) popupQueue.push(item);
  }

  // closes the popup that is up, or with a key only the popup of that key, which is how a popup goes by itself once what it told about is over
  function closeErrorPopup(key) {
    if (key) {
      for (let i = popupQueue.length - 1; i >= 0; i--) if (popupQueue[i].key === key) popupQueue.splice(i, 1);
      if (!popupShown || popupShown.key !== key) return;
    }
    if (!popupShown) return;
    popupShown = null;
    popupVeil.classList.remove("is-open");
    const back = popupReturnFocus;
    popupReturnFocus = null;
    if (back && back.isConnected && typeof back.focus === "function") { try { back.focus({ preventScroll: true }); } catch {} }
    if (popupQueue.length) setTimeout(() => { if (!popupShown && popupQueue.length) openPopup(popupQueue.shift()); }, 160);
  }

  // a popup that was left up when the window was closed does not wait for the next session, and a helper that is still down is told again after three more polls
  function resetErrorPopup() {
    popupQueue.length = 0;
    closeErrorPopup();
    helperFailures = 0;
  }

  popupOk.addEventListener("click", () => closeErrorPopup());
  popupVeil.addEventListener("click", (e) => { if (e.target === popupVeil) closeErrorPopup(); });
  // while it is up every key belongs to the popup, so nothing behind it (the editor, the grid) reacts to a key that was meant for it
  window.addEventListener("keydown", (e) => {
    if (!popupShown) return;
    if (e.key === "Escape" || e.key === "Enter") { e.preventDefault(); e.stopImmediatePropagation(); closeErrorPopup(); return; }
    if (e.key === "Tab") e.preventDefault();
    e.stopImmediatePropagation();
  }, true);

  function thumbUrlFor(clip) {
    return HELPER + "/thumb/" + encodeURIComponent(clip.name)
      + "?v=" + encodeURIComponent(String(clip.size) + "-" + String(clip.mtime));
  }

  function clipPreviewUrlFor(clip) {
    return HELPER + "/file/" + encodeURIComponent(clip.name)
      + "?v=" + encodeURIComponent(String(clip.size || 0) + "-" + String(clip.mtime || 0));
  }

  // the pictures that have been on screen before: the browser still has them in memory, so one of them goes straight in. typing in the search box moves the cards around, and a card that had to wait for its picture was a flash
  const loadedThumbs = new Set();
  function showSeenThumb(img, src) {
    if (!loadedThumbs.has(src)) return false;
    if (thumbObserver) thumbObserver.unobserve(img);
    img.dataset.src = src;
    img.dataset.loaded = "1";
    delete img.dataset.loadingSrc;
    delete img.dataset.thumbTries;
    img.decoding = "sync";
    img.src = src;
    const card = img.closest && img.closest(".card");
    if (card) {
      card.classList.remove("is-processing");
      fillDurationBadge(card);
    }
    return true;
  }

  // a clip that was written a moment ago has no picture until OBS is done with the file, so its card says it is being processed and asks again until the picture is there; the helper stops saying the picture is not ready after a few minutes, and an older clip without a picture just stays plain
  const PROCESSING_WINDOW_S = 180;
  const THUMB_RETRY_MAX = 60;
  function isFreshClip(mtime) {
    return (Date.now() / 1000 - (Number(mtime) || 0)) < PROCESSING_WINDOW_S;
  }
  function isProcessingClip(clip) {
    return !!clip && clip.indexed !== true && isFreshClip(clip.mtime);
  }
  function syncProcessing(card, clip) {
    const thumb = card.querySelector(".thumb");
    card.classList.toggle("is-processing", isProcessingClip(clip) && !(thumb && thumb.dataset.loaded === "1"));
  }
  function thumbNotReady(img, src) {
    const card = img.closest && img.closest(".card");
    if (!card || !isFreshClip(card.dataset.mtime)) return;
    const tries = (Number(img.dataset.thumbTries) || 0) + 1;
    img.dataset.thumbTries = String(tries);
    if (tries > THUMB_RETRY_MAX) {
      card.classList.remove("is-processing");
      return;
    }
    card.classList.add("is-processing");
    setTimeout(() => {
      if (img.dataset && img.dataset.src === src && img.dataset.loaded !== "1") loadThumb(img);
    }, Math.min(1000 + tries * 500, 5000));
  }

  function setThumbSource(img, src) {
    if (!img || !src) return;
    if (img.dataset.src === src && img.dataset.loaded === "1") return;
    if (thumbObserver) thumbObserver.unobserve(img);
    img.dataset.src = src;
    delete img.dataset.loaded;
    delete img.dataset.loadingSrc;
    delete img.dataset.thumbTries;
    const badge = img.closest && img.closest(".card") && img.closest(".card").querySelector(".duration-badge");
    if (badge) {
      badge.textContent = "";
      badge.classList.remove("is-ready");
    }
    if (showSeenThumb(img, src)) return;
    img.src = EMPTY_THUMB;
    observeThumb(img);
  }

  function loadThumb(img) {
    const src = img && img.dataset ? img.dataset.src : "";
    if (!src) return;
    if (thumbObserver) thumbObserver.unobserve(img);
    if (img.dataset.loaded === "1" && img.src === src) {
      const card = img.closest && img.closest(".card");
      if (card) fillDurationBadge(card);
      return;
    }
    if (img.dataset.loadingSrc === src) return;
    if (showSeenThumb(img, src)) return;
    img.dataset.loadingSrc = src;
    thumbLoadQueue.push(img);
    pumpThumbLoads();
  }

  function pumpThumbLoads() {
    while (thumbLoadsActive < MAX_THUMB_LOADS && thumbLoadQueue.length) {
      const img = thumbLoadQueue.shift();
      if (!img || !img.dataset) continue;
      const src = img.dataset.loadingSrc;
      if (!src || img.dataset.src !== src) {
        if (img.dataset) delete img.dataset.loadingSrc;
        continue;
      }
      thumbLoadsActive++;
      loadThumbNow(img, src).finally(() => {
        thumbLoadsActive--;
        pumpThumbLoads();
      });
    }
  }

  function loadThumbNow(img, src) {
    const loader = new Image();
    loader.decoding = "async";
    let failed = false;
    loader.onerror = () => { failed = true; };
    loader.src = src;
    const ready = loader.decode
      ? loader.decode()
      : new Promise(resolve => {
        loader.onload = resolve;
        loader.onerror = () => { failed = true; resolve(); };
      });
    return ready.then(() => {
      if (!img.dataset || img.dataset.src !== src) return;
      delete img.dataset.loadingSrc;
      if (failed) {
        thumbNotReady(img, src);
        return;
      }
      img.src = src;
      img.dataset.loaded = "1";
      delete img.dataset.thumbTries;
      loadedThumbs.add(src);
      // piggyback on the same near-viewport trigger to lazy-fill the duration badge, kept out of the scroll renderer so CEF is not decoding thumbnails and probing video metadata during every scroll frame.
      const card = img.closest && img.closest(".card");
      if (card) {
        card.classList.remove("is-processing");
        fillDurationBadge(card);
      }
    }).catch(() => {
      if (img.dataset && img.dataset.src === src) {
        delete img.dataset.loadingSrc;
        thumbNotReady(img, src);
      }
    });
  }

  function observeThumb(img) {
    if (!img) return;
    if (img.dataset && img.dataset.loaded === "1") return;
    if (img.dataset && img.dataset.loadingSrc) return;
    if (thumbObserver) thumbObserver.observe(img);
    else loadThumb(img);
  }

  // what a clip is on disk: the editor writes a clip over with its date kept, so the size is what tells a new file from the one before
  function clipVersion(clip) {
    return String(clip.size || 0) + "-" + String(clip.mtime || 0);
  }

  function clipSignature(clip) {
    return [
      clip.name,
      clip.size,
      clip.mtime,
      clip.duration || 0,
      clip.codec || "",
      clip.tag || "",
      clip.streamable_url || "",
      clip.uploaded_at || 0,
      clip.retention_days || 0,
      isFavorite(clip.name) ? 1 : 0,
    ].join("|");
  }

  function clipsListSignature(clips) {
    return clips.map(clipSignature).join("\n");
  }

  // the columns of the list are the ones the css grid works out (.grid, with the chosen card size when there is one): the used track sizes are read back from the list itself or, when it is hidden, from the favorites above it, so every card of every tab stands on the same columns
  function computeVirtualLayout() {
    const source = gridEl.style.display === "none" ? favoritesGridEl : gridEl;
    const tracks = getComputedStyle(source).gridTemplateColumns.split(" ").map(parseFloat).filter((px) => px > 0);
    return tracks.length ? { columns: tracks.length, cardWidth: tracks[0] } : { columns: virtualColumns, cardWidth: virtualCardWidth };
  }

  // the drag of the dotted corner of a card: every card takes the width the pointer asks for, along the diagonal of the card the drag began on, and that card stays where it is on the screen while the rows move around it
  let cardResize = null;
  let cardResizeLast = null;

  function cardResizeFrom(card) {
    const rect = card.getBoundingClientRect();
    const name = card.dataset.name;
    return {
      width: rect.width,
      height: rect.height,
      name,
      favorite: favoritesGridEl.contains(card),
      index: allClips.findIndex((clip) => clip && clip.name === name),
      x: 0,
      y: 0,
      target: null,
      frame: 0,
    };
  }

  // where the top of the card the drag began on is in the scrolled content: the main grid keeps only the cards near the screen, so its card is placed from its number in the list and the height of a row, and a favorite is read from the page
  function cardResizeTop(drag, columns) {
    const origin = scrollAreaEl.getBoundingClientRect().top - scrollAreaEl.scrollTop;
    if (drag.favorite) {
      const card = favoritesGridEl.querySelector('.card[data-name="' + CSS.escape(drag.name) + '"]');
      return card ? card.getBoundingClientRect().top - origin : 0;
    }
    if (drag.index < 0) return 0;
    return gridEl.getBoundingClientRect().top - origin + Math.floor(drag.index / columns) * virtualRowStride;
  }

  // gives every card the width px, or takes the choice back with null, and keeps the card the drag began on at the same place on the screen
  function setCardWidth(px, drag) {
    const next = px == null ? null : Math.round(Math.max(CARD_MIN_WIDTH, Math.min(CARD_MAX_WIDTH, px)));
    if (next === cardWidthPref) return;
    const scrolled = scrollAreaEl.scrollTop;
    const before = cardResizeTop(drag, virtualColumns);
    cardWidthPref = next;
    paintCardWidth();
    const layout = computeVirtualLayout();
    // a row is as high as a card, and a card is as much higher as its picture is: 16 to 9 of the width
    virtualRowStride = Math.ceil(drag.height + (layout.cardWidth - drag.width) * 9 / 16) + GRID_GAP;
    gridEl.style.height = Math.ceil((Math.max(allClips.length, allClipsExpected) + saveGhostSlots()) / layout.columns) * virtualRowStride + "px";
    scrollAreaEl.scrollTop = scrolled + cardResizeTop(drag, layout.columns) - before;
    renderVirtualWindow(true);
  }

  function saveCardWidth() {
    try {
      if (cardWidthPref == null) localStorage.removeItem(CARD_SIZE_KEY);
      else localStorage.setItem(CARD_SIZE_KEY, String(cardWidthPref));
    } catch {}
  }

  function startCardResize(e, card) {
    if (e.button !== 0 || cardResize) return;
    e.preventDefault();
    e.stopPropagation();
    if (window.RKTooltip) RKTooltip.hide();
    // a second press a moment later and in the same place is a double click: it takes the choice back and no drag starts
    const now = performance.now();
    const second = cardResizeLast && now - cardResizeLast.at < 400 && Math.abs(e.clientX - cardResizeLast.x) < 6 && Math.abs(e.clientY - cardResizeLast.y) < 6;
    cardResizeLast = second ? null : { at: now, x: e.clientX, y: e.clientY };
    if (second) {
      resetCardWidth(card);
      return;
    }
    cardResize = Object.assign(cardResizeFrom(card), { x: e.clientX, y: e.clientY });
    document.documentElement.classList.add("is-card-resizing");
    document.addEventListener("pointermove", onCardResizeMove);
    document.addEventListener("pointerup", endCardResize);
    document.addEventListener("pointercancel", endCardResize);
    window.addEventListener("blur", endCardResize);
  }

  function onCardResizeMove(e) {
    const drag = cardResize;
    if (!drag) return;
    // a button let go outside of the page is never seen as a release, so the first move that finds it up ends the drag
    if (!(e.buttons & 1)) {
      endCardResize();
      return;
    }
    const dx = e.clientX - drag.x;
    const dy = e.clientY - drag.y;
    // the pointer is projected on the diagonal of the card, so a pull to the right and a pull down both make it bigger
    drag.target = drag.width + ((dx * drag.width + dy * drag.height) / (drag.width * drag.width + drag.height * drag.height)) * drag.width;
    if (drag.frame) return;
    drag.frame = requestAnimationFrame(() => {
      drag.frame = 0;
      setCardWidth(drag.target, drag);
    });
  }

  function endCardResize() {
    const drag = cardResize;
    if (!drag) return;
    cardResize = null;
    if (drag.frame) {
      cancelAnimationFrame(drag.frame);
      if (drag.target != null) setCardWidth(drag.target, drag);
    }
    document.documentElement.classList.remove("is-card-resizing");
    document.removeEventListener("pointermove", onCardResizeMove);
    document.removeEventListener("pointerup", endCardResize);
    document.removeEventListener("pointercancel", endCardResize);
    window.removeEventListener("blur", endCardResize);
    saveCardWidth();
  }

  function resetCardWidth(card) {
    setCardWidth(null, cardResizeFrom(card));
    saveCardWidth();
  }

  function clearTransientCardState(card) {
    if (!card) return;
    const menu = card.querySelector(".menu-dropdown");
    if (menu) closeCardMenu(menu);
    else card.classList.remove("menu-open");
    resetDeleteButton(card.querySelector(".delete-menu-item"));
    card.classList.remove("is-processing");
    const preview = card.querySelector(".preview");
    if (preview) preview.classList.remove("is-playing", "is-video-ready", "is-idle");
    const progress = card.querySelector(".progress-fill");
    if (progress) progress.style.width = "0%";
    const badge = card.querySelector(".duration-badge");
    if (badge) {
      badge.textContent = "";
      badge.classList.remove("is-ready");
    }
    const video = card.querySelector("video");
    if (video) {
      if (activePreview === video) activePreview = null;
      resetVideoZoom(video);
      abortVideoLoad(video);
    }
  }

  function bindCardToClip(card, clip) {
    if (!card || !clip) return card;
    if (card.dataset.name !== clip.name) {
      clearTransientCardState(card);
      card.dataset.clipSig = "";
      card.dataset.actionSig = "";
    }
    updateCard(card, clip);
    return card;
  }

  function acquireCard(clip) {
    const card = recycledCards.pop();
    return card ? bindCardToClip(card, clip) : buildCard(clip);
  }

  function releaseCard(card) {
    if (!card) return;
    clearTransientCardState(card);
    const thumb = card.querySelector(".thumb");
    if (thumb) {
      if (thumbObserver) thumbObserver.unobserve(thumb);
      delete thumb.dataset.loaded;
      delete thumb.dataset.loadingSrc;
      thumb.src = EMPTY_THUMB;
    }
    card.remove();
    card.style.removeProperty("--virtual-card-x");
    card.style.removeProperty("--virtual-card-y");
    card.style.removeProperty("--virtual-card-width");
    if (recycledCards.length < MAX_RECYCLED_CARDS) recycledCards.push(card);
  }

  function isFullscreenOrHostTransition() {
    return fullscreenHostTransition || !!(document.fullscreenElement || document.webkitFullscreenElement);
  }

  function updateFullscreenBodyState() {
    document.body.classList.toggle("is-clip-fullscreen", isFullscreenOrHostTransition());
  }

  function isTheaterPreview(preview) {
    return !!preview && theaterPreview === preview;
  }

  function syncTheaterButton(preview) {
    if (!preview) return;
    const button = preview.querySelector(".theater-btn");
    if (!button) return;
    const active = isTheaterPreview(preview);
    const enterIcon = button.querySelector(".theater-enter-icon");
    const exitIcon = button.querySelector(".theater-exit-icon");
    button.dataset.tooltip = active ? "Exit theater mode (T)" : "Theater mode (T)";
    button.setAttribute("aria-label", button.title);
    if (enterIcon) enterIcon.style.display = active ? "none" : "";
    if (exitIcon) exitIcon.style.display = active ? "" : "none";
  }

  function enterTheaterMode(preview) {
    if (!preview || isTheaterPreview(preview)) return !!preview;
    if (theaterPreview) exitTheaterMode();
    const card = preview.closest(".card");
    if (!card) return false;
    theaterPreview = preview;
    card.classList.add("is-theater-card");
    preview.classList.remove("is-idle");
    document.body.classList.add("is-clip-theater");
    syncTheaterButton(preview);
    return true;
  }

  // grid fade-back on theatre exit. the reflow read restarts the animation when theatre is toggled again before the previous one finished, which is otherwise a no-op because the class is already on the body.
  let theaterRestoreTimer = null;
  function playTheaterRestore() {
    const body = document.body;
    body.classList.remove("is-theater-restoring");
    void body.offsetWidth;
    body.classList.add("is-theater-restoring");
    clearTimeout(theaterRestoreTimer);
    theaterRestoreTimer = setTimeout(() => body.classList.remove("is-theater-restoring"), 220);
  }

  function exitTheaterMode() {
    const preview = theaterPreview;
    if (!preview) return false;
    theaterPreview = null;
    const card = preview.closest(".card");
    if (card) card.classList.remove("is-theater-card");
    preview.classList.remove("is-idle");
    document.body.classList.remove("is-clip-theater");
    playTheaterRestore();
    resetVideoZoom(preview.querySelector("video"));
    resetPlaybackRate(preview.querySelector("video"));
    syncTheaterButton(preview);
    if (pendingLoadedRenderAfterTheater) {
      pendingLoadedRenderAfterTheater = false;
      renderLoadedClips(loadedClips);
    } else if (pendingRenderOnTheaterExit) {
      pendingRenderOnTheaterExit = false;
      queueVirtualRender(true);
    }
    return true;
  }

  function beginFullscreenHostTransition() {
    fullscreenHostTransitionSeq += 1;
    fullscreenHostTransition = true;
    if (fullscreenHostTransitionTimer) {
      clearTimeout(fullscreenHostTransitionTimer);
      fullscreenHostTransitionTimer = 0;
    }
    updateFullscreenBodyState();
    return fullscreenHostTransitionSeq;
  }

  function deferVirtualRenderUntilFullscreenSettles(force) {
    deferredFullscreenRender = true;
    deferredFullscreenRenderForce = deferredFullscreenRenderForce || !!force;
  }

  function flushDeferredFullscreenRender(delayMs) {
    if (!deferredFullscreenRender) return;
    const delay = Math.max(0, Number(delayMs) || 0);
    setTimeout(() => {
      if (!deferredFullscreenRender || isFullscreenOrHostTransition()) return;
      const force = deferredFullscreenRenderForce;
      deferredFullscreenRender = false;
      deferredFullscreenRenderForce = false;
      if (pendingLoadedRenderAfterFullscreen) {
        pendingLoadedRenderAfterFullscreen = false;
        renderLoadedClips(loadedClips);
        return;
      }
      queueVirtualRender(force);
    }, delay);
  }

  function finishFullscreenHostTransition(seq, restoreScroll) {
    if (seq !== fullscreenHostTransitionSeq) return;
    if (fullscreenHostTransitionTimer) clearTimeout(fullscreenHostTransitionTimer);
    fullscreenHostTransitionTimer = setTimeout(() => {
      if (seq !== fullscreenHostTransitionSeq) return;
      fullscreenHostTransition = false;
      if (!(document.fullscreenElement || document.webkitFullscreenElement)) {
        document.querySelectorAll(".card.is-fullscreen-card").forEach((card) => {
          card.classList.remove("is-fullscreen-card");
        });
      }
      updateFullscreenBodyState();
      // window is back to its normal size now, so clientHeight (and therefore maxScrollTop) is trustworthy again -- put the pre-fullscreen offset back, render at that position, then re-assert once more in case the render nudged it.
      if (restoreScroll && fullscreenSavedScrollTop != null) {
        const applyScroll = () => {
          if (fullscreenSavedScrollTop == null) return;   // a window reset cleared it out from under us
          const maxScrollTop = Math.max(0, scrollAreaEl.scrollHeight - scrollAreaEl.clientHeight);
          scrollAreaEl.scrollTop = Math.min(fullscreenSavedScrollTop, maxScrollTop);
        };
        requestAnimationFrame(() => {
          applyScroll();
          queueVirtualRender(true);
          requestAnimationFrame(() => {
            applyScroll();
            fullscreenSavedScrollTop = null;
          });
        });
      } else {
        fullscreenSavedScrollTop = null;
      }
      flushDeferredFullscreenRender(0);
    }, 180);
  }

  function positionVirtualCard(card, index, columns, cardWidth) {
    const row = Math.floor(index / columns);
    const col = index % columns;
    card.style.setProperty("--virtual-card-width", cardWidth + "px");
    card.style.setProperty("--virtual-card-x", (col * (cardWidth + GRID_GAP)) + "px");
    card.style.setProperty("--virtual-card-y", (row * virtualRowStride) + "px");
  }

  function resetVirtualWindow() {
    allClipsExpected = 0;
    virtualRangeStart = -1;
    virtualRangeEnd = -1;
    virtualRenderForceQueued = false;
    topSpacerEl.style.height = "0px";
    bottomSpacerEl.style.height = "0px";
    gridEl.style.height = "0px";
    gridEl.querySelectorAll(".card").forEach(releaseCard);
  }

  function updateVisibleCards() {
    gridEl.querySelectorAll(".card").forEach((card) => {
      const clip = lastClipsByName.get(card.dataset.name);
      if (clip) updateCard(card, clip);
    });
  }

  function measureVirtualRow() {
    const card = gridEl.querySelector(".card");
    if (!card) return;
    const measured = Math.ceil(card.getBoundingClientRect().height) + GRID_GAP;
    if (Number.isFinite(measured) && measured > 80 && Math.abs(measured - virtualRowStride) > 2) {
      virtualRowStride = measured;
      queueVirtualRender(true);
    }
  }

  function renderVirtualWindow(force) {
    virtualRenderQueued = false;
    if (!HOME_MODE && (activeFilter === "screenshots" || (activeFilter === "all" && collapsedSections.has("clips")))) return;
    if (isClipRenameActive()) {
      pendingLoadedRenderAfterRename = true;
      return;
    }
    // embedded in the obs window the page stays laid out while another view is showing, and drawing its first cards then is what lets it appear already filled when it is revealed
    if ((document.hidden && !window.RKEmbedded) || !allClips.length) return;
    if (theaterPreview) {
      pendingRenderOnTheaterExit = true;
      return;
    }

    // if a card inside the grid is currently fullscreened, skip the render entirely -- removing or recycling the fullscreen element forces an immediate fullscreen exit in Chromium/CEF.
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
    if (fsEl && gridEl.contains(fsEl)) {
      pendingRenderOnFsExit = true;
      return;
    }

    // during a pending post-fullscreen restore, aim at the offset captured before the host window grew, not the value chromium clamped down while it was monitor-sized.
    const previousScrollTop = fullscreenSavedScrollTop != null ? fullscreenSavedScrollTop : scrollAreaEl.scrollTop;
    const layout = computeVirtualLayout();
    const columns = layout.columns;
    const cardWidth = layout.cardWidth;
    const lead = saveGhostSlots();
    const totalRows = Math.ceil((Math.max(allClips.length, allClipsExpected) + lead) / columns);
    // the virtualized grid sits below an optional favorites sub-section, so subtract that section height from scrollTop before computing visible rows.
    const virtScrollTop = Math.max(0,
      scrollAreaEl.scrollTop - (gridEl.offsetTop - scrollAreaEl.offsetTop));
    const startRow = Math.max(0, Math.floor(virtScrollTop / virtualRowStride) - VIRTUAL_OVERSCAN_ROWS);
    const endRow = Math.min(
      totalRows,
      Math.ceil((virtScrollTop + scrollAreaEl.clientHeight) / virtualRowStride) + VIRTUAL_OVERSCAN_ROWS
    );
    const start = Math.max(0, Math.min(allClips.length, startRow * columns - lead));
    const end = Math.max(0, Math.min(allClips.length, endRow * columns - lead));

    if (!force &&
        lead === virtualLead &&
        columns === virtualColumns &&
        cardWidth === virtualCardWidth &&
        start === virtualRangeStart &&
        end === virtualRangeEnd) {
      return;
    }

    virtualColumns = columns;
    virtualLead = lead;
    virtualCardWidth = cardWidth;
    virtualRangeStart = start;
    virtualRangeEnd = end;
    topSpacerEl.style.height = "0px";
    bottomSpacerEl.style.height = "0px";
    gridEl.style.height = Math.max(0, totalRows * virtualRowStride) + "px";
    // the height just set can bring the scrollbar in and the scrollbar takes its width from the grid, so the cards would stand at the width the grid had before it; the width is measured again and a layout that comes out different is made again right here, before the frame is drawn, instead of by whatever render comes next
    if (!virtualSettling) {
      const settled = computeVirtualLayout();
      if (settled.columns !== columns || settled.cardWidth !== cardWidth) {
        virtualSettling = true;
        try { renderVirtualWindow(true); } finally { virtualSettling = false; }
        return;
      }
    }

    const desiredNames = new Set();
    for (let i = start; i < end; i++) {
      if (allClips[i] && allClips[i].name) desiredNames.add(allClips[i].name);
    }

    const existingByName = new Map();
    gridEl.querySelectorAll(".card").forEach(c => {
      const name = c.dataset.name;
      if (desiredNames.has(name)) existingByName.set(name, c);
      else releaseCard(c);
    });

    for (let i = start; i < end; i++) {
      const clip = allClips[i];
      let card = existingByName.get(clip.name);
      if (card) {
        if (card.dataset.clipSig !== clipSignature(clip)) updateCard(card, clip);
      } else {
        card = acquireCard(clip);
        gridEl.appendChild(card);
      }
      positionVirtualCard(card, i + lead, columns, cardWidth);
      observeThumb(card.querySelector(".thumb"));
    }

    if (lead && startRow === 0) {
      if (!saveGhostEl) saveGhostEl = buildSaveGhost();
      if (saveGhostEl.parentNode !== gridEl) gridEl.appendChild(saveGhostEl);
      positionVirtualCard(saveGhostEl, 0, columns, cardWidth);
      // as tall as the cards beside it, whose action row it does not have
      const sample = gridEl.querySelector(".card");
      saveGhostEl.style.height = sample ? sample.offsetHeight + "px" : "";
    } else if (saveGhostEl) {
      saveGhostEl.remove();
      saveGhostEl = null;
    }

    // the active preview belongs to the users current hover or a fullscreened card; abort only when it is no longer in the virtual window or favorites.
    if (activePreview &&
        !gridEl.contains(activePreview) &&
        !favoritesGridEl.contains(activePreview)) {
      abortVideoLoad(activePreview);
      activePreview = null;
    }
    const maxScrollTop = Math.max(0, scrollAreaEl.scrollHeight - scrollAreaEl.clientHeight);
    const restoredScrollTop = Math.min(previousScrollTop, maxScrollTop);
    if (Math.abs(scrollAreaEl.scrollTop - restoredScrollTop) > 1) {
      scrollAreaEl.scrollTop = restoredScrollTop;
    }
    requestAnimationFrame(measureVirtualRow);
    loadMoreClipsIfNeeded(false);
  }

  function queueVirtualRender(force) {
    if (fullscreenHostTransition) {
      deferVirtualRenderUntilFullscreenSettles(force);
      return;
    }
    if (theaterPreview) {
      pendingRenderOnTheaterExit = true;
      return;
    }
    if (force) {
      virtualRangeStart = -1;
      virtualRangeEnd = -1;
    }
    virtualRenderForceQueued = virtualRenderForceQueued || !!force;
    if (virtualRenderQueued) return;
    virtualRenderQueued = true;
    requestAnimationFrame(() => {
      const shouldForce = virtualRenderForceQueued;
      virtualRenderForceQueued = false;
      renderVirtualWindow(shouldForce);
    });
  }

  function normalizeClipsPayload(data, requestedOffset) {
    if (Array.isArray(data)) {
      return {
        paged: false,
        version: clipsListSignature(data),
        total: data.length,
        offset: 0,
        clips: data,
      };
    }
    const clips = Array.isArray(data && data.clips) ? data.clips : [];
    return {
      paged: true,
      version: String((data && data.version) || ""),
      folder: String((data && data.folder) || ""),
      total: Math.max(0, Number((data && data.total) || clips.length) || 0),
      linked: Math.max(0, Number(data && data.linked) || 0),
      offset: Math.max(0, Number((data && data.offset) || requestedOffset) || 0),
      clips,
    };
  }

  async function fetchClipsPage(offset, refresh, sortValue) {
    const requestSort = CLIP_SORT_VALUES.has(sortValue) ? sortValue : activeSort;
    const url = HELPER + "/clips?offset=" + encodeURIComponent(offset)
      + "&limit=" + encodeURIComponent(CLIPS_PAGE_LIMIT)
      + "&sort=" + encodeURIComponent(requestSort)
      + (refresh ? "&refresh=1" : "")
      + "&t=" + Date.now();
    const res = await fetch(url, { cache: "no-store" });
    if (!res.ok) throw new Error("HTTP " + res.status);
    return normalizeClipsPayload(await res.json(), offset);
  }

  function mergeClipsPage(page, base) {
    const merged = base.slice(0, Math.max(base.length, page.offset + page.clips.length));
    for (let i = 0; i < page.clips.length; i++) {
      merged[page.offset + i] = page.clips[i];
    }
    if (page.total < merged.length) merged.length = page.total;
    return merged.filter(Boolean);
  }

  async function fetchClips() {
    const forceRefresh = clipsRefreshCounter++ % CLIPS_FORCE_REFRESH_EVERY === 0;
    const requestSort = activeSort;
    const page = await fetchClipsPage(0, forceRefresh, requestSort);
    if (requestSort !== activeSort) return null;
    if (page.paged && page.folder && page.folder !== clipsFolder) {
      const switched = clipsFolder !== "";
      clipsFolder = page.folder;
      if (switched) forgetFolderState();
    }
    if (!page.paged) {
      clipsPageVersion = page.version;
      clipsTotal = page.total;
      clipsTotalKnown = true;
      clipsLinkedTotal = page.linked;
      clipsNextOffset = page.clips.length;
      return page.clips;
    }
    const previousVersion = clipsPageVersion;
    const sameVersion = page.version && page.version === previousVersion;
    clipsPageVersion = page.version;
    clipsTotal = page.total;
    clipsTotalKnown = true;
    clipsLinkedTotal = page.linked;
    // a list that changed keeps the pages read so far until their new copies come in, so what is on the screen does not go blank for a moment. a reset that was asked for (a new sort, a new folder) and a different order start over
    const sameOrder = previousVersion !== "" && previousVersion.split("|sort:")[1] === String(page.version).split("|sort:")[1];
    const keep = (sameVersion || sameOrder) && loadedClips.length > page.clips.length;
    clipsNextOffset = Math.min(page.total, sameVersion && keep ? Math.max(loadedClips.length, page.offset + page.clips.length) : page.offset + page.clips.length);
    if (keep || (sameVersion && loadedClips.length)) {
      return mergeClipsPage(page, loadedClips);
    }
    return page.clips;
  }

  // the clip folder was switched: nothing read from the old one may stay, neither the loaded clips nor the favorites and drafts found in it nor the counts of the tabs, which come back from the new folder
  function forgetFolderState() {
    if (screenshotGallery) screenshotGallery.reset();
    clipsPageVersion = "";
    clipsTotal = 0;
    clipsTotalKnown = false;
    clipsLinkedTotal = 0;
    clipsNextOffset = 0;
    loadedClips = [];
    lastClipsByName = new Map();
    resolvedFavoriteClips = [];
    resolvedDraftClips = new Map();
    draftResolveKey = "";
    tabCountsShown = null;
    try { localStorage.removeItem(TAB_COUNTS_KEY); } catch {}
    scrollAreaEl.scrollTop = 0;
    resetVirtualWindow();
    refreshFavoriteClips();
    refreshDrafts();
  }

  async function loadMoreClipsIfNeeded(force) {
    if (HOME_MODE || clipsLoadingMore || document.hidden || clipsNextOffset >= clipsTotal) return;
    // a list that is not very long is read in full right away, so every clip is there before anybody scrolls to it; a very long one is read ahead of the scroll
    if (!force && clipsNextOffset >= CLIPS_EAGER_LIMIT) {
      const loadedRows = Math.ceil(allClips.length / Math.max(1, virtualColumns));
      const loadedEnd = (gridEl.offsetTop - scrollAreaEl.offsetTop) + loadedRows * virtualRowStride;
      if (loadedEnd - (scrollAreaEl.scrollTop + scrollAreaEl.clientHeight) > virtualRowStride * CLIPS_LOAD_AHEAD_ROWS) return;
    }
    clipsLoadingMore = true;
    try {
      const requestSort = activeSort;
      const page = await fetchClipsPage(clipsNextOffset, false, requestSort);
      if (requestSort !== activeSort) return;
      if (!page.paged) return;
      if (page.version !== clipsPageVersion) {
        // the list was rebuilt between the pages: the first page is read again, and the pages read so far stay on the screen until their new copies are in
        scheduleNextPoll(0);
        return;
      }
      clipsTotal = page.total;
      clipsTotalKnown = true;
      clipsLinkedTotal = page.linked;
      clipsNextOffset = Math.min(page.total, page.offset + page.clips.length);
      const merged = mergeClipsPage(page, loadedClips);
      const byName = new Map(lastClipsByName);
      for (const c of page.clips) byName.set(c.name, c);
      lastClipsByName = byName;
      renderLoadedClips(merged);
    } catch {
      // the regular poll path will surface helper connectivity errors.
    } finally {
      clipsLoadingMore = false;
    }
  }

  async function fetchUploadStatus() {
    const res = await fetch(HELPER + "/status?t=" + Date.now(), { cache: "no-store" });
    if (!res.ok) return null;
    return await res.json().catch(() => null);
  }

  function uploadJobsFromStatus(status) {
    if (!status) return [];
    const jobs = Array.isArray(status.jobs) ? status.jobs.slice() : [];
    if (status.clipName && !jobs.some(j => j && j.requestId === status.requestId)) {
      jobs.push(status);
    }
    return jobs.filter(j => j && j.clipName);
  }

  function syncUploadJobState(status) {
    const jobs = uploadJobsFromStatus(status);
    const byName = new Map();
    const newErrors = [];
    const completed = [];
    let active = false;
    // drop grace entries whose job has already vanished from the status feed.
    if (cancelledUploads.size) {
      const now = Date.now();
      for (const [name, ts] of Array.from(cancelledUploads)) {
        if (now - ts > CANCEL_GRACE_MS) cancelledUploads.delete(name);
      }
    }
    for (const job of jobs) {
      // a save in the editor has a popup of its own and answers on its own request, so it is no business of the cards and never an upload error
      if (job.kind === "trim") continue;
      const st = job.state || "";
      // just-cancelled clip: skip its still-active job, and pre-mark its error=Cancelled as seen so poll() doesnt raise an "Upload failed" toast.
      const cancelledAt = cancelledUploads.get(job.clipName);
      if (cancelledAt !== undefined && Date.now() - cancelledAt <= CANCEL_GRACE_MS) {
        if (job.active) continue;
        cancelledUploads.delete(job.clipName);
        seenUploadErrors.add(job.requestId || job.clipName);
        continue;
      }
      const pending = pendingLinkCreations.get(job.clipName);
      const matchesPendingRequest = pending &&
        (!pending.requestId || !job.requestId || pending.requestId === job.requestId);
      const isBusy = job.active && (st === "uploading" || st === "compressing");
      if (isBusy) {
        active = true;
        byName.set(job.clipName, job);
        localState.set(job.clipName, st);
      } else if (st === "done" && job.url) {
        if (matchesPendingRequest) pendingLinkCreations.delete(job.clipName);
        const completionKey = job.requestId || (job.clipName + "|" + job.url);
        if (!seenUploadCompletions.has(completionKey)) {
          seenUploadCompletions.add(completionKey);
          completed.push(job);
        }
      } else if (st === "error") {
        if (matchesPendingRequest) pendingLinkCreations.delete(job.clipName);
        // a job that was cancelled ends in the error state too, with the phase cancelled; it is no failure to tell about, even to a page that only loaded after the cancel
        if (job.error && job.phase !== "cancelled" && !seenUploadErrors.has(job.requestId || job.clipName)) {
          seenUploadErrors.add(job.requestId || job.clipName);
          newErrors.push(job);
          byName.set(job.clipName, job);
        }
      }
    }
    lastUploadJobsByName = byName;
    for (const [name, st] of Array.from(localState)) {
      if ((st !== "uploading" && st !== "compressing") || byName.has(name)) continue;
      // a UI action just set this optimistically and the helper job isnt in /status yet -- dont reap it (would flash the card back to "Create Link" for a poll).
      if (optimisticStateFresh(name)) continue;
      optimisticStateAt.delete(name);
      localState.delete(name);
    }
    return { active, jobs, newErrors, completed };
  }

  function applyCompletedUploadLinks(clips, completed) {
    const urlsByName = new Map();
    for (const job of completed) {
      if (job && job.clipName && job.url) urlsByName.set(job.clipName, job.url);
    }
    if (!urlsByName.size) return clips;
    const applyLink = (clip) => {
      const url = clip && urlsByName.get(clip.name);
      if (!url || clip.streamable_url === url) return clip;
      // stamp the same initial transcode state the helpers MarkUploaded writes, so the card goes straight to the amber "processing" bar instead of flashing "Copy Link" + "link NaNm left" before the next /clips lands.
      return { ...clip, streamable_url: url, ready: false, transcode_status: 1, transcode_percent: 0 };
    };
    resolvedFavoriteClips = resolvedFavoriteClips.map(applyLink);
    return clips.map(applyLink);
  }

  // what the helper is doing with a clip right now, in words that finish "this clip is": a file that is being uploaded or compressed is refused when the edit is saved, or is replaced under the editor
  function clipBusyWith(name) {
    const job = lastUploadJobsByName.get(name);
    const state = job && job.active ? job.state : localState.get(name);
    if (state === "compressing") return "being compressed";
    if (state === "uploading") return "being uploaded";
    return "";
  }

  // the one way into the editor from a card: a clip that is being uploaded or compressed is told so instead
  function requestOpenEditor(clip) {
    if (!clip) return;
    const busyWith = clipBusyWith(clip.name);
    if (busyWith) {
      showToast("This clip is " + busyWith + " right now. Edit it once that has finished, or cancel it first.", "bad", "The clip is busy");
      return;
    }
    openEditor(clip);
  }

  function hasPendingLinkCreation(name) {
    const pending = pendingLinkCreations.get(name);
    if (!pending) return false;
    if (Date.now() - pending.startedAt > PENDING_LINK_CREATION_MS) {
      pendingLinkCreations.delete(name);
      return false;
    }
    return true;
  }

  async function cancelActiveUpload(filename, requestId, suppressToast) {
    // cancellation always wins, including while Create Link is still waiting for /upload to return its request ID.
    if (filename) { linkTransitionBusy.add(filename); bulkUploadInFlight.delete(filename); }
    try {
      // repaint the card before the helper round-trip. drop every local trace of the job and null out the last status snapshot so renderActions cant re-derive "uploading" from stale data; the grace window in syncUploadJobState covers the poll or two before the helper catches up.
      if (filename) {
        cancelledUploads.set(filename, Date.now());
        progressVisuals.delete(filename);
        localState.delete(filename);
        pendingLinkCreations.delete(filename);
        bulkUploadQueued.delete(filename);
        lastUploadJobsByName.delete(filename);
        if (lastUploadStatus && lastUploadStatus.clipName === filename) lastUploadStatus = null;
        anyUploading = localState.size > 0;
        rerenderCard(filename);
      } else {
        localState.clear();
        pendingLinkCreations.clear();
        bulkUploadQueued.clear();
        lastUploadJobsByName.clear();
        lastUploadStatus = null;
        anyUploading = false;
      }
      if (!suppressToast) {
        showToast("Upload cancelled", "info");
        scheduleToastHide(2500);
      }
      // build with encodeURIComponent, NOT URLSearchParams -- the latter encodes spaces as "+", which the helpers Uri.UnescapeDataString leaves as literal "+", so a clip name with spaces (every replay clip) never matched the running job and the cancel silently no-opd.
      let url = HELPER + "/cancel-upload?t=" + Date.now();
      if (filename) url += "&file=" + encodeURIComponent(filename);
      if (requestId) url += "&requestId=" + encodeURIComponent(requestId);
      const req = fetch(url, { method: "POST", cache: "no-store" }).catch(() => {});
      if (filename) inFlightCancels.set(filename, req);
      try {
        await req;
      } finally {
        if (filename && inFlightCancels.get(filename) === req) inFlightCancels.delete(filename);
      }
      scheduleNextPoll(POLL_FAST_MS);
    } finally {
      if (filename) linkTransitionBusy.delete(filename);
    }
  }

  async function triggerUpload(filename, quiet) {
    // if this clip was just cancelled, let the /cancel-upload request land first -- otherwise the helper still sees the old job active and rejects this as busy.
    const priorCancel = inFlightCancels.get(filename);
    if (priorCancel) { try { await priorCancel; } catch {} }
    // quiet=1 for bulk uploads: helper skips the per-clip windows balloon + clipboard write.
    const url = HELPER + "/upload?file=" + encodeURIComponent(filename) + (quiet ? "&quiet=1" : "") + "&t=" + Date.now();
    const res = await fetch(url, { method: "POST", cache: "no-store" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      // attach the toobig signal + size info to the thrown error so oncreatelink can pop the "compress this" modal instead of showing a generic toast.
      const err = new Error(data.message || data.error || ("HTTP " + res.status));
      if (data.tooBig) {
        err.tooBig = true;
        err.sizeMb = Number(data.sizeMb) || 0;
        err.capMb  = Number(data.capMb)  || 0;
      }
      // over streamables 10-min limit -- no compress fix (compression doesnt shorten), just a clear message.
      if (data.tooLong) {
        err.tooLong = true;
        err.durationSec = Number(data.durationSec) || 0;
      }
      // helper returns busy=true (409) at the concurrency cap. the bulk queue backs off and retries instead of failing the clip.
      if (data.busy) err.busy = true;
      throw err;
    }
    return data;
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      const ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      let ok = false;
      try { ok = document.execCommand("copy"); } catch {}
      document.body.removeChild(ta);
      return ok;
    }
  }

  function isVideoFullscreen(video) {
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
    return !!(fsEl && video && (fsEl === video || (fsEl.contains && fsEl.contains(video))));
  }

  const VIDEO_ZOOM_MAX = 4;
  const VIDEO_ZOOM_STEP = 1.12;

  function resetVideoZoom(video) {
    if (!video) return;
    if (typeof video._resetZoom === "function") {
      video._resetZoom();
      return;
    }
    video.style.removeProperty("transform");
    video.style.removeProperty("transform-origin");
    video.removeAttribute("data-zoom");
  }

  function enableCursorZoom(container, video, canZoom) {
    let scale = 1;
    let panX = 0;
    let panY = 0;

    function applyZoom() {
      const zoomed = scale > 1.001;
      video.style.transformOrigin = "0 0";
      video.style.transform = zoomed
        ? "translate(" + panX + "px, " + panY + "px) scale(" + scale + ")"
        : "";
      if (zoomed) video.dataset.zoom = Math.round(scale * 100) + "%";
      else video.removeAttribute("data-zoom");
    }

    function reset() {
      scale = 1;
      panX = 0;
      panY = 0;
      video.style.removeProperty("transform");
      video.style.removeProperty("transform-origin");
      video.removeAttribute("data-zoom");
    }

    video._resetZoom = reset;
    container.addEventListener("wheel", (e) => {
      if (!canZoom() || !Number.isFinite(e.deltaY) || e.deltaY === 0) return;
      if (e.target.closest && e.target.closest("button, input, select, .progress-bar, .settings-menu")) return;
      const rect = container.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;

      const nextScale = Math.max(
        1,
        Math.min(VIDEO_ZOOM_MAX, scale * Math.pow(VIDEO_ZOOM_STEP, -e.deltaY / 100))
      );
      const x = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
      const y = Math.max(0, Math.min(rect.height, e.clientY - rect.top));
      const sourceX = (x - panX) / scale;
      const sourceY = (y - panY) / scale;
      scale = nextScale;
      panX = x - sourceX * scale;
      panY = y - sourceY * scale;
      panX = Math.max(rect.width - rect.width * scale, Math.min(0, panX));
      panY = Math.max(rect.height - rect.height * scale, Math.min(0, panY));
      applyZoom();
      e.preventDefault();
      e.stopPropagation();
    }, { passive: false });
  }

  function clearHoverPreviewTimer(video) {
    if (!video) return;
    if (video._hoverPreviewTimer) {
      clearTimeout(video._hoverPreviewTimer);
      video._hoverPreviewTimer = 0;
    }
    video._hoverPreviewSeq = (video._hoverPreviewSeq || 0) + 1;
  }

  function clearPreviewVisualState(video) {
    const preview = video && video.closest && video.closest(".preview");
    if (preview) preview.classList.remove("is-playing", "is-video-ready");
  }

  function resetPlaybackRate(video) {
    if (!video) return;
    video.defaultPlaybackRate = 1;
    video.playbackRate = 1;
    // load() can discard queued media events; synchronize the controls before teardown.
    video.dispatchEvent(new Event("ratechange"));
  }

  function abortVideoLoad(video) {
    if (!video) return;
    clearHoverPreviewTimer(video);
    clearPreviewVisualState(video);
    video.pause();
    resetPlaybackRate(video);
    video.removeAttribute("src");
    try { video.load(); } catch {}
  }

  function stopActivePreviewForScroll() {
    if (!activePreview || isVideoFullscreen(activePreview) || isTheaterPreview(activePreview.closest(".preview"))) return;
    const video = activePreview;
    activePreview = null;
    abortVideoLoad(video);
  }

  function syncFavoriteButton(card, name) {
    const btn = card.querySelector(".favorite-btn");
    const fav = isFavorite(name);
    if (btn) {
      btn.classList.toggle("is-favorite", fav);
      btn.dataset.tooltip = fav ? "Remove favorite" : "Favorite clip";
      btn.setAttribute("aria-label", btn.dataset.tooltip);
      btn.innerHTML = `<svg viewBox="0 0 24 24">${fav ? ICON.STAR : ICON.STAR_BORDER}</svg>`;
    }
    const menuItem = card.querySelector(".favorite-menu-item");
    if (!menuItem) return;
    const label = menuItem.querySelector(".menu-item-label");
    const icon = menuItem.querySelector("svg");
    menuItem.classList.toggle("is-favorite", fav);
    menuItem.setAttribute("aria-label", fav ? "Remove favorite" : "Favorite");
    if (label) label.textContent = fav ? "Remove favorite" : "Favorite";
    if (icon) icon.innerHTML = fav ? ICON.STAR : ICON.STAR_BORDER;
  }

  function syncClipSelection(card, name) {
    if (!card || !name) return;
    const selected = selectedClipNames.has(name);
    card.classList.toggle("is-selected", selected);
    const btn = card.querySelector(".select-clip-btn");
    if (btn) {
      btn.dataset.tooltip = selected ? "Deselect clip" : "Select clip";
      btn.setAttribute("aria-label", btn.dataset.tooltip);
      btn.setAttribute("aria-pressed", selected ? "true" : "false");
    }
    const menuItem = card.querySelector(".select-menu-item");
    if (!menuItem) return;
    const label = menuItem.querySelector(".menu-item-label");
    menuItem.setAttribute("aria-label", selected ? "Deselect clip" : "Select clip");
    if (label) label.textContent = selected ? "Deselect" : "Select";
  }

  function refreshBulkSelectionUi() {
    const bulkActions = document.getElementById("bulk-actions");
    const bulkSummary = document.getElementById("bulk-summary");
    const bulkCount = document.getElementById("bulk-count");
    const bulkSize = document.getElementById("bulk-size");
    const count = selectedClipNames.size;
    if (bulkActions) bulkActions.hidden = count === 0;
    if (bulkSummary) bulkSummary.hidden = count === 0;
    if (bulkClearBtn) bulkClearBtn.hidden = count === 0;
    let totalBytes = 0;
    selectedClipNames.forEach((name) => {
      const clip = lastClipsByName.get(name) || loadedClips.find((item) => item && item.name === name);
      totalBytes += Math.max(0, Number(clip && clip.size) || 0);
    });
    if (bulkCount) bulkCount.textContent = count + " clip" + (count === 1 ? "" : "s") + " selected";
    if (bulkSize) bulkSize.textContent = formatSize(totalBytes);
    document.body.classList.toggle("is-selection-mode", count > 0);
    if (count > 0 && activePreview && !isVideoFullscreen(activePreview)) {
      const preview = activePreview;
      activePreview = null;
      abortVideoLoad(preview);
    }
    document.querySelectorAll(".card[data-name]").forEach((card) => syncClipSelection(card, card.dataset.name));
    if (typeof syncBulkUploadButtons === "function") syncBulkUploadButtons();
    if (typeof compressAllRunning !== "undefined" && !compressAllRunning) setCompressAllButton("idle");
  }

  function toggleClipSelection(name) {
    if (!name) return;
    if (selectedClipNames.has(name)) selectedClipNames.delete(name);
    else selectedClipNames.add(name);
    resetBulkDeleteButton();
    refreshBulkSelectionUi();
  }

  function clearClipSelection() {
    selectedClipNames.clear();
    resetBulkDeleteButton();
    refreshBulkSelectionUi();
  }

  function currentClipForCard(card) {
    const name = card && card.dataset ? card.dataset.name : "";
    if (!name) return null;
    return lastClipsByName.get(name) || loadedClips.find(c => c && c.name === name) || null;
  }

  function resetDeleteButton(btn) {
    if (!btn) return;
    if (btn._deleteArmTimer) {
      clearTimeout(btn._deleteArmTimer);
      btn._deleteArmTimer = 0;
    }
    btn.classList.remove("is-armed");
    btn.dataset.tooltip = btn.dataset.deleteLabel || "Delete clip";
    btn.setAttribute("aria-label", btn.dataset.deleteLabel || "Delete clip");
    const label = btn.querySelector(".menu-item-label");
    if (label) label.textContent = "Delete";
  }

  function resetAllArmedDeleteButtons(exceptBtn) {
    document.querySelectorAll(".delete-btn.is-armed").forEach((btn) => {
      if (btn !== exceptBtn) resetDeleteButton(btn);
    });
  }

  function armDeleteButton(btn) {
    if (!btn) return;
    resetDeleteButton(btn);
    btn.classList.add("is-armed");
    btn.dataset.tooltip = "Click again to delete";
    btn.setAttribute("aria-label", "Click again to delete");
    const label = btn.querySelector(".menu-item-label");
    if (label) label.textContent = "Delete?";
    btn._deleteArmTimer = setTimeout(() => resetDeleteButton(btn), 3000);
  }

  document.addEventListener("click", (event) => {
    const inside = event.target.closest && event.target.closest(".settings-wrap");
    document.querySelectorAll(".settings-wrap.is-open").forEach((wrap) => {
      if (wrap !== inside) wrap.classList.remove("is-open");
    });
  });
  function buildCard(clip) {
    const card = document.createElement("div");
    card.className = "card rk-hover-card" + (selectedClipNames.has(clip.name) ? " is-selected" : "");
    card.dataset.name = clip.name;
    card.dataset.clipSig = clipSignature(clip);
    card.dataset.mtime = String(clip.mtime || 0);
    // cache-bust the file url with size + mtime so an overwritten clip (trim-overwrite, compress-overwrite) is fetched fresh instead of cefs stale-cached bytes. mtime alone isnt enough: the compress worker deliberately restores the source mtime so clips dont jump to the top of the list, but the file content and size both change. with v=<size>-<mtime>, any of those three changing (rename, trim, compress) yields a fresh url and a clean fetch.
    card.dataset.previewUrl = clipPreviewUrlFor(clip);
    card.dataset.version = clipVersion(clip);
    card.dataset.duration = String(Number(clip.duration) || 0);
    if (Number(clip.duration) > 0) durationCache.set(clip.name, Number(clip.duration));
    const thumbUrl   = thumbUrlFor(clip);
    const nameEsc    = escapeHtml(displayClipName(clip.name));
    // hevc still tagged hev1 hasnt been through the auto-fix yet and may not play on an iphone -- every other codec/tag combo is just informational, so only that one gets the warning tint.
    const codecReady   = replayKitSettings.showCodecTags && !!clip.codec;
    const codecUnfixed = clip.codec === "hevc" && !!clip.tag && clip.tag !== "hvc1";
    const codecLabel   = codecReady ? escapeHtml(String(clip.codec).toUpperCase()) : "";

    card.innerHTML = `
      <div class="preview">
        <!-- thumb + video share an overflow:hidden wrapper so the hover zoom is clipped to the frame (the .preview box itself keeps an overflow-clip-margin for the scrub dot, which would otherwise let the zoom spill past the rounded corners). -->
        <div class="preview-media">
          <img class="thumb" src="${EMPTY_THUMB}" data-src="${thumbUrl}" loading="lazy" decoding="async" alt="">
          <!-- Chromes fullscreen 3-dot "more options" menu only appears when at least one item is enabled, so disabling every possible entry (nodownload, noplaybackrate, noremoteplayback, disablePictureInPicture) makes the button itself disappear; we deliberately do not add 'nofullscreen' since that hides the separate fullscreen toggle button, not the 3-dot menu. -->
          <video preload="none" muted loop playsinline
                 controlsList="nodownload noplaybackrate noremoteplayback"
                 disablePictureInPicture></video>
        </div>
        <div class="draft-mark" aria-hidden="true"><svg viewBox="0 0 24 24">${ICON.OPEN_EDITOR}</svg></div>
        <button class="locate-btn" type="button" data-tooltip="Show in Clips" aria-label="Show in Clips">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 19H5V5h7V3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z"/></svg>
        </button>
        <div class="processing-overlay" aria-hidden="true"><div class="processing-spinner"></div><span>Processing&hellip;</span></div>
        <button class="select-clip-btn" type="button" data-tooltip="Select clip" aria-label="Select clip" aria-pressed="false">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <circle class="select-ring" cx="12" cy="12" r="8"></circle>
            <path class="select-check" d="m7.5 12 3 3 6-6"></path>
          </svg>
        </button>
        <button class="favorite-btn" type="button" data-tooltip="Favorite clip" aria-label="Favorite clip">
          <svg viewBox="0 0 24 24">${isFavorite(clip.name) ? ICON.STAR : ICON.STAR_BORDER}</svg>
        </button>
        <div class="play-overlay"></div>
        <!-- YouTube-style flash: pops up in the center for ~500ms every time the user toggles play/pause via click or space, then fades out; js picks which icon to show. -->
        <div class="center-pulse">
          <svg viewBox="0 0 24 24" class="cp-play">${ICON.PLAY}</svg>
          <svg viewBox="0 0 24 24" class="cp-pause" style="display:none">${ICON.PAUSE}</svg>
        </div>
        <div class="player-shade"></div>
        ${RKClipPlayer.controlsHtml(ICON, VOLUME_ICON)}
        <div class="progress-bar">
          <!-- hover "ghost fill" sits behind the real fill; CSS sizes it from 0 to the cursors x position so the user can preview where a click will seek to, same as YouTubes lighter-coloured band. -->
          <div class="progress-hover-fill"></div>
          <div class="progress-fill">
            <div class="progress-scrub-dot"></div>
          </div>
          <!-- timestamp tooltip that follows the cursor: js positions it horizontally and fills it with the formatted time at that x, CSS handles centering above the bar. -->
          <div class="progress-hover-time"></div>
        </div>
        <span class="duration-badge" data-duration=""></span>
        <span class="codec-badge${codecReady ? " is-ready" : ""}${codecUnfixed ? " is-unfixed" : ""}">${codecLabel}</span>
      </div>
      <div class="body">
        <div class="title-row">
          <span class="name" title="${nameEsc}">${nameEsc}</span>
          <button class="icon-mini-btn edit-btn" data-tooltip="Rename" type="button">
            <svg viewBox="0 0 24 24">${ICON.EDIT}</svg>
          </button>
          <div class="title-actions">
            <button class="icon-mini-btn delete-btn" data-tooltip="Delete clip" aria-label="Delete clip" type="button">
              <svg viewBox="0 0 24 24">${ICON.TRASH}</svg>
              <span class="delete-confirm-text">Delete?</span>
            </button>
            <div class="menu-container">
              <button class="icon-mini-btn menu-btn" data-tooltip="More options" type="button">
                <svg viewBox="0 0 24 24">${ICON.MORE}</svg>
              </button>
              <div class="menu-dropdown">
                <div class="menu-heading">Share</div>
                <div class="menu-item share-menu-item link-menu-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.LINK}</svg>
                  <span class="menu-item-label">Create Link</span>
                </div>
                <div class="menu-item share-menu-item clipboard-clip-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.CLIPBOARD}</svg>
                  <span>Copy to clipboard</span>
                </div>
                <div class="menu-heading">Edit</div>
                <div class="menu-item trim-clip-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.CONTENT_CUT}</svg>
                  <span>Edit clip</span>
                </div>
                <div class="menu-item discard-draft-item" style="display:none">
                  <svg class="menu-item-icon" viewBox="0 0 16 16">${ICON.RESET}</svg>
                  <span>Delete draft</span>
                </div>
                <div class="menu-item rename-menu-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.EDIT}</svg>
                  <span>Edit name</span>
                </div>
                <div class="menu-item compress-clip-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.COMPRESS}</svg>
                  <span>Compress&hellip;</span>
                </div>
                <div class="menu-heading">Organize</div>
                <div class="menu-item favorite-menu-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.STAR_BORDER}</svg>
                  <span class="menu-item-label">Favorite</span>
                </div>
                <div class="menu-item select-menu-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.SELECT}</svg>
                  <span class="menu-item-label">Select</span>
                </div>
                <div class="menu-item duplicate-menu-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.DUPLICATE}</svg>
                  <span>Duplicate</span>
                </div>
                <div class="menu-item open-folder-item">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.FOLDER_OPEN}</svg>
                  <span>Open file location</span>
                </div>
                <div class="menu-separator"></div>
                <div class="menu-item delete-menu-item delete-btn" role="button" tabindex="0" aria-label="Delete clip">
                  <svg class="menu-item-icon" viewBox="0 0 24 24">${ICON.TRASH}</svg>
                  <span class="menu-item-label">Delete</span>
                </div>
              </div>
            </div>
          </div>
        </div>
        <div class="meta">
          <span class="size">${formatSize(clip.size)}</span>
          <span class="dot"></span>
          <span class="age" data-mtime="${clip.mtime}">${formatTimeAgo(clip.mtime)}</span>
        </div>
      </div>
      <div class="actions"></div>
      <div class="card-resize" data-tooltip="Drag to resize clips, double-click to reset">${CARD_GRIP_SVG}</div>
    `;

    syncProcessing(card, clip);
    applyClipHovers(card);
    const video        = card.querySelector("video");
    const preview      = card.querySelector(".preview");
    const progressFill = card.querySelector(".progress-fill");
    const volumeBtn    = card.querySelector(".volume-btn");
    const volumeSlider = card.querySelector(".volume-slider");
    const theaterBtn   = card.querySelector(".theater-btn");
    const fullscreenBtn= card.querySelector(".fullscreen-btn");
    const favoriteBtn  = card.querySelector(".favorite-btn");
    const selectBtn    = card.querySelector(".select-clip-btn");
    const editBtn      = card.querySelector(".edit-btn");
    const menuBtn      = card.querySelector(".menu-btn");
    const menuDropdown = card.querySelector(".menu-dropdown");
    const linkMenuItem = card.querySelector(".link-menu-item");
    const favoriteMenuItem = card.querySelector(".favorite-menu-item");
    const selectMenuItem = card.querySelector(".select-menu-item");
    const renameMenuItem = card.querySelector(".rename-menu-item");
    const deleteMenuItem = card.querySelector(".delete-menu-item");
    const openFolderEl = card.querySelector(".open-folder-item");
    const deleteClipEl = card.querySelector(".delete-btn");

    enableCursorZoom(preview, video, () => isVideoFullscreen(video) || isTheaterPreview(preview));

    const locateBtn = card.querySelector(".locate-btn");
    if (locateBtn) {
      locateBtn.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        openClipsFromHome("all", card.dataset.name);
      });
    }

    favoriteBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      const name = card.dataset.name;
      const next = !isFavorite(name);
      setFavorite(name, next);
      syncFavoriteButton(card, name);
      // little celebratory bounce when a clip is starred; a plain quick squish when it is un-starred.
      if (favoriteBtn.animate) {
        try {
          favoriteBtn.animate(
            next ? [{ scale: "0.6" }, { scale: "1.25", offset: 0.45 }, { scale: "1" }]
                 : [{ scale: "0.8" }, { scale: "1" }],
            { duration: next ? 280 : 150, easing: "cubic-bezier(.2, .7, .3, 1)" }
          );
        } catch (_) {}
      }
      updateTabs(loadedClips);
      // re-render when the toggle changes which section the card belongs to: on the favorites filter an unfavorite removes the card from view; on the all filter any toggle moves the card between the favorites sub-section and the main list.
      if ((activeFilter === "favorites" && !next) || activeFilter === "all") {
        renderLoadedClips(loadedClips);
      }
    });
    selectBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      toggleClipSelection(card.dataset.name);
    });
    // once the first circle is selected, cards themselves become the selection target. interactive child controls still keep their normal behavior -- .btn covers the in-card progress readout, which is a <div> not a <button> so it wouldnt match the bare button selector.
    card.addEventListener("click", (e) => {
      if (!document.body.classList.contains("is-selection-mode")) return;
      if (e.target.closest && e.target.closest("button, input, a, .btn, .actions, .menu-dropdown, .settings-menu")) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.stopImmediatePropagation) e.stopImmediatePropagation();
      toggleClipSelection(card.dataset.name);
    }, true);

    // preview playback
    function markPreviewVideoReady() {
      if (video.src && video.readyState >= 2) preview.classList.add("is-video-ready");
    }
    video.addEventListener("loadeddata", markPreviewVideoReady);
    video.addEventListener("canplay", markPreviewVideoReady);
    video.addEventListener("playing", () => {
      if (video === activePreview) {
        preview.classList.add("is-playing");
        markPreviewVideoReady();
      }
    });
    video.addEventListener("error", () => {
      preview.classList.remove("is-playing", "is-video-ready");
    });

    // youtube-style play/pause button + live time display the play-toggle button (only visible in fullscreen via css) is a third way to toggle playback alongside space and click-on-video. the two svgs inside it are swapped to reflect the current state; we listen on the native play/pause events so any other path (space key, end-of-video, etc.) keeps the icon in sync.
    const playToggleBtn = card.querySelector(".play-toggle-btn");
    const playIconShow  = playToggleBtn ? playToggleBtn.querySelector(".pt-play")  : null;
    const playIconHide  = playToggleBtn ? playToggleBtn.querySelector(".pt-pause") : null;
    function syncPlayIcon() {
      if (!playIconShow || !playIconHide) return;
      // show the opposite of the current state -- "play" icon when paused (becuase clicking will start playback), "pause" icon when playing (becuase clicking will pause). standard player affordance.
      if (video.paused) {
        playIconShow.style.display = "";
        playIconHide.style.display = "none";
      } else {
        playIconShow.style.display = "none";
        playIconHide.style.display = "";
      }
      // the tip names what the click will do, same as the icon
      playToggleBtn.dataset.tooltip = video.paused ? "Play" : "Pause";
    }
    if (playToggleBtn) {
      playToggleBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        // route thru the shared togglevideoplayback so the center-pulse fires whether you tap the bottom bar button, click on the video itself, or hit space -- one indicator, every toggle path.
        toggleVideoPlayback();
        try { playToggleBtn.blur(); } catch {}
      });
    }
    video.addEventListener("play",  syncPlayIcon);
    video.addEventListener("pause", syncPlayIcon);
    video.addEventListener("ended", syncPlayIcon);

    // live time display ("0:36 / 1:00"). wired into the same raf loop that drives the progress-fill (tickprogress, below) so we dont have two animation frames running for one playhead.
    const timeCurrentEl = card.querySelector(".time-current");
    const timeTotalEl   = card.querySelector(".time-total");
    function setTimeDisplay() {
      if (!timeCurrentEl || !timeTotalEl) return;
      if (isFinite(video.duration) && video.duration > 0) {
        timeTotalEl.textContent   = formatDuration(video.duration);
        timeCurrentEl.textContent = formatDuration(video.currentTime);
      }
    }
    // first fill once metadata is in (so total time replaces the placeholder "0:00" the markup ships with).
    video.addEventListener("loadedmetadata", setTimeDisplay);
    video.addEventListener("timeupdate",     setTimeDisplay);
    // the raf-driven progress fill (tickProgress below) only runs while playing, so an arrow-key / scrub seek on a PAUSED video left the bar frozen at the old spot. repaint it here on every seek.
    video.addEventListener("seeked", () => {
      setTimeDisplay();
      if (video.duration > 0) progressFill.style.width = (video.currentTime / video.duration * 100) + "%";
    });
    // block dblclick from exiting fullscreen. two redundant guards becuase chromium has multiple paths that can toggle fullscreen off, and we want to be sure none of them fire from a dblclick: on the <video>: chromiums mediaelementscontroller used to respond to a video-level dblclick by toggling fullscreen. since we now fullscreen the .preview container (not the <video>) that path is mostly inert, but the listener stays as belt-and-suspenders in case cef behaviour drifts. on the .preview container: a dblclick on the now-fullscreen <div> can still bubble to a parent handler that calls exitfullscreen. preventdefault + stoppropagation + capture phase + a stopimmediatepropagation kills every pathway. esc and the in-overlay close button still work -- they go thru document.exitfullscreen() directly, not dom dblclick.
    const blockDblclick = (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.stopImmediatePropagation) e.stopImmediatePropagation();
    };
    video.addEventListener("dblclick", blockDblclick, true);
    preview.addEventListener("dblclick", blockDblclick, true);
    // the htmlmediaelement `timeupdate` event only fires every ~250ms, which makes the bar visibly step. drive it from raf instead so it moves smoothly at the display refresh rate while the video plays, and stops cleanly when the video pauses/ends/aborts.
    let progressRaf = 0;
    function tickProgress() {
      if (video.duration > 0) {
        progressFill.style.width = (video.currentTime / video.duration * 100) + "%";
      }
      progressRaf = (video.paused || video.ended) ? 0 : requestAnimationFrame(tickProgress);
    }
    video.addEventListener("play", () => {
      if (!progressRaf) progressRaf = requestAnimationFrame(tickProgress);
    });
    video.addEventListener("pause", () => {
      if (progressRaf) { cancelAnimationFrame(progressRaf); progressRaf = 0; }
    });
    video.addEventListener("ended", () => {
      if (progressRaf) { cancelAnimationFrame(progressRaf); progressRaf = 0; }
      progressFill.style.width = "0%";
    });

    function scheduleHoverPreview() {
      clearHoverPreviewTimer(video);
      const now = performance.now ? performance.now() : Date.now();
      const scrollQuietFor = now - lastScrollActivityAt;
      const scrollDelay = Math.max(0, SCROLL_PREVIEW_SETTLE_MS - scrollQuietFor);
      const delay = Math.max(HOVER_PREVIEW_DELAY_MS, scrollDelay);
      const seq = video._hoverPreviewSeq;
      video._hoverPreviewTimer = setTimeout(() => {
        video._hoverPreviewTimer = 0;
        startHoverPreview(seq);
      }, delay);
    }

    function startHoverPreview(seq) {
      // dont disturb the currently-fullscreened video.
      if (document.fullscreenElement || document.webkitFullscreenElement || theaterPreview) return;
      if (seq !== video._hoverPreviewSeq || !card.matches(":hover") || card.classList.contains("is-processing")) return;
      const now = performance.now ? performance.now() : Date.now();
      if (now - lastScrollActivityAt < SCROLL_PREVIEW_SETTLE_MS) {
        scheduleHoverPreview();
        return;
      }
      const url = card.dataset.previewUrl;
      if (!url) return;
      if (activePreview && activePreview !== video) {
        abortVideoLoad(activePreview);
      }
      activePreview = video;
      preview.classList.add("is-playing");
      if (video.readyState < 2) preview.classList.remove("is-video-ready");
      applyVolume(video);
      if (video.getAttribute("src") !== url) {
        preview.classList.remove("is-video-ready");
        video.src = url;
      }
      try { video.currentTime = 0; } catch {}
      video.play().catch(() => {});
    }

    // bound on the whole card, not just .preview, so hovering the name/info row below the thumbnail also starts playback -- matches the .card:hover css that already fades the play-overlay/controls/badges for the whole card, not just the thumbnail box.
    card.addEventListener("mouseenter", () => {
      if (document.body.classList.contains("is-selection-mode")) return;
      // dont disturb the currently-fullscreened video.
      if (document.fullscreenElement || document.webkitFullscreenElement || theaterPreview) return;
      scheduleHoverPreview();
    });
    card.addEventListener("mouseleave", () => {
      // skip teardown while the preview (or its video) is the fullscreen element -- entering fullscreen takes .preview out of the cards normal flow (a viewport-filling fixed element), which shrinks the card down to just .body and makes the browser think the cursor left it; without this check wed abort the video and the user would see a frozen first frame the instant fullscreen opens.
      const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
      if (fsEl === preview || fsEl === video) return;
      if (isTheaterPreview(preview)) return;
      // skip teardown mid-drag on the scrubber too -- dragging the thin bar naturally drifts the cursor off it (and sometimes off the card entirely) while the user is focused on left-right movement, not pixel-perfect vertical alignment; pointer capture keeps the actual seek going regardless of where the cursor physically is, but a teardown here would still rip out the video mid-drag. endScrub (bound to pointerup/pointercancel) is what actually ends the drag.
      if (scrubbing) return;
      clearHoverPreviewTimer(video);
      if (video === activePreview) activePreview = null;
      preview.classList.remove("is-playing", "is-video-ready");
      progressFill.style.width = "0%";
      abortVideoLoad(video);
    });

    // scrubber (click + drag on the progress bar to seek) the .progress-bars hit area grows in fullscreen via css; the pointer-event listeners below are wired in every mode so a future "show scrubber in the dock too" tweak is just a css change. uses pointer events so the drag survives the cursor briefly leaving the bar -- mouse events would lose the drag the moment the cursor went above the bars 10 px strip.
    const progressBar = card.querySelector(".progress-bar");
    const hoverTimeEl = card.querySelector(".progress-hover-time");
    function seekToPointer(e) {
      const rect = progressBar.getBoundingClientRect();
      const pct  = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      if (video.duration > 0 && isFinite(video.duration)) {
        video.currentTime = pct * video.duration;
        // repaint the fill immediately so dragging feels responsive even if the raf loop is paused (e.g. video is paused).
        progressFill.style.width = (pct * 100) + "%";
      }
    }
    // youtube-style hover preview: writes two css vars on the bar each pointermove. --hover-fill is the band that fills from 0 to the cursor x (rendered by .progress-hover-fill); --hover-x is the pixel x position used to centre the time tooltip above the bar. both pure-css once the vars are set, so no relayout cost per move. skipped entirely when the bar isnt being hovered in fullscreen (the css only renders the elements inside .preview:fullscreen .progress-bar:hover anyway).
    progressBar.addEventListener("pointermove", (e) => {
      const rect = progressBar.getBoundingClientRect();
      const xPx  = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
      const pct  = rect.width ? xPx / rect.width : 0;
      progressBar.style.setProperty("--hover-fill", (pct * 100) + "%");
      progressBar.style.setProperty("--hover-x",     xPx + "px");
      if (hoverTimeEl && isFinite(video.duration) && video.duration > 0) {
        hoverTimeEl.textContent = formatDuration(pct * video.duration);
      }
    });
    progressBar.addEventListener("pointerleave", () => {
      // wipe the hover-fill back to 0 so it doesnt get stuck at the last cursor position the next time the bar is shown.
      progressBar.style.setProperty("--hover-fill", "0%");
    });
    let scrubbing = false;
    // true if the video was mid-playback when a scrub started -- nothing in the scrub handlers below ever paused it, so it kept playing the whole drag, just visually masked by seekToPointer firing on every pointermove faster than the browser could render a frame; the moment the cursor stopped moving there was nothing left to interrupt it, so it would visibly start playing until the next move, looking exactly like holding still while dragging "started" playback when it actually never stopped.
    let scrubResumePlay = false;
    // document-level fallback for the actual drag tracking, added only while scrubbing and removed the instant it ends -- setPointerCapture (below) is supposed to make this unnecessary, but in practice the drag was still dying the moment the cursor left the bar, so capture isnt reliably doing that here; listening on document instead sidesteps the question of why, since document always gets the move/up regardless of which element the cursor is actually over.
    function onDocPointerMove(e) {
      bumpIdle();
      seekToPointer(e);
    }
    function onDocPointerUp(e) {
      endScrub(e);
    }
    progressBar.addEventListener("pointerdown", (e) => {
      // only react to the primary mouse button / touch contact -- right-click on a scrubber should never seek.
      if (e.button !== 0 && e.pointerType === "mouse") return;
      scrubbing = true;
      // pause for the duration of the drag so scrubbing shows a still frame per position instead of racing an in-progress playback -- resumed in endScrub if it was actually playing.
      scrubResumePlay = !video.paused;
      if (scrubResumePlay) video.pause();
      // keep the bar in its expanded "hovered" look while the user drags. without this the bar collapses the moment the cursor moves vertically off the 14px strip, even though setpointercapture still routes the move events here.
      progressBar.classList.add("is-scrubbing");
      try { progressBar.setPointerCapture(e.pointerId); } catch {}
      document.addEventListener("pointermove", onDocPointerMove);
      document.addEventListener("pointerup", onDocPointerUp);
      document.addEventListener("pointercancel", onDocPointerUp);
      // pointer capture suppresses the bubbled mousemove on .preview, so the idle-hide timer would fire 2.5s into a drag and yank the whole scrubber out from under the user. bumping idle here (and on every captured move below) keeps the controls visible for the whole drag.
      bumpIdle();
      seekToPointer(e);
      e.stopPropagation();
      e.preventDefault();
    });
    progressBar.addEventListener("pointermove", (e) => {
      if (!scrubbing) return;
      bumpIdle();
      seekToPointer(e);
    });
    const endScrub = (e) => {
      if (!scrubbing) return;
      scrubbing = false;
      progressBar.classList.remove("is-scrubbing");
      try { progressBar.releasePointerCapture(e.pointerId); } catch {}
      document.removeEventListener("pointermove", onDocPointerMove);
      document.removeEventListener("pointerup", onDocPointerUp);
      document.removeEventListener("pointercancel", onDocPointerUp);
      if (scrubResumePlay) { scrubResumePlay = false; video.play().catch(() => {}); }
      // restart a fresh idle countdown from "now" so the user gets the full 2.5s after they let go.
      bumpIdle();
    };
    progressBar.addEventListener("pointerup",     endScrub);
    progressBar.addEventListener("pointercancel", endScrub);

    // fullscreen idle-hide for the overlay controls after 2.5 s with no mouse movement inside the fullscreened preview, fade the controls + scrubber out and hide the cursor. any movement reveals them again instantly. mirrors the standard "video player" idle behaviour the user expects.
    let idleTimer = 0;
    function bumpIdle() {
      preview.classList.remove("is-idle");
      if (idleTimer) { clearTimeout(idleTimer); idleTimer = 0; }
      // dont arm the idle timer while the user is mid-drag on the scrubber. the drag handler clears it; endscrub re-arms it.
      if (scrubbing) return;
      idleTimer = setTimeout(() => preview.classList.add("is-idle"), 2500);
    }
    preview.addEventListener("mousemove", () => {
      if (document.fullscreenElement === preview) bumpIdle();
    });
    preview.addEventListener("fullscreenchange", () => {
      const inFs = (document.fullscreenElement === preview);
      card.classList.toggle("is-fullscreen-card", inFs);
      updateFullscreenBodyState();
      if (inFs) bumpIdle();
      else {
        preview.classList.remove("is-idle");
        if (idleTimer) { clearTimeout(idleTimer); idleTimer = 0; }
        if (!isTheaterPreview(preview)) resetVideoZoom(video);
        resetPlaybackRate(video);
      }
      // swap the fullscreen toggle icon so it always shows the action itll perform next (youtube convention): the "exit" brackets when were in fullscreen, the "enter" brackets when were out. same listener fires for both directions.
      const enterIcon = fullscreenBtn.querySelector(".fs-enter-icon");
      const exitIcon  = fullscreenBtn.querySelector(".fs-exit-icon");
      if (enterIcon) enterIcon.style.display = inFs ? "none" : "";
      if (exitIcon)  exitIcon.style.display  = inFs ? ""     : "none";
      fullscreenBtn.dataset.tooltip = inFs ? "Exit fullscreen (F)" : "Fullscreen (F)";
      // leaving fullscreen also closes any open settings popup so a re-entry starts clean.
      if (!inFs) settingsWrap && settingsWrap.classList.remove("is-open");
    });

    // click-to-toggle play/pause on the video surface youtube convention: clicking the video toggles playback. we bind on the .preview container in capture phase so it only fires for empty space, not for clicks on overlays (controls, settings menu, scrubber, etc) -- those each call stoppropagation in their own handlers above. hover-only (dock) preview play stays untouched: this click only takes effect once a video is loaded.
    const centerPulse = card.querySelector(".center-pulse");
    const cpPlayIcon  = centerPulse ? centerPulse.querySelector(".cp-play")  : null;
    const cpPauseIcon = centerPulse ? centerPulse.querySelector(".cp-pause") : null;
    let pulseTimer = 0;
    function flashCenterPulse(showingPauseAfter) {
      if (!centerPulse) return;
      // show the icon that represents the new state after the click. after a play() the new state is "playing" -> pause icon.
      if (cpPlayIcon && cpPauseIcon) {
        cpPlayIcon.style.display  = showingPauseAfter ? "none" : "";
        cpPauseIcon.style.display = showingPauseAfter ? ""     : "none";
      }
      centerPulse.classList.remove("is-flashing");
      // force a reflow so re-adding the class restarts the transition even when the user mashes space rapidly.
      void centerPulse.offsetWidth;
      centerPulse.classList.add("is-flashing");
      if (pulseTimer) clearTimeout(pulseTimer);
      pulseTimer = setTimeout(
        () => centerPulse.classList.remove("is-flashing"),
        500
      );
    }
    function toggleVideoPlayback() {
      if (!video.src) return; // hover-only preview not yet loaded
      if (video.paused) {
        video.play().catch(() => {});
        flashCenterPulse(true); // about to be playing -> pause glyph
      } else {
        video.pause();
        flashCenterPulse(false); // about to be paused -> play glyph
      }
    }
    // expose on the card so the document-level space-key handler (which doesnt share this closure) can fire the same toggle + center-pulse path. keeps every toggle gesture visually consistent without having to plumb a global registry.
    card._togglePlayback = toggleVideoPlayback;
    // bind on .preview, not on the video element directly: the video is overlaid by .play-overlay (which already has its own click handler) and the player-shade gradient, both of which would swallow the click. using the preview catches the click for the visible video region while still respecting stoppropagation calls from inner controls.
    preview.addEventListener("click", (e) => {
      // only react to plain left clicks on the bare video area. skip any click that originated on a button / input / link / menu the user might be using.
      if (e.button !== 0) return;
      if (e.target.closest &&
          e.target.closest("button, input, a, .settings-menu, .progress-bar, .volume-slider-wrap")) {
        return;
      }
      // a card of the drafts tab opens its draft in the editor instead, unless it is the big player of theater or full screen
      if (card.classList.contains("is-draft-card") && !card.classList.contains("is-theater-card") && !card.closest(".rk-fs") && !document.fullscreenElement) {
        requestOpenEditor(loadedClips.find((c) => c.name === card.dataset.name));
        return;
      }
      toggleVideoPlayback();
    });

    // playback-speed settings popup anchored to the gear; only ever visible in fullscreen (the .settings-wrap is display:none outside via css). gear toggles the popup; slider + pills both write video.playbackrate and mirror each other so changing one updates the other instantly.
    const settingsWrap   = card.querySelector(".settings-wrap");
    const settingsBtn    = card.querySelector(".settings-btn");
    const settingsMenu   = card.querySelector(".settings-menu");
    const speedSlider    = card.querySelector(".speed-slider");
    const speedCurrent   = card.querySelector(".speed-current-value");
    const speedDownBtn   = card.querySelector(".speed-down");
    const speedUpBtn     = card.querySelector(".speed-up");
    const speedPills     = card.querySelectorAll(".speed-pill");
    // clamp ranges. the typed input, slider, and pills all share this ceiling -- browsers support up to 16x via htmlmediaelement.playbackrate. anything outside [0.1, 16] is silently rejected.
    const SPEED_MIN = 0.1;
    const SPEED_MAX = 16;
    function setPlaybackRate(rate, source) {
      const parsed = parseFloat(rate);
      if (!isFinite(parsed)) return;
      rate = Math.max(SPEED_MIN, Math.min(SPEED_MAX, parsed));
      // playbackrate has weird floating-point precision quirks; snap to the nearest 0.05 so the readout stays clean.
      rate = Math.round(rate * 20) / 20;
      // Unloading the source restores playbackRate from defaultPlaybackRate.
      video.defaultPlaybackRate = rate;
      video.playbackRate = rate;
      syncPlaybackRate(source);
    }

    function syncPlaybackRate(source) {
      const rate = video.playbackRate;
      // dont stomp the input while the user is mid-edit -- theyd see their typing get rewritten character-by-character.
      if (speedCurrent && source !== "input") {
        speedCurrent.value = rate.toFixed(2);
      }
      // mirror state across slider + pills. skip the source so we dont fight the users in-flight input.
      if (speedSlider && source !== "slider") {
        // read the sliders own min/max so this cant drift out of sync with the html again -- clamp the visible thumb position even when the input went higher than the sliders range.
        const sliderMin = Number(speedSlider.min) || SPEED_MIN;
        const sliderMax = Number(speedSlider.max) || SPEED_MAX;
        speedSlider.value = String(Math.max(sliderMin, Math.min(sliderMax, rate)));
      }
      speedPills.forEach((el) => {
        const v = parseFloat(el.dataset.speed);
        el.classList.toggle("is-active", Math.abs(v - rate) < 0.001);
      });
    }
    video.addEventListener("ratechange", () => syncPlaybackRate());
    if (settingsBtn) {
      settingsBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        settingsWrap.classList.toggle("is-open");
        // reset the idle timer on open so the menu doesnt fade out a moment after the user opens it.
        if (document.fullscreenElement === preview) bumpIdle();
      });
    }
    if (settingsMenu) {
      // swallow internal clicks so the document-level outside-click dismiss below doesnt close us when the user picks a pill or moves the slider.
      settingsMenu.addEventListener("click", (e) => e.stopPropagation());
    }
    // editable custom-rate input. commits on enter or blur; escape reverts. focus() selects the whole text so typing replaces the current value instead of inserting next to it (matches the behaviour of every other "tap to retype" numeric field).
    if (speedCurrent) {
      speedCurrent.addEventListener("focus", () => {
        try { speedCurrent.select(); } catch {}
      });
      const commitTyped = () => {
        const v = parseFloat(speedCurrent.value);
        if (isFinite(v) && v > 0) {
          setPlaybackRate(v); // setplaybackrate clamps + rewrites the input
        } else {
          // restore the last good value so the field never sits in a bad state after blur.
          speedCurrent.value = (video.playbackRate || 1).toFixed(2);
        }
      };
      speedCurrent.addEventListener("blur", commitTyped);
      speedCurrent.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          commitTyped();
          try { speedCurrent.blur(); } catch {}
        } else if (e.key === "Escape") {
          e.preventDefault();
          speedCurrent.value = (video.playbackRate || 1).toFixed(2);
          try { speedCurrent.blur(); } catch {}
        }
        // block the docks document-level space=play-pause and escape=exit-fullscreen handlers from firing while were editing -- the user wants those keys to type/cancel here.
        e.stopPropagation();
      });
      // same defence for keyup -- the keydown stop above doesnt automatically prevent the document-level handlers re-firing on keyup in some browsers.
      speedCurrent.addEventListener("keyup", (e) => e.stopPropagation());
    }
    if (speedSlider) {
      speedSlider.addEventListener("input", (e) => {
        setPlaybackRate(e.target.value, "slider");
      });
    }
    // press-and-hold auto-repeat on the +/- steppers -- a plain click/tap still steps once immediately (via pointerdown); holding waits HOLD_REPEAT_DELAY_MS before it starts auto-stepping so a normal click cant accidentally rack up extra steps, then repeats at HOLD_REPEAT_INTERVAL_MS while the button stays down.
    const SPEED_STEP = 0.25;
    const HOLD_REPEAT_DELAY_MS = 400;
    const HOLD_REPEAT_INTERVAL_MS = 120;
    function bindSpeedStepHold(btn, step) {
      if (!btn) return;
      let delayTimer = 0;
      let repeatTimer = 0;
      let pointerHandled = false;
      const stepOnce = () => setPlaybackRate(video.playbackRate + step);
      const stopHold = () => {
        clearTimeout(delayTimer);
        clearInterval(repeatTimer);
        delayTimer = 0;
        repeatTimer = 0;
      };
      btn.addEventListener("pointerdown", (e) => {
        if (e.button > 0) return; // left/primary + touch/pen only, same scope the old click-only handler had
        e.stopPropagation();
        pointerHandled = true;
        stopHold(); // defensive -- a missed pointerup (eg. alt-tab mid-press) shouldnt stack timers
        stepOnce();
        delayTimer = setTimeout(() => {
          repeatTimer = setInterval(stepOnce, HOLD_REPEAT_INTERVAL_MS);
        }, HOLD_REPEAT_DELAY_MS);
      });
      btn.addEventListener("pointerup", stopHold);
      btn.addEventListener("pointerleave", stopHold);
      btn.addEventListener("pointercancel", stopHold);
      // keyboard activation (enter/space on a focused button) fires click with no pointerdown first -- handle that path with a single step so tab+enter still works, without double-stepping the mouse/touch path (which already stepped via pointerdown above).
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        if (pointerHandled) { pointerHandled = false; return; }
        stepOnce();
      });
    }
    bindSpeedStepHold(speedDownBtn, -SPEED_STEP);
    bindSpeedStepHold(speedUpBtn, SPEED_STEP);
    speedPills.forEach((pill) => {
      pill.addEventListener("click", (e) => {
        e.stopPropagation();
        setPlaybackRate(pill.dataset.speed);
      });
    });

    // volume + slider local ui updates immediately, then notifyvolumesync rewrites every other cards icon + slider so volume is truly global.
    syncVolumeUi(volumeBtn, volumeSlider);
    volumeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      toggleMute();
    });
    volumeSlider.addEventListener("input", () => {
      VolumeState.volume = Math.max(0, Math.min(1, volumeSlider.value / 100));
      // dragging the slider implies "i want to hear sound" -- unmute. dropping to 0 implies mute.
      VolumeState.muted = (VolumeState.volume === 0);
      notifyVolumeSync();
    });

    // the dotted corner resizes every card, and a double click on it takes the choice back
    const resizeGrip = card.querySelector(".card-resize");
    resizeGrip.addEventListener("pointerdown", (e) => startCardResize(e, card));
    resizeGrip.addEventListener("click", (e) => e.stopPropagation());

    syncTheaterButton(preview);
    theaterBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      if (isTheaterPreview(preview)) {
        exitTheaterMode();
        try { theaterBtn.blur(); } catch {}
        return;
      }
      clearHoverPreviewTimer(video);
      if (activePreview && activePreview !== video) abortVideoLoad(activePreview);
      activePreview = video;
      applyVolume(video);
      preview.classList.add("is-playing");
      if (video.readyState >= 2) preview.classList.add("is-video-ready");
      else preview.classList.remove("is-video-ready");
      if (!enterTheaterMode(preview)) return;
      if (card.dataset.previewUrl && video.getAttribute("src") !== card.dataset.previewUrl) {
        preview.classList.remove("is-video-ready");
        video.src = card.dataset.previewUrl;
      }
      const tryPlay = () => video.play().catch(() => {});
      tryPlay();
      video.addEventListener("loadeddata", tryPlay, { once: true });
      try { theaterBtn.blur(); } catch {}
    });

    // fullscreen requestfullscreen() must run synchronously inside the user-gesture event handler. if we wait for play() to resolve before calling it (the obvious shape: "load, play, then go fullscreen"), the gesture is "stale" by the time we get there and the browser silently rejects the request -- which is why the previous version refused to fullscreen until the clip had already started playing. instead: request fullscreen now, kick off loading + play in parallel, and let the video pop into the fullscreened element once its first frame is ready.
    fullscreenBtn.addEventListener("click", (e) => {
      e.stopPropagation();

      // toggle behaviour: if this preview is currently fullscreen, clicking the button exits. matches the user expectation that the same button "owns" both directions of the toggle (same gesture pattern as f, esc, and the back mouse button). we check both the preview and the video element so the toggle still works if someone reverts to fullscreening <video>.
      const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
      if (fsEl === preview || fsEl === video) {
        if (document.exitFullscreen)            document.exitFullscreen().catch(() => {});
        else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
        try { fullscreenBtn.blur(); } catch {}
        return;
      }

      if (isTheaterPreview(preview)) exitTheaterMode();

      clearHoverPreviewTimer(video);
      if (activePreview && activePreview !== video) abortVideoLoad(activePreview);
      activePreview = video;
      applyVolume(video);
      preview.classList.add("is-playing");
      if (video.readyState >= 2) preview.classList.add("is-video-ready");
      else preview.classList.remove("is-video-ready");

      // we fullscreen the .preview container, not the <video> itself. reason: when the <video> element is the fullscreen target, chromium/cefs mediaelementscontroller intercepts dblclick on it as a built-in "toggle fullscreen" shortcut that runs at a lower level than dom events -- preventdefault on dblclick doesnt reliably block it. fullscreening the container shifts the dblclick to a plain <div>, so our dom-level preventdefault below actualy works and the overlay controls (volume slider, close button) stay reachable while fullscreened.
      if (preview.requestFullscreen)            preview.requestFullscreen().catch(() => {});
      else if (preview.webkitRequestFullscreen) preview.webkitRequestFullscreen();
      // drop focus from the button so space/enter dont re-activate it (which would toggle fullscreen back off). play/pause is handled by the document-level space handler.
      try { fullscreenBtn.blur(); } catch {}

      if (card.dataset.previewUrl && video.getAttribute("src") !== card.dataset.previewUrl) {
        preview.classList.remove("is-video-ready");
        video.src = card.dataset.previewUrl;
      }
      const tryPlay = () => video.play().catch(() => {});
      tryPlay();
      // backup: if the synchronous play() rejected becuase the source wasnt loaded yet, retry once enough data has arrived. safe to also fire if play already succeeded -- a second play() on a playing video is a no-op.
      video.addEventListener("loadeddata", tryPlay, { once: true });
    });

    // rename (pencil)
    editBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      startRename(card);
    });
    favoriteMenuItem.addEventListener("click", (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      favoriteBtn.click();
    });
    selectMenuItem.addEventListener("click", (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      selectBtn.click();
    });
    renameMenuItem.addEventListener("click", (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      editBtn.click();
    });
    linkMenuItem.addEventListener("click", (e) => {
      e.stopPropagation();
      const actionButton = card.querySelector(".actions .action-link-btn");
      if (!actionButton || actionButton.disabled) return;
      closeCardMenu(menuDropdown);
      actionButton.click();
    });

    // the same menu is available from the visible 3-dot button or a right-click anywhere on the clip
    function openCardMenuAtPointer(e) {
      openCardMenuAt(card, menuDropdown, e);
    }
    menuBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      openCardMenuAtPointer(e);
    });
    card.addEventListener("contextmenu", (e) => {
      if (e.target.closest && e.target.closest("input, .volume-slider-wrap")) return;
      e.preventDefault();
      e.stopPropagation();
      openCardMenuAtPointer(e);
    });
    openFolderEl.addEventListener("click", async (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      try {
        await fetch(HELPER + "/open-folder?file=" + encodeURIComponent(card.dataset.name), {
          method: "POST", cache: "no-store",
        });
      } catch {}
    });
    // both delete entry points use the same two-click confirmation. The menu keeps its first-click state visible, while the card button uses its compact inline confirmation.
    function handleDeleteAction(e, trigger) {
      e.preventDefault();
      e.stopPropagation();
      const inMenu = trigger === deleteMenuItem;
      resetAllArmedDeleteButtons(trigger);
      if (!trigger.classList.contains("is-armed")) {
        if (!inMenu) closeAllMenus();
        armDeleteButton(trigger);
        if (inMenu) {
          menuDropdown.classList.add("is-open");
          card.classList.add("menu-open");
        }
        return;
      }
      resetDeleteButton(trigger);
      closeCardMenu(menuDropdown);
      // on the drafts tab the trash of the card lets go of the draft and leaves the clip; the red Delete of the menu is still the clip
      if (trigger === deleteClipEl && card.classList.contains("is-draft-card")) {
        const draftName = card.dataset.name;
        if (draftName) {
          forgetDraft(draftName);
          showToast("Draft deleted", "info");
          scheduleToastHide(2400);
        }
        return;
      }
      deleteClip(card);
    }
    deleteMenuItem.addEventListener("click", (e) => handleDeleteAction(e, deleteMenuItem));
    deleteClipEl.addEventListener("click", (e) => handleDeleteAction(e, deleteClipEl));
    card.querySelector(".trim-clip-item").addEventListener("click", (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      requestOpenEditor(currentClipForCard(card));
    });
    card.querySelector(".discard-draft-item").addEventListener("click", (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      const name = card.dataset.name;
      if (!name) return;
      forgetDraft(name);
      showToast("Draft deleted", "info");
      scheduleToastHide(2400);
    });
    card.querySelector(".duplicate-menu-item").addEventListener("click", async (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      const name = card.dataset.name;
      if (!name) return;
      try {
        const res = await fetch(HELPER + "/duplicate-clip?file=" + encodeURIComponent(name) + "&t=" + Date.now(),
                                { method: "POST", cache: "no-store" });
        const data = await res.json().catch(() => ({}));
        if (!res.ok || !data.ok) throw new Error(data.message || ("HTTP " + res.status));
        // force a fresh, non-merged clips fetch so the copy shows up on the next tick.
        clipsPageVersion = "";
        scheduleNextPoll(0);
        showToast("Duplicated " + escapeHtml(displayClipName(name)), "info");
        scheduleToastHide(2200);
      } catch (err) {
        showToast("Duplicate failed: " + escapeHtml(err.message || err));
        scheduleToastHide(TOAST_ERROR_MS);
      }
    });
    card.querySelector(".clipboard-clip-item").addEventListener("click", async (e) => {
      e.stopPropagation();
      closeCardMenu(menuDropdown);
      const currentClip = currentClipForCard(card);
      if (!currentClip) return;
      // clipboard puts the file on the windows clipboard as cf_hdrop, same as ctrl+c on the file in explorer. the user can then paste into discord / slack / explorer / etc.
      try {
        const res = await fetch(HELPER + "/clipboard?file=" + encodeURIComponent(currentClip.name) + "&t=" + Date.now(),
                                { method: "POST", cache: "no-store" });
        const data = await res.json().catch(() => ({}));
        if (!res.ok || !data.ok) throw new Error(data.message || ("HTTP " + res.status));
        showToast("Copied to clipboard - paste anywhere to share the file", "info");
        scheduleToastHide(3000);
      } catch (err) {
        showToast("Clipboard copy failed: " + (err.message || err));
        scheduleToastHide(TOAST_ERROR_MS);
      }
    });
    // single "compress..." menu item opens the mode picker. the picker hands the chosen mode to startcompressoverwrite(), which is also reused by callers elsewhere (e.g. if we ever want to re-bind a keyboard shortcut to "compress fast"). keeping the fetch in one function avoids drift between menu vs. modal.
    card.querySelectorAll(".compress-clip-item").forEach((item) => {
      item.addEventListener("click", (e) => {
        e.stopPropagation();
        closeCardMenu(menuDropdown);
        const currentClip = currentClipForCard(card);
        if (currentClip) openCompressModePicker(currentClip.name);
      });
    });

    observeThumb(card.querySelector(".thumb"));
    renderActions(card, clip, true);
    syncDraftUi(card, clip.name);
    return card;
  }

  async function deleteClip(card) {
    const filename = card.dataset.name;
    if (!filename) return;
    // confirmation now happens on the trash icon (the two-click pattern wired up in rendercard). by the time this runs the user has explicitly clicked delete twice, so a redundant browser confirm() would be condescending.
    try {
      const res = await fetch(HELPER + "/delete?file=" + encodeURIComponent(filename), {
        method: "POST", cache: "no-store",
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || !data.ok) {
        throw new Error(data.message || ("HTTP " + res.status));
      }
      // the clip just got deleted out from under whatever enlarged view is showing it -- drop back to the grid instead of leaving theater/fullscreen locked onto a card thats about to be torn out by renderLoadedClips below.
      const preview = card.querySelector(".preview");
      if (preview && isTheaterPreview(preview)) exitTheaterMode();
      if (preview && (document.fullscreenElement === preview || document.webkitFullscreenElement === preview)) tryExitFullscreen();
      const video = card.querySelector("video");
      if (video) {
        if (activePreview === video) activePreview = null;
        abortVideoLoad(video);
      }
      localState.delete(filename);
      selectedClipNames.delete(filename);
      setFavorite(filename, false);
      lastClipsByName.delete(filename);
      loadedClips = loadedClips.filter((clip) => clip.name !== filename);
      clipsTotal = Math.max(0, clipsTotal - 1);
      clipsNextOffset = Math.min(clipsNextOffset, clipsTotal);
      renderLoadedClips(loadedClips);
      refreshBulkSelectionUi();
      showToast("Deleted " + escapeHtml(displayClipName(filename)), "info");
      scheduleToastHide(1800);
      scheduleNextPoll(250);
    } catch (e) {
      showToast("Delete failed: " + escapeHtml(e.message || e));
      scheduleToastHide(TOAST_ERROR_MS);
    }
  }

  // inline rename replace the .name span with an input, commit on enter / blur, revert on escape. server is asked via post /rename; on success the next poll picks up the new card identity, on failure we restore.
  function isClipRenameActive() {
    const input = document.querySelector(".name-edit-input");
    return !!(input && document.body.contains(input));
  }

  function flushRenderAfterRename() {
    if (!pendingLoadedRenderAfterRename || isClipRenameActive()) return;
    pendingLoadedRenderAfterRename = false;
    renderLoadedClips(loadedClips);
  }

  function startRename(card) {
    const currentName = card.dataset.name;
    const titleRow = card.querySelector(".title-row");
    const nameSpan = card.querySelector(".name");
    if (!nameSpan) return;
    // the pencil + the trailing action cluster get shoved sideways when the name swaps to a full-width input; FLIP them so they glide instead of jumping.
    const flipTargets = [card.querySelector(".edit-btn"), card.querySelector(".title-actions")];

    // extension lives in its own static chip, not the input -- theres nothing to select past the basename, so it cant be typed over or dragged away by accident.
    const dot = currentName.lastIndexOf(".");
    const hasExt = dot > 0;
    const baseName = hasExt ? currentName.slice(0, dot) : currentName;
    const ext = hasExt ? currentName.slice(dot) : "";

    const input = document.createElement("input");
    input.type = "text";
    input.className = "name-edit-input";
    input.value = baseName;

    let extSpan = null;
    if (hasExt) {
      extSpan = document.createElement("span");
      extSpan.className = "name-edit-ext";
      extSpan.textContent = ext;
    }

    rkFlip(flipTargets, () => {
      titleRow.replaceChild(input, nameSpan);
      if (extSpan) titleRow.insertBefore(extSpan, input.nextSibling);
    });
    try { input.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 150, easing: "ease-out" }); } catch (_) {}
    input.focus();
    input.select();

    let settled = false;
    function swapBackToNameSpan() {
      rkFlip(flipTargets, () => {
        if (input.parentNode) input.parentNode.replaceChild(nameSpan, input);
        else titleRow.insertBefore(nameSpan, titleRow.firstChild);
        if (extSpan && extSpan.parentNode) extSpan.parentNode.removeChild(extSpan);
      });
    }
    function restore() {
      if (settled) return;
      settled = true;
      swapBackToNameSpan();
      flushRenderAfterRename();
    }
    async function commit() {
      if (settled) return;
      const newBase = input.value.trim();
      const newName = newBase + ext;
      if (!newBase || newName === currentName) { restore(); return; }
      settled = true;
      try {
        const res = await fetch(HELPER + "/rename"
          + "?file=" + encodeURIComponent(currentName)
          + "&to="   + encodeURIComponent(newName),
          { method: "POST", cache: "no-store" });
        const data = await res.json().catch(() => ({}));
        if (!res.ok || !data.ok) {
          throw new Error(data.message || ("HTTP " + res.status));
        }
        // optimistically update the card so we dont wait for the next /clips poll to relabel. the next poll will reconcile.
        const finalName = data.name || newName;
        transferFavorite(currentName, finalName);
        card.dataset.name = finalName;
        nameSpan.textContent = displayClipName(card.dataset.name);
        nameSpan.title       = displayClipName(card.dataset.name);
        syncFavoriteButton(card, card.dataset.name);
        swapBackToNameSpan();
        flushRenderAfterRename();
        scheduleNextPoll(500);
      } catch (e) {
        swapBackToNameSpan();
        showToast("Rename failed: " + (e.message || e));
        scheduleToastHide(TOAST_ERROR_MS);
        flushRenderAfterRename();
      }
    }
    input.addEventListener("blur", commit);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter")  { e.preventDefault(); input.blur(); }
      if (e.key === "Escape") { e.preventDefault(); input.removeEventListener("blur", commit); restore(); }
    });
  }

  // close any open 3-dot menu. three triggers, becuase no single one catches every "the user is interacting with something else now" case: mousedown on the document: in-page clicks. inspects e.target with .closest() directly so it doesnt matter if some handler in the click path calls stoppropagation. mouseleave on <html>: cursor left the viewport -- this is the one that catches a click on the os window title bar, becuase the cursor has to cross the document edge to get up there and the title bar doesnt dispatch any dom event to the page. window blur: the popup lost focus (alt-tab, another window clicked). belt-and-braces backup for the title-bar case on browsers where mouseleave doesnt fire reliably.
  // opens the menu of a card, a clip or a screenshot, with its top-left corner at the pointer, clamped to the visible scroll area so every item stays reachable
  function openCardMenuAt(card, menuDropdown, e) {
    if (window.RKTooltip) RKTooltip.hide();
    resetAllArmedDeleteButtons();
    document.querySelectorAll(".menu-dropdown.is-open").forEach((d) => {
      if (d !== menuDropdown) {
        closeCardMenu(d);
      }
    });
    const rect = card.getBoundingClientRect();
    // the menu is painted inside .scroll-area, which clips at its own edge minus the scrollbar and starts below the toolbar -- clamping to the window instead let the menu slide under that edge, slicing off its border and rounded corners on the side it pressed against
    const area = card.closest(".scroll-area");
    const bounds = area && area.clientWidth > 0 ? area.getBoundingClientRect() : null;
    const boundsLeft = Math.max(0, bounds ? bounds.left : 0);
    const boundsTop = Math.max(0, bounds ? bounds.top : 0);
    const boundsRight = Math.min(window.innerWidth, bounds ? bounds.left + area.clientWidth : window.innerWidth);
    const boundsBottom = Math.min(window.innerHeight, bounds ? bounds.top + area.clientHeight : window.innerHeight);
    const edge = 8;
    // cleared first so a cap from an earlier open does not shrink the measurement below the natural height; a window too short for the whole menu gets a capped height with its own scrollbar instead of a hard cut
    menuDropdown.style.maxHeight = "";
    menuDropdown.style.overflowY = "";
    const width = menuDropdown.offsetWidth || 190;
    const room = boundsBottom - boundsTop - edge * 2;
    let height = menuDropdown.offsetHeight || 280;
    if (height > room && room > 0) {
      height = room;
      menuDropdown.style.maxHeight = room + "px";
      menuDropdown.style.overflowY = "auto";
    }
    // the menu is positioned against the cards padding box, so the 1px card border has to come off the pointer offset
    const originX = rect.left + card.clientLeft;
    const originY = rect.top + card.clientTop;
    const minLeft = boundsLeft + edge - originX;
    const maxLeft = boundsRight - width - edge - originX;
    const minTop = boundsTop + edge - originY;
    const maxTop = boundsBottom - height - edge - originY;
    const left = Math.max(minLeft, Math.min(maxLeft, e.clientX - originX));
    const top = Math.max(minTop, Math.min(maxTop, e.clientY - originY));
    menuDropdown.style.setProperty("--menu-left", left + "px");
    menuDropdown.style.setProperty("--menu-top", top + "px");
    menuDropdown.classList.add("is-open");
    card.classList.add("menu-open");
  }

  function closeCardMenu(menu) {
    if (!menu) return;
    menu.classList.remove("is-open");
    const card = menu.closest(".card, .screenshot-card");
    if (!card) return;
    if (menu._closeTimer) clearTimeout(menu._closeTimer);
    menu._closeTimer = setTimeout(() => {
      if (!menu.classList.contains("is-open")) card.classList.remove("menu-open");
    }, 180);
  }

  function closeAllMenus() {
    document.querySelectorAll(".menu-dropdown.is-open").forEach((d) => {
      closeCardMenu(d);
    });
  }
  document.addEventListener("mousedown", (e) => {
    if (e.target.closest(".menu-btn, .menu-dropdown, .delete-btn")) return;
    closeAllMenus();
    resetAllArmedDeleteButtons();
  });
  document.documentElement.addEventListener("mouseleave", () => {
    closeAllMenus();
    resetAllArmedDeleteButtons();
  });
  window.addEventListener("blur", () => {
    closeAllMenus();
    resetAllArmedDeleteButtons();
  });

  // defensive: explicitly exit fullscreen on esc. browsers normally handle this themselves at the chrome level, but on some chromium builds the built-in gesture can get lost (especially in popup windows opened via window.open). this makes esc work unconditionally as long as the keydown reaches the document. also: pressing space on the fullscreen <video> should play/pause -- without this handler the focused fullscreen button receives the space activation and toggles fullscreen off instead. try to exit the current fullscreen element. returns true if we were in fullscreen and an exit was requested, false otherwise. centralised here becuase three diffrent inputs trigger the same action (esc key, f key while fullscreen, back mouse button).
  function tryExitFullscreen() {
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
    if (!fsEl) return false;
    if (document.exitFullscreen)            document.exitFullscreen().catch(() => {});
    else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
    return true;
  }

  // find the .preview container to fullscreen when the user hits f outside fullscreen. priority: the card whose video is currently the activepreview (auto-set whenever a video starts playing on hover or click), falling back to the card under the cursor. the cursor-based fallback handles the case where nothing is playing yet -- hovering over a card and pressing f still works.
  let _lastHoveredCard = null;
  document.addEventListener("mouseover", (e) => {
    const card = e.target && e.target.closest && e.target.closest(".card");
    if (card) _lastHoveredCard = card;
  });

  function previewToFullscreen() {
    let preview = null;
    if (activePreview) {
      preview = activePreview.closest && activePreview.closest(".preview");
    }
    if (!preview && _lastHoveredCard) {
      preview = _lastHoveredCard.querySelector(".preview");
    }
    if (!preview) return false;
    const pendingCard = preview.closest && preview.closest(".card");
    if (pendingCard && pendingCard.classList.contains("is-processing")) return false;
    if (isTheaterPreview(preview)) exitTheaterMode();
    if (preview.requestFullscreen)            preview.requestFullscreen().catch(() => {});
    else if (preview.webkitRequestFullscreen) preview.webkitRequestFullscreen();
    const card = preview.closest && preview.closest(".card");
    const video = preview.querySelector && preview.querySelector("video");
    if (card && video) {
      clearHoverPreviewTimer(video);
      if (activePreview && activePreview !== video) abortVideoLoad(activePreview);
      activePreview = video;
      applyVolume(video);
      preview.classList.add("is-playing");
      if (video.readyState >= 2) preview.classList.add("is-video-ready");
      else preview.classList.remove("is-video-ready");
      if (card.dataset.previewUrl && video.getAttribute("src") !== card.dataset.previewUrl) {
        preview.classList.remove("is-video-ready");
        video.src = card.dataset.previewUrl;
      }
      const tryPlay = () => video.play().catch(() => {});
      tryPlay();
      video.addEventListener("loadeddata", tryPlay, { once: true });
    }
    return true;
  }

  // in the editor f and m belong to its video, so they only act while the pointer is over it or something in it has the focus
  function trimVideoEngaged() {
    const wrap = document.querySelector("#trim-veil .trim-video-wrap");
    return !!wrap && (wrap.matches(":hover") || wrap.contains(document.activeElement));
  }

  // true while a field takes the letters, so f, t and m still type in a rename box or the search field
  function typingInField() {
    const tag = (document.activeElement && document.activeElement.tagName) || "";
    return tag === "INPUT" || tag === "TEXTAREA" || !!(document.activeElement && document.activeElement.isContentEditable);
  }

  document.addEventListener("keydown", (e) => {
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;

    if (e.key === "Escape" && !fsEl && exitTheaterMode()) {
      e.preventDefault();
      return;
    }

    // f = toggle fullscreen, whether or not were currently in it. outside fullscreen we promote the active / hovered card. inside we just exit. skip when focus is in an editable surface so the letter f still types in inline rename fields / search boxes. a held key is ignored after its first press, since it repeats the keydown and would flip fullscreen again and again.
    if ((e.key === "f" || e.key === "F") && !e.ctrlKey && !e.altKey && !e.metaKey) {
      if (typingInField()) return;
      const inEditor = trimVeil.classList.contains("is-open");
      if (inEditor && !fsEl && !trimVideoEngaged()) return;
      e.preventDefault();
      if (e.repeat) return;
      if (fsEl) tryExitFullscreen();
      else if (inEditor) document.getElementById("trim-fullscreen").click();
      else previewToFullscreen();
      return;
    }

    // t = theater mode on the clip under the pointer, and off again from anywhere while one is in theater mode. the same guards as f: nothing while a field takes the letters, and nothing over the editor, which sits on top of the grid; a held key is ignored after its first press, the same way as for f
    if ((e.key === "t" || e.key === "T") && !e.ctrlKey && !e.altKey && !e.metaKey && !fsEl) {
      if (typingInField() || e.repeat || trimVeil.classList.contains("is-open") || trimProcessingVeil.classList.contains("is-open")) return;
      const card = theaterPreview ? theaterPreview.closest(".card") : document.querySelector(".card:hover");
      const button = card && !card.classList.contains("is-processing") && card.querySelector(".theater-btn");
      if (button) {
        e.preventDefault();
        button.click();
      }
      return;
    }

    // m = mute and unmute, the switch of the volume buttons. only with a clip in front of the pointer, one in fullscreen or in theater mode, the editor, or a card under the pointer, so the key never changes the sound of a clip that is not there; a held key does not flip it again and again
    if ((e.key === "m" || e.key === "M") && !e.ctrlKey && !e.altKey && !e.metaKey) {
      if (typingInField() || e.repeat) return;
      if (!fsEl && !theaterPreview && !(trimVeil.classList.contains("is-open") && trimVideoEngaged()) && !document.querySelector(".card:hover")) return;
      e.preventDefault();
      toggleMute();
      return;
    }

    if (!fsEl) return;

    if (e.key === "Escape") {
      tryExitFullscreen();
      return;
    }
    if (e.key === " " || e.code === "Space") {
      // space = toggle play/pause. prefer routing thru the cards own _toggleplayback (buildcard attaches it) so the same center-pulse animation fires for space as for click + button. falls back to a direct play/pause for any odd edge case where the card couldnt be located (e.g. an external <video> element ever ends up fullscreened directly).
      e.preventDefault();
      const fsCard = fsEl.closest && fsEl.closest(".card");
      if (fsCard && typeof fsCard._togglePlayback === "function") {
        fsCard._togglePlayback();
        return;
      }
      let v = null;
      if (fsEl instanceof HTMLVideoElement) v = fsEl;
      else if (fsEl.querySelector)          v = fsEl.querySelector("video");
      if (!v) return;
      if (v.paused) v.play().catch(() => {});
      else          v.pause();
      return;
    }

    if ((e.key === "ArrowLeft" || e.key === "ArrowRight") &&
        !e.ctrlKey && !e.altKey && !e.metaKey) {
      const fsCard = fsEl.closest && fsEl.closest(".card");
      let v = fsCard && fsCard.querySelector ? fsCard.querySelector("video") : null;
      if (!v) {
        if (fsEl instanceof HTMLVideoElement) v = fsEl;
        else if (fsEl.querySelector)          v = fsEl.querySelector("video");
      }
      if (!v || !isFinite(v.duration) || v.duration <= 0) return;
      e.preventDefault();
      const delta = e.key === "ArrowRight" ? VIDEO_SEEK_STEP_SECONDS : -VIDEO_SEEK_STEP_SECONDS;
      v.currentTime = Math.max(0, Math.min(v.duration, v.currentTime + delta));
    }
  });

  // back mouse button (xbutton1, button === 3) exits fullscreen. "back" reads as "go back to the grid" in the users mental model. forward button (xbutton2 = 4) is left alone since theres no natural "forward" action from within fullscreen. mouseup is used instead of mousedown so the gesture matches windows/chromiums back-button convention (fires on release).
  document.addEventListener("mouseup", (e) => {
    if (e.button !== 3) return;
    if (tryExitFullscreen()) {
      e.preventDefault();
      e.stopPropagation();
    }
  });
  // the browser also fires `mousedown` on the back button which it would normally interpret as "navigate back" -- swallow that separately so it doesnt yank the dock out from under us.
  document.addEventListener("mousedown", (e) => {
    if (e.button === 3 &&
        (document.fullscreenElement || document.webkitFullscreenElement)) {
      e.preventDefault();
      e.stopPropagation();
    }
  });

  // fullscreen is owned by CEFs browser fullscreen API. do not maximize the native Clips popup here: a maximized host keeps the custom title bar/taskbar and is not a real player fullscreen.
  let lastFsActive = false;
  function handleHostFullscreenChange() {
    const fsActive = !!(document.fullscreenElement || document.webkitFullscreenElement);
    if (fsActive === lastFsActive) return;
    lastFsActive = fsActive;
    updateFullscreenBodyState();
    // keep the host-transition guard raised from enter all the way through the exit resize so renders + the scroll poller stay suppressed the whole time the native window is a non-standard size. document.fullscreenElement covers the in-fullscreen stretch; the guard flag covers the gap after fullscreen ends but before the window has shrunk back. on entry, stash the real scroll offset first -- the window is still its normal size here, the last chance to read it before chromium clamps it once the window grows.
    if (fsActive) fullscreenSavedScrollTop = scrollAreaEl.scrollTop;
    const seq = beginFullscreenHostTransition();
    // CEFs Fullscreen API only expands inside its popup. mirror YouTubes element fullscreen with a borderless monitor-sized host so the player actually occupies the selected screen instead of a maximized framed window.
    const route = fsActive ? "/window/fullscreen" : "/window/fullscreen/restore";
    const fetchDone = fetch(HELPER + route + "?title=Clips" + (window.RKEmbedded ? "&embedded=1" : "") + "&t=" + Date.now(), { method: "POST", cache: "no-store" }).catch(() => {});
    // only settle the guard on the way out -- on the way in we want it to stay up until the matching exit fires. exit gets the scroll restore.
    if (!fsActive) fetchDone.finally(() => finishFullscreenHostTransition(seq, true));
  }
  document.addEventListener("fullscreenchange", handleHostFullscreenChange);
  document.addEventListener("webkitfullscreenchange", handleHostFullscreenChange);

  function updateDisplayProgress(name, fill, reportedPercent, smoothTime) {
    const reported = Math.max(0, Math.min(100, Number(reportedPercent) || 0));
    let v = progressVisuals.get(name);
    if (!v) {
      v = { displayed: 0, target: 0, vel: 0, fill: null, raf: 0, lastT: 0 };
      progressVisuals.set(name, v);
    }
    // monotonic: the fill only ever moves toward the newest real percentage, never back
    if (reported > v.target) v.target = reported;
    v.fill = fill;
    fill.style.width = v.displayed.toFixed(3) + "%";
    if (v.raf) return;
    v.lastT = 0;
    // critically-damped follow (unity-style SmoothDamp): the fill carries a velocity, so a coarse jump in the reported percent bends its path instead of restarting an ease -- kills the stop-and-go at each reported point. st ~= how long to close most of a gap: 0.6 for the ~1/s upload meter, larger for the transcode bar whose samples land ~5s apart (so it glides the whole gap instead of snapping then sitting).
    const st = (typeof smoothTime === "number" && smoothTime > 0) ? smoothTime : 0.6;
    const step = (now) => {
      if (progressVisuals.get(name) !== v || !v.fill) { v.raf = 0; return; }
      const dt = v.lastT ? Math.min(0.05, (now - v.lastT) / 1000) : 0.016;
      v.lastT = now;
      const omega = 2 / st;
      const x = omega * dt;
      const expf = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
      const change = v.displayed - v.target;
      const temp = (v.vel + omega * change) * dt;
      v.vel = (v.vel - omega * temp) * expf;
      let out = v.target + (change + temp) * expf;
      if (out > v.target) { out = v.target; v.vel = 0; }
      v.displayed = out;
      v.fill.style.width = v.displayed.toFixed(3) + "%";
      if (v.target - v.displayed > 0.08 || v.vel > 0.4) {
        v.raf = requestAnimationFrame(step);
      } else {
        v.displayed = v.target;
        v.fill.style.width = v.displayed.toFixed(3) + "%";
        v.raf = 0;
      }
    };
    v.raf = requestAnimationFrame(step);
  }

  // the helper holds percent at 99 and moves phase to "finalizing"/"copying link" once the byte transfer is done -- the quick transcode-trigger call runs silently under a faint sheen, no jump to 100 and no text.
  function uploadPhaseIsFinalizing(job) {
    const p = job && job.phase ? job.phase : "";
    return p === "finalizing" || p === "copying link";
  }

  function renderActions(card, clip, force) {
    const actions = card.querySelector(".actions");
    const meta = card.querySelector(".meta");
    const status = lastUploadJobsByName.get(clip.name)
      || (lastUploadStatus && lastUploadStatus.clipName === clip.name ? lastUploadStatus : null);
    // within the cancel grace window keep the card idle even if a stale status snapshot still reports the job active.
    const cancelPending = cancelledUploads.has(clip.name);
    const state = !cancelPending && status && status.active && (status.state === "compressing" || status.state === "uploading")
      ? status.state
      : (localState.get(clip.name) || "idle");
    const uploadPct = status && Number(status.percent) ? Number(status.percent) : 0;
    // ready/transcode_percent/transcode_status flow from the background poller that runs after each upload. older entries omit them, in which case the link is always shown as ready.
    const uploadedAt = Number(clip.uploaded_at) || 0;
    const processingAge = uploadedAt > 0
      ? Math.max(0, Math.floor(Date.now() / 1000) - uploadedAt)
      : 0;
    const failed     = clip.transcode_status === 3;
    const timedOut   = clip.transcode_status === 4 ||
      (clip.ready === false && uploadedAt > 0 && processingAge >= STREAMABLE_PROCESSING_TIMEOUT_SEC);
    const processing = clip.ready === false && !failed && !timedOut;
    const transPct   = Number(clip.transcode_percent) || 0;
    const stillTicking = (state === "compressing" || state === "uploading");
    const queuedCompression = state === "compressing" && status && status.phase === "queued";
    const finalizing = stillTicking && uploadPhaseIsFinalizing(status);
    // amber transcode bar while streamable processes; the link is only offered once it is playable (ready, or our poll timed out and we give it the benefit of the doubt). a failed transcode offers no link.
    const showTranscodeBar = !!clip.streamable_url && processing && !stillTicking;
    const showCopy = !!clip.streamable_url && !processing && !failed;
    // in the drafts tab a card that is not busy offers the editor, whatever it has of a link
    const draftOpen = activeFilter === "drafts" && hasDraft(clip.name) && !stillTicking;
    card.classList.toggle("is-draft-card", draftOpen);
    const actionSig = [
      draftOpen ? 1 : 0,
      state,
      clip.streamable_url || "",
      clip.uploaded_at || 0,
      clip.retention_days || 0,
      uploadPct,
      (status && status.phase) || "",
      processing ? 1 : 0,
      failed ? 1 : 0,
      timedOut ? 1 : 0,
      transPct,
    ].join("|");
    // while an upload/compress is running and its bar already exists, nudge the fill in place -- rebuilding actions.innerHTML every poll (below) tears down the cancel X, dropping its :hover state and re-binding its listener each tick so it flashes under the pointer.
    if (stillTicking) {
      const bar = actions.querySelector(".btn.is-progress");
      if (bar) {
        card.dataset.actionSig = actionSig;
        bar.classList.toggle("is-finalizing", finalizing);
        bar.classList.toggle("is-queued", !!queuedCompression);
        const phaseLabel = bar.querySelector(".progress-phase");
        if (phaseLabel) phaseLabel.textContent = queuedCompression ? "Queued" : "";
        updateDisplayProgress(clip.name, bar.querySelector(".progress-fill"), uploadPct);
        return;
      }
    }
    // same fast path for the amber transcode bar -- nudge the fill in place instead of rebuilding actions each poll.
    if (showTranscodeBar && !draftOpen) {
      const tbar = actions.querySelector(".btn.is-progress.is-transcode");
      if (tbar) {
        card.dataset.actionSig = actionSig;
        // ~4.5s smoothing -- streamables percent lands roughly every 5s, so the bar glides the whole gap instead of snapping in 0.6s then stalling.
        updateDisplayProgress(clip.name, tbar.querySelector(".progress-fill"), transPct, 4.5);
        return;
      }
    }
    if (!force && card.dataset.actionSig === actionSig) return;
    card.dataset.actionSig = actionSig;
    meta.querySelectorAll(".link-status, .link-status-dot").forEach(n => n.remove());
    actions.innerHTML = "";
    actions.classList.toggle("is-progress", state === "compressing" || state === "uploading" || (showTranscodeBar && !draftOpen));
    // cleared here (before the transcode bar is (re)built below) so a fresh amber bar smooth-damps up from 0 instead of inheriting the upload bar target of 100. the poll-to-poll nudge goes through the fast path above, which returns before this line.
    if (state !== "compressing" && state !== "uploading") progressVisuals.delete(clip.name);
    const linkMenuItem = card.querySelector(".link-menu-item");
    if (linkMenuItem) {
      const label = linkMenuItem.querySelector(".menu-item-label");
      const icon = linkMenuItem.querySelector(".menu-item-icon");
      const busy = !clip.streamable_url && stillTicking;
      const linkLocked = !!clip.streamable_url && (processing || failed);
      // coerce to a real boolean -- CEF treats classList.toggle(cls, undefined) as a plain toggle, so `false || undefined` was flipping is-disabled ON for every eligible (never-linked) clip.
      const menuBlockReason = showCopy ? null : uploadBlockReason(clip);
      linkMenuItem.classList.toggle("is-disabled", !!(busy || linkLocked || menuBlockReason));
      const linkText = showCopy ? "Copy Link"
        : menuBlockReason ? menuBlockReason + " -- Streamable will not accept this clip"
        : processing ? "Processing on Streamable"
        : failed ? "Processing failed"
        : busy ? "Creating link" : "Create Link";
      linkMenuItem.setAttribute("aria-label", linkText);
      // the full reason stays in the aria label -- putting it in the visible label too is what was ballooning the menus height when it wrapped across lines
      if (label) label.textContent = menuBlockReason ? "Create Link" : linkText;
      if (icon) icon.innerHTML = showCopy ? COPY_ICON : ICON.LINK;
    }

    const addMetaStatus = (text, expiring) => {
      const dot = document.createElement("span");
      dot.className = "dot link-status-dot";
      const span = document.createElement("span");
      span.className = "link-status" + (expiring ? " is-expiring" : "");
      span.textContent = text;
      span.title = text;
      meta.appendChild(dot);
      meta.appendChild(span);
    };

    if (draftOpen) {
      // the drafts tab: what a card offers is picking the edit up again, in place of the link
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "btn action-link-btn draft-open-btn";
      btn.dataset.tooltip = "Pick up your unsaved edits";
      btn.innerHTML = `<svg class="btn-icon" viewBox="0 0 24 24">${ICON.OPEN_EDITOR}</svg><span class="btn-label">Open in editor</span>`;
      btn.addEventListener("click", () => {
        const host = btn.closest(".card");
        requestOpenEditor(host ? currentClipForCard(host) : clip);
      });
      actions.appendChild(btn);
      const dot = document.createElement("span");
      dot.className = "dot link-status-dot";
      const note = document.createElement("span");
      note.className = "link-status is-draft";
      note.textContent = "Unsaved edits";
      meta.appendChild(dot);
      meta.appendChild(note);
    } else if (showTranscodeBar) {
      // streamable transcodes server-side after the upload; the link is not offered until it is playable. amber bar tracks transcode_percent.
      const bar = document.createElement("div");
      bar.className = "btn is-progress is-transcode";
      bar.title = "Processing on Streamable";
      bar.innerHTML = '<span class="progress-track"><span class="progress-fill"></span></span>';
      actions.appendChild(bar);
      updateDisplayProgress(clip.name, bar.querySelector(".progress-fill"), transPct, 4.5);
    } else if (showCopy) {
      const btn = document.createElement("button");
      btn.className = "btn action-link-btn";
      btn.innerHTML = `<svg class="btn-icon" viewBox="0 0 24 24">${COPY_ICON}</svg><span class="btn-label">Copy Link</span>`;
      btn.dataset.url = clip.streamable_url;
      btn.addEventListener("click", async () => {
        const ok = await copyText(btn.dataset.url);
        if (!ok) return;
        const label = btn.querySelector(".btn-label");
        const orig  = label.textContent;
        btn.classList.add("is-flashing");
        label.textContent = "Link Copied";
        setTimeout(() => {
          btn.classList.remove("is-flashing");
          label.textContent = orig;
        }, 1100);
      });
      actions.appendChild(btn);

      if (timedOut) {
        addMetaStatus("Status check timed out", true);
      } else {
        const expiry = formatExpiry(clip.uploaded_at, clip.retention_days);
        if (expiry) {
          // "expiring soon" highlight when <6 h remaining for short retentions or <24 h for longer ones.
          const retSec = (clip.retention_days || 2) * 86400;
          const remaining = retSec - (Math.floor(Date.now() / 1000) - clip.uploaded_at);
          const threshold = retSec > 7 * 86400 ? 24 * 3600 : 6 * 3600;
          addMetaStatus("Link " + expiry + " left", remaining < threshold);
        }
      }
    } else if (state === "compressing" || state === "uploading") {
      // only built when the bar isnt already there (fresh card or a state flip into uploading) -- the fast path above keeps an existing bar alive across polls so the cancel X doesnt flash. the trailing X is the only cancel target, so aiming at the bar no longer turns the whole thing red.
      let btn = actions.querySelector(".btn.is-progress");
      if (!btn) {
        actions.innerHTML = "";
        btn = document.createElement("div");
        btn.className = "btn is-progress";
        btn.innerHTML =
          '<span class="progress-track"><span class="progress-fill"></span></span><span class="progress-phase"></span>' +
          '<button type="button" class="progress-cancel" aria-label="Cancel" title="Cancel">' +
          '<svg viewBox="0 0 320.591 320.591" aria-hidden="true">' + CANCEL_ICON + '</svg></button>';
        // resolve the clip + its live job at click time from the card -- a recycled card node can outlive the clip this listener was built for.
        const xBtn = btn.querySelector(".progress-cancel");
        xBtn.addEventListener("click", () => {
          const host = btn.closest(".card");
          const name = (host && host.dataset.name) || "";
          if (!name) return;
          xBtn.disabled = true;
          bulkUploadQueued.delete(name);
          const job = lastUploadJobsByName.get(name);
          cancelActiveUpload(name, (job && job.requestId) || "");
        });
        actions.appendChild(btn);
      }
      btn.classList.toggle("is-finalizing", finalizing);
      btn.classList.toggle("is-queued", !!queuedCompression);
      btn.querySelector(".progress-phase").textContent = queuedCompression ? "Queued" : "";
      updateDisplayProgress(clip.name, btn.querySelector(".progress-fill"), uploadPct);
    } else {
      const btn = document.createElement("button");
      btn.className = "btn action-link-btn";
      btn.innerHTML = `<svg class="btn-icon" viewBox="0 0 24 24">${ICON.LINK}</svg><span class="btn-label">Create Link</span>`;
      // Streamable would reject this one anyway -- lock the button and say why instead of letting the upload run and fail.
      const blockReason = uploadBlockReason(clip);
      if (blockReason) {
        btn.disabled = true;
        btn.classList.add("is-blocked");
        btn.title = blockReason + " -- Streamable will not accept this clip. Trim or compress it first.";
      } else {
        btn.addEventListener("click", () => onCreateLink(clip.name, btn));
      }
      actions.appendChild(btn);
      if (blockReason) addMetaStatus(blockReason, true);
      // a failed transcode still has a (dead) streamable_url on the clip -- Create Link above retries; the X drops the dead link + the "Processing failed" note.
      if (failed) {
        addMetaStatus("Processing failed", true);
        const x = document.createElement("button");
        x.type = "button";
        x.className = "link-remove-btn";
        x.title = "Remove link";
        x.setAttribute("aria-label", "Remove link");
        x.innerHTML = '<svg viewBox="0 0 320.591 320.591" aria-hidden="true">' + CANCEL_ICON + '</svg>';
        x.addEventListener("click", async (e) => {
          e.stopPropagation();
          x.disabled = true;
          const c = lastClipsByName.get(clip.name) || (loadedClips || []).find((z) => z && z.name === clip.name);
          if (c) { delete c.streamable_url; delete c.transcode_status; delete c.transcode_percent; delete c.transcode_error; c.ready = undefined; }
          clipsLinkedTotal = Math.max(0, clipsLinkedTotal - 1);
          updateTabs(loadedClips);
          try {
            await fetch(HELPER + "/remove-link?file=" + encodeURIComponent(clip.name) + "&t=" + Date.now(), { method: "POST", cache: "no-store" });
          } catch {}
          rerenderCard(clip.name);
          scheduleNextPoll(POLL_FAST_MS);
        });
        actions.appendChild(x);
      }
    }
  }

  async function onCreateLink(filename, btn) {
    // spam guard: bail if an op for this clip is mid-flight or one just ran. covers a click that landed on this button right after a swap.
    if (!linkOpAllowed(filename)) return;
    linkTransitionBusy.add(filename);
    btn.disabled = true;
    btn.classList.add("is-busy");
    // the button now has an <svg> + <span class="btn-label"> inside, so we swap only the label text -- otherwise textcontent would also wipe out the link icon.
    const label = btn.querySelector(".btn-label") || btn;
    label.textContent = "Sending...";
    const pending = { requestId: "", startedAt: Date.now() };
    // a fresh Create Link overrides any pending cancel grace for this clip.
    cancelledUploads.delete(filename);
    pendingLinkCreations.set(filename, pending);
    markLocalState(filename, "uploading");
    anyUploading = true;
    rerenderCard(filename);
    let attempts = 0;
    try {
      while (true) {
        try {
          const upload = await triggerUpload(filename);
          pending.requestId = String((upload && upload.requestId) || "");
          // cancel can land before /upload has created a job. Once the response gives us its request ID, issue a second cancel against that exact job so a late upload response can never revive the clip.
          if (cancelledUploads.has(filename)) {
            await cancelActiveUpload(filename, pending.requestId, true);
            return;
          }
          scheduleNextPoll(POLL_UPLOAD_MS);
          rerenderCard(filename);
          return;
        } catch (e) {
          // transient cap contention (a just-finished cancel, another upload wrapping up) -- keep the card on "uploading" and retry a couple times before surfacing anything.
          if (e && e.busy && attempts < 2) {
            attempts++;
            await new Promise((r) => setTimeout(r, 500));
            continue;
          }
          if (cancelledUploads.has(filename)) {
            pendingLinkCreations.delete(filename);
            localState.delete(filename);
            anyUploading = localState.size > 0;
            rerenderCard(filename);
            return;
          }
          pendingLinkCreations.delete(filename);
          localState.delete(filename);
          anyUploading = localState.size > 0;
          btn.classList.remove("is-busy");
          btn.disabled = false;
          label.textContent = "Create Link";
          rerenderCard(filename);
          // "too big" -> offer the temp-compress path; "too long" -> nothing to do but trim, say so; "busy" -> the cap is full, say so plainly instead of a scary error; otherwise the generic toast.
          if (e && e.tooBig) {
            openCompressModal(filename, e.sizeMb || 0, e.capMb || 0);
          } else if (e && e.tooLong) {
            const d = e.durationSec || 0;
            const mmss = Math.floor(d / 60) + ":" + String(Math.round(d % 60)).padStart(2, "0");
            showToast("It is " + mmss + " long and the limit is 10 minutes. Trim it shorter first.", "bad", "Clip is too long for Streamable");
            scheduleToastHide(TOAST_ERROR_MS);
          } else if (e && e.busy) {
            showToast("Try that clip again in a moment.", "bad", "Upload queue is full");
            scheduleToastHide(TOAST_ERROR_MS);
          } else {
            showToast("Couldn't create link: " + (e.message || "Unknown error"));
            scheduleToastHide(TOAST_ERROR_MS);
          }
          return;
        }
      }
    } finally {
      linkTransitionBusy.delete(filename);
    }
  }

  function rerenderCard(filename) {
    // lastClipsByName briefly shrinks to page 0 right after a clip-db version change; fall back to loadedClips (the full on-screen list) so an instant repaint after Create Link / cancel never no-ops.
    const clip = lastClipsByName.get(filename)
      || loadedClips.find((c) => c && c.name === filename);
    if (!clip) return;
    const selector = '.card[data-name="' + CSS.escape(filename) + '"]';
    const card = gridEl.querySelector(selector) || favoritesGridEl.querySelector(selector);
    if (card) renderActions(card, clip, true);
  }

  function updateCard(card, clip) {
    const sig = clipSignature(clip);
    // a clip that was written over loses the length it had: a badge that is already showing it would keep it for good, and so would the cache of lengths
    const version = clipVersion(clip);
    const rewritten = card.dataset.name === clip.name && !!card.dataset.version && card.dataset.version !== version;
    if (rewritten) {
      durationCache.delete(clip.name);
      const staleBadge = card.querySelector(".duration-badge");
      if (staleBadge) {
        staleBadge.textContent = "";
        staleBadge.classList.remove("is-ready");
      }
    }
    card.dataset.version = version;
    const duration = Number(clip.duration) || 0;
    card.dataset.duration = String(duration);
    if (duration > 0) durationCache.set(clip.name, duration);
    if (card.dataset.clipSig !== sig) {
      card.dataset.name = clip.name;
      card.dataset.clipSig = sig;
      card.dataset.mtime = String(clip.mtime || 0);
      // refresh the cache-busted preview url and drop any previously-loaded src. after a trim/compress overwrite the file on disk is diffrent; without resetting video.src the next hover would replay cefs cached old bytes (and crash on seek becuase the byte offsets no longer match the file). cache key is size+mtime, not mtime alone, becuase compress-overwrite restores the source mtime to preserve sort order -- so the size delta is what guarantees a fresh url.
      card.dataset.previewUrl = clipPreviewUrlFor(clip);
      const v = card.querySelector("video");
      const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
      if (v && v.src && v !== fsEl) abortVideoLoad(v);
      const nameEl = card.querySelector(".name");
      if (nameEl) {
        nameEl.textContent = displayClipName(clip.name);
        nameEl.title = displayClipName(clip.name);
      }
      const sizeEl = card.querySelector(".size");
      if (sizeEl) sizeEl.textContent = formatSize(clip.size);
      const thumb = card.querySelector(".thumb");
      const thumbUrl = thumbUrlFor(clip);
      if (thumb && thumb.dataset.src !== thumbUrl) setThumbSource(thumb, thumbUrl);
      syncProcessing(card, clip);
      // the codec badge is otherwise baked into buildCards innerHTML once and never revisited -- a card built before clip_index_worker finishes probing a brand-new file would keep an empty badge forever even once later polls carry the real codec, since clipSig already covers clip.codec/clip.tag and gets us into this block.
      const codecEl = card.querySelector(".codec-badge");
      if (codecEl) {
        const codecReady = replayKitSettings.showCodecTags && !!clip.codec;
        const codecUnfixed = clip.codec === "hevc" && !!clip.tag && clip.tag !== "hvc1";
        codecEl.className = "codec-badge" + (codecReady ? " is-ready" : "") + (codecUnfixed ? " is-unfixed" : "");
        codecEl.textContent = codecReady ? String(clip.codec).toUpperCase() : "";
      }
    }
    syncFavoriteButton(card, clip.name);
    syncClipSelection(card, clip.name);
    const ageEl = card.querySelector(".age");
    if (ageEl) {
      ageEl.dataset.mtime = String(clip.mtime);
      ageEl.textContent = formatTimeAgo(clip.mtime);
    }
    if (duration > 0) setDurationBadge(card, duration);
    // the clip index has not measured the new file yet, so the card measures it itself
    else if (rewritten) fillDurationBadge(card);
    renderActions(card, clip);
    syncDraftUi(card, clip.name);
  }

  function render(clips) {
    const nextSignature = clipsListSignature(clips);
    const listChanged = nextSignature !== allClipsSignature;
    allClipsSignature = nextSignature;
    if (HOME_MODE) {
      renderHomeClips(clips);
      return;
    }
    // on the "all" tab, split favorites into their own sub-section above the main grid. the favorites are rendered as a plain (non-virtualized) grid -- a user typically has at most a few dozen favorites, well below the count where virtualization matters. the main grid still virtualizes the rest of the clips, so its existing math is unchanged: it just operates on the non-favorites array. on any other filter the favorites section is hidden and the main grid renders the full filtered list as before.
    const allFilterActive = (activeFilter === "all");
    if (allFilterActive) ensureFavoriteResolve();
    const favClips = allFilterActive ? applyClipFilter(favoriteClipsForRender(loadedClips)) : [];
    const favClipNames = new Set(favClips.map(c => c.name));
    const restClips = allFilterActive ? clips.filter(c => !favClipNames.has(c.name)) : clips;
    const showFavSection = allFilterActive && favClips.length > 0;
    favoritesHeadingEl.classList.toggle("is-shown", showFavSection);
    favoritesGridEl.classList.toggle("is-shown", showFavSection);
    if (showFavSection && !collapsedSections.has("favorites")) renderFavoritesGrid(favClips);
    else favoritesGridEl.replaceChildren();
    // every section keeps its heading available while its contents are collapsed.
    const restHeadingContent = allFilterActive
      ? FILTER_SECTION_HEADING.all
      : FILTER_SECTION_HEADING[activeFilter];
    restHeadingEl.classList.toggle("is-shown", !!restHeadingContent);
    restHeadingEl.classList.toggle("section-heading-rest", allFilterActive && (showFavSection || !!(screenshotGallery && screenshotGallery.count())));
    if (restHeadingContent) {
      restHeadingIconEl.innerHTML = restHeadingContent.icon;
      restHeadingLabelEl.textContent = restHeadingContent.label;
    }
    syncClipView();
    allClips = restClips;
    allClipsExpected = (clipsNextOffset >= clipsTotal || activeFilter !== "all" || clipSearchQuery.trim()) ? restClips.length : Math.max(restClips.length, clipsTotal - favClips.length);
    if (!restClips.length) {
      resetVirtualWindow();
      gridEl.style.display = "none";
      topSpacerEl.style.display = "none";
      bottomSpacerEl.style.display = "none";
      // empty placeholder only when the whole view (favorites + rest) is empty. if favorites are visible but the rest is empty, hide the placeholder entirely -- the user can clearly see their favorites at the top.
      if (showFavSection) {
        emptyEl.style.display = "none";
      } else {
        emptyEl.style.display = "block";
        emptyEl.classList.remove("error");
        if (loadedClips.length > 0) {
          emptyEl.innerHTML = escapeHtml(emptyMessageForFilter());
          if (activeFilter !== "all" && activeFilter !== "drafts" && clipsNextOffset < clipsTotal) {
            emptyEl.innerHTML += "<div class=\"hint\">Loading more clips for this filter...</div>";
            loadMoreClipsIfNeeded(true);
          }
        } else {
          emptyEl.innerHTML = "No clips yet.<div class=\"hint\">"
            + "Save a clip with your clip hotkey, or pick a different clip folder in ReplayKit Settings.</div>";
        }
      }
      return;
    }
    if (!didInitialClipRender) {
      scrollAreaEl.scrollTop = 0;
      didInitialClipRender = true;
    }
    emptyEl.style.display = "none";
    gridEl.style.display = "";
    topSpacerEl.style.display = "none";
    bottomSpacerEl.style.display = "none";
    if (listChanged) queueVirtualRender(true);
    else {
      updateVisibleCards();
      queueVirtualRender(false);
    }
  }

  // the home view: a big player for the newest clip, then a shelf of the clips after it and a shelf of the favorites, one row each. every clip is the very same card with the very same player as in the clips view, built into plain rows like the favorites grid is, with no virtual window
  const homeShelvesEl = document.getElementById("home-shelves");
  const homeHeroEl = document.getElementById("home-hero");
  const homeHeroInfoEl = document.getElementById("home-hero-info");
  const homeRecentSectionEl = document.getElementById("home-recent-section");
  const homeRecentEl = document.getElementById("home-recent");
  const homeFavsSectionEl = document.getElementById("home-favs-section");
  const homeFavsEl = document.getElementById("home-favs");
  const OPEN_FILTER_KEY = "rk-clips-open-filter";
  let homeRenderedOnce = false;

  // how many cards a row holds at the width the page has now
  function homeColumns() {
    const style = getComputedStyle(scrollAreaEl);
    const width = scrollAreaEl.clientWidth - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0);
    return Math.max(1, Math.min(HOME_CLIP_LIMIT - 1, Math.floor((width + HOME_GRID_GAP) / (HOME_MIN_CARD + HOME_GRID_GAP))));
  }

  // the first reveal of the home view waits for the favorites as well, so their shelf does not pop in under a view that is already showing
  function homeSettled() {
    return clipStateHydrated && (!favoriteNamesList().length || favoriteResolveKey === currentFavoriteResolveKey());
  }

  function homeHeroInfoHtml(clip) {
    const name = escapeHtml(displayClipName(clip.name));
    const facts = [];
    const duration = Number(clip.duration) || durationCache.get(clip.name) || 0;
    if (duration > 0) facts.push(["Length", formatDuration(duration)]);
    const width = Number(clip.width) || 0;
    const height = Number(clip.height) || 0;
    const fps = Number(clip.fps) || 0;
    if (width > 0 && height > 0) facts.push(["Resolution", width + " \u00d7 " + height + (fps > 0 ? " \u00b7 " + Math.round(fps) + " fps" : "")]);
    facts.push(["Size", formatSize(clip.size)]);
    return '<div class="home-hero-name" title="' + name + '">' + name + "</div>"
      + '<div class="home-hero-age age" data-mtime="' + (Number(clip.mtime) || 0) + '">' + formatTimeAgo(clip.mtime) + "</div>"
      + '<dl class="home-hero-facts">' + facts.map(([label, value]) => "<dt>" + label + "</dt><dd>" + escapeHtml(value) + "</dd>").join("") + "</dl>";
  }

  function renderHomeClips(clips) {
    homeRenderedOnce = true;
    // the plain rows below are the only cards of this view, so the virtual window has nothing to draw
    allClips = [];
    resetVirtualWindow();
    gridEl.style.display = "none";
    topSpacerEl.style.display = "none";
    bottomSpacerEl.style.display = "none";
    favoritesHeadingEl.classList.remove("is-shown");
    favoritesGridEl.classList.remove("is-shown");
    restHeadingEl.classList.remove("is-shown");
    ensureFavoriteResolve();
    const hero = clips[0] || null;
    if (!hero) {
      homeShelvesEl.classList.remove("is-shown");
      renderCardRow(homeHeroEl, []);
      renderCardRow(homeRecentEl, []);
      renderCardRow(homeFavsEl, []);
      emptyEl.style.display = "block";
      emptyEl.classList.remove("error");
      emptyEl.innerHTML = "No clips yet.<div class=\"hint\">Save a clip with your clip hotkey, or pick a different clip folder in ReplayKit Settings.</div>";
    } else {
      emptyEl.style.display = "none";
      homeShelvesEl.classList.add("is-shown");
      const columns = homeColumns();
      const recent = clips.slice(1, 1 + columns);
      const favorites = favoriteClipsForRender(clips).slice(0, columns);
      renderCardRow(homeHeroEl, [hero]);
      const infoHtml = homeHeroInfoHtml(hero);
      if (homeHeroInfoEl._html !== infoHtml) {
        homeHeroInfoEl._html = infoHtml;
        homeHeroInfoEl.innerHTML = infoHtml;
      }
      homeRecentSectionEl.hidden = !recent.length;
      homeRecentEl.style.setProperty("--home-cols", String(columns));
      renderCardRow(homeRecentEl, recent);
      homeFavsSectionEl.hidden = !favorites.length;
      homeFavsEl.style.setProperty("--home-cols", String(columns));
      renderCardRow(homeFavsEl, favorites);
    }
    // a card that was playing and has just left the page (a newer clip took its place, or the row got narrower) would otherwise keep playing out of sight
    if (activePreview && !activePreview.isConnected) {
      abortVideoLoad(activePreview);
      activePreview = null;
    }
    if (homeSettled()) resolveFirstRender();
  }

  // see all on a shelf: the clips view comes up, on its favorites tab when it was the shelf of the favorites; the tab is handed over thru storage, which the clips view reads when it shows
  function openClipsFromHome(filter, reveal) {
    try { if (filter === "favorites" || reveal) localStorage.setItem(OPEN_FILTER_KEY, JSON.stringify({ filter, reveal: reveal || "", at: Date.now() })); } catch {}
    fetch(HELPER + "/open-clips-window", { method: "POST", cache: "no-store" })
      .then((res) => { if (!res.ok) throw new Error("HTTP " + res.status); })
      .catch((e) => {
        try { localStorage.removeItem(OPEN_FILTER_KEY); } catch {}
        showToast("Could not open Clips: " + escapeHtml(e.message));
        scheduleToastHide(TOAST_ERROR_MS);
      });
  }
  homeShelvesEl.addEventListener("click", (e) => {
    const see = e.target.closest("[data-see]");
    if (see) openClipsFromHome(see.dataset.see);
  });

  // renders a plain row of cards (the favorites sub-section and the shelves of the home view), reusing buildCard/updateCard so they get the same interactivity as the main grid, non-virtualized becuase these lists are small -- only touches the dom for a card thats not already in the right spot, since routing every card thru a documentfragment on every poll tick (regardless of whether anything changed) detaches it from the document (a node can only have one parent), which paused a mid-hover-preview <video> the instant it was detached and resumed it on reattach, flickering the play icon every poll tick, only in these rows, since updateVisibleCards (the main grids equivalent) already updates cards in place and never re-parents them.
  function renderCardRow(container, clips) {
    const existing = new Map();
    container.querySelectorAll(".card").forEach(c => existing.set(c.dataset.name, c));
    let cursor = container.firstChild;
    for (const clip of clips) {
      let card = existing.get(clip.name);
      if (card) {
        updateCard(card, clip);
        existing.delete(clip.name);
      } else {
        card = buildCard(clip);
      }
      if (card === cursor) {
        cursor = cursor.nextSibling;
      } else {
        container.insertBefore(card, cursor);
      }
    }
    for (const leftover of existing.values()) leftover.remove();
  }
  function renderFavoritesGrid(favClips) { renderCardRow(favoritesGridEl, favClips); }

  async function poll() {
    if (document.hidden) return;
    lastPollAt = Date.now();
    if (isClipRenameActive()) {
      scheduleNextPoll(POLL_IDLE_MS);
      return;
    }
    // never refresh while a video is in fullscreen. render() reorders cards by moving them thru a documentfragment, which briefly detaches the fullscreened <video> from the dom -- and per the fullscreen api spec, removing the fullscreen element from the document forces an immediate exit. result: the popup would randomly drop out of fullscreen on the next poll tick. just pause polling for the duration of the session; well resume on the next tick after exit.
    if (isFullscreenOrHostTransition()) {
      scheduleNextPoll(POLL_FAST_MS);
      return;
    }
    if (pollInFlight) return;
    pollInFlight = true;
    try {
      let clips = await fetchClips();
      if (!clips) return;
      helperFailures = 0;
      closeErrorPopup("helper-offline");
      let uploadStatus = null;
      try { uploadStatus = await fetchUploadStatus(); } catch {}
      lastUploadStatus = uploadStatus;
      syncClipSave(uploadStatus && uploadStatus.clipSave);
      const jobSync = syncUploadJobState(uploadStatus);
      if (jobSync.completed.length) {
        clips = applyCompletedUploadLinks(clips, jobSync.completed);
        if (jobSync.completed.some(job => isFavorite(job.clipName))) refreshFavoriteClips();
      }
      const byName = new Map();
      for (const c of clips) byName.set(c.name, c);
      lastClipsByName = byName;
      const uploadError = jobSync.newErrors.length ? jobSync.newErrors[0].error : "";
      // decide whether each locally-tracked pending operation is still in flight. the helper now reports all active jobs, so this also restores bars after the clips popup is refreshed or reopened.
      for (const [name, st] of Array.from(localState)) {
        if (st !== "uploading" && st !== "compressing") continue;
        const c = byName.get(name);
        if (c && c.streamable_url) {
          localState.delete(name);
          pendingLinkCreations.delete(name);
          continue;
        }
        const job = lastUploadJobsByName.get(name);
        const helperRunningThisClip = !!(job && job.active &&
          (job.state === "uploading" || job.state === "compressing"));
        if (helperRunningThisClip) {
          // follow the helpers reported phase, so e.g. the compress-then-upload flow flips the card label from compressing -> uploading mid-way.
          if (job.state !== st) localState.set(name, job.state);
          continue;
        }
        if (hasPendingLinkCreation(name)) {
          localState.set(name, "uploading");
          continue;
        }
        // a compress/upload the user just kicked off whose job isnt in /status yet -- keep it a poll longer (self-heals via optimisticStateFreshs grace window).
        if (optimisticStateFresh(name)) continue;
        // helper isnt running this clips operation. drop the stale pending flag so finished compress-overwrite jobs return the button to "create link" after /clips sees the new mtime/size.
        optimisticStateAt.delete(name);
        localState.delete(name);
      }
      anyUploading = jobSync.active;
      // drop bulk-batch clips that have finished (or errored) so the "Stop uploads" button hides once the batch is done.
      if (bulkUploadInFlight.size) {
        for (const name of Array.from(bulkUploadInFlight)) {
          const s = localState.get(name);
          if (s !== "uploading" && s !== "compressing") bulkUploadInFlight.delete(name);
        }
        syncBulkUploadButtons();
      }
      if (uploadError) {
        showToast("Upload failed: " + uploadError);
        scheduleToastHide(TOAST_ERROR_MS);
      }
      if (isFullscreenOrHostTransition()) {
        deferVirtualRenderUntilFullscreenSettles(true);
        return;
      }
      renderLoadedClips(clips);
    } catch (e) {
      setClipSortLoading(false);
      updateClipSortControl();
      gridEl.style.display = "none";
      topSpacerEl.style.display = "none";
      bottomSpacerEl.style.display = "none";
      // hide the favorites sub-section too so the "helper offline" placeholder isnt bracketed by stale heading rows.
      favoritesHeadingEl.classList.remove("is-shown");
      favoritesGridEl.classList.remove("is-shown");
      homeShelvesEl.classList.remove("is-shown");
      restHeadingEl.classList.remove("is-shown");
      emptyEl.style.display = "block";
      emptyEl.classList.add("error");
      emptyEl.innerHTML = "ReplayKit is not responding."
        + "<div class=\"hint\">Restart OBS to reconnect.</div>";
      helperFailures += 1;
      if (helperFailures === HELPER_FAILURES_BEFORE_POPUP) {
        showErrorPopup("The helper that loads the clips is not answering, so nothing can load right now. OBS usually starts it again by itself within about half a minute. If this stays, restart OBS.", "ReplayKit is not responding", "helper-offline");
      }
      resolveFirstRender();
    } finally {
      pollInFlight = false;
      const hasActiveStreamableUpload = Array.from(lastUploadJobsByName.values()).some((job) =>
        job && job.active && job.state === "uploading");
      scheduleNextPoll(hasActiveStreamableUpload ? POLL_UPLOAD_MS : pendingClipSave ? SAVE_GHOST_POLL_MS : (anyUploading ? POLL_FAST_MS : POLL_IDLE_MS));
    }
  }

  function scheduleNextPoll(delay) {
    if (pollTimer) clearTimeout(pollTimer);
    if (document.hidden) return;
    pollTimer = setTimeout(poll, delay);
  }

  // fade + short rise on the grid when its contents are swapped wholesale (tab switch, sort change). opacity + transform only -- neither affects scrollTop / clientHeight / element height, which are the measurements rendervirtualwindow relies on, so the render underneath stays correct.
  function flashGridSwap() {
    if (!scrollAreaEl.animate) return;
    try {
      scrollAreaEl.animate(
        [{ opacity: 0.25, transform: "translateY(7px)" }, { opacity: 1, transform: "translateY(0)" }],
        { duration: 220, easing: "cubic-bezier(.16, 1, .3, 1)" }
      );
    } catch (_) {}
  }

  // a change of tab brings the new list in with a rise and a fade, a row after the row above it and a card after the card to its left; the rise is the individual translate property, which composes with the transform that places a virtual card, and the ease has no overshoot, so the list settles and does not bounce
  function fadeGridSwap() {
    if (!scrollAreaEl.animate) return;
    requestAnimationFrame(() => {
      try {
        const area = scrollAreaEl.getBoundingClientRect();
        const shown = [];
        for (const el of scrollAreaEl.querySelectorAll(".card, .section-heading.is-shown, #empty")) {
          if (el.offsetParent === null) continue;
          const r = el.getBoundingClientRect();
          if (r.bottom < area.top || r.top > area.bottom) continue;
          shown.push({ el, top: Math.round(r.top), left: Math.round(r.left) });
        }
        const rows = Array.from(new Set(shown.map((s) => s.top))).sort((a, b) => a - b);
        const cols = Array.from(new Set(shown.map((s) => s.left))).sort((a, b) => a - b);
        for (const s of shown) {
          const delay = Math.min(rows.indexOf(s.top), 4) * 55 + Math.min(cols.indexOf(s.left), 4) * 18;
          s.el.animate(
            [{ opacity: 0, translate: "0 14px" }, { opacity: 1, translate: "0 0" }],
            { duration: 380, delay, easing: "cubic-bezier(.16, 1, .3, 1)", fill: "backwards" }
          );
        }
      } catch (_) {}
    });
  }

  // FLIP: run `mutate` (a dom change that reflows the row), then slide the given elements from where they were to where they landed. uses the web animations api so it leaves no inline styles behind and reverts cleanly to whatever css says afterwards.
  function rkFlip(els, mutate) {
    const list = (els || []).filter(Boolean);
    if (!list.length || !list[0].animate) { mutate(); return; }
    const before = list.map((el) => el.getBoundingClientRect());
    mutate();
    for (let i = 0; i < list.length; i++) {
      const a = before[i];
      const b = list[i].getBoundingClientRect();
      const dx = a.left - b.left;
      const dy = a.top - b.top;
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) continue;
      try {
        list[i].animate(
          [{ transform: "translate(" + dx + "px, " + dy + "px)" }, { transform: "translate(0, 0)" }],
          { duration: 240, easing: "cubic-bezier(.16, 1, .3, 1)" }
        );
      } catch (_) {}
    }
  }

  function selectClipFilter(next) {
    if (next === activeFilter) { renderLoadedClips(loadedClips); return; }
    activeFilter = next;
    if (next === "screenshots") {
      clearClipSelection();
      stopActivePreviewForScroll();
      if (activeSort === "longest" || activeSort === "shortest") activeSort = "newest";
    }
    if (next === "drafts") refreshDrafts();
    scrollAreaEl.scrollTop = 0;
    resetVirtualWindow();
    renderLoadedClips(loadedClips);
    fadeGridSwap();
  }
  tabBtns.forEach((btn) => {
    btn.addEventListener("click", () => selectClipFilter(btn.dataset.filter || "all"));
  });
  // the clip the home view asked for: the view is put on everything, then scrolled to the clip, and the card lights up for a moment. the clip is in one of two places, the favorites above the grid or the virtual window of the grid; one that is in neither yet is waited for while the list loads, and one that does not show up in time is searched by its name
  let revealTimer = 0;
  function revealClip(name) {
    clearTimeout(revealTimer);
    if (activeFilter !== "all") selectClipFilter("all");
    if (clipSearchQuery) {
      clipSearchQuery = "";
      if (clipSearchInput) clipSearchInput.value = "";
      renderLoadedClips(loadedClips);
    }
    const cardFor = () => Array.from(document.querySelectorAll(".card")).find((c) => c.dataset.name === name && !c.classList.contains("is-theater-card"));
    const light = () => {
      const card = cardFor();
      if (!card) return;
      card.classList.remove("is-located");
      void card.offsetWidth;
      card.classList.add("is-located");
      setTimeout(() => card.classList.remove("is-located"), 2600);
    };
    const started = Date.now();
    let searched = false;
    let lastTop = -1;
    let settled = 0;
    const step = () => {
      const card = cardFor();
      if (card) {
        // scrolled to, smoothly, and lit once the scroll has stopped, since the cards of the grid are recycled while it moves
        const bounds = card.getBoundingClientRect();
        const area = scrollAreaEl.getBoundingClientRect();
        const centered = bounds.top >= area.top && bounds.bottom <= area.bottom;
        if (!centered) card.scrollIntoView({ block: "center", behavior: "smooth" });
        if (Math.abs(scrollAreaEl.scrollTop - lastTop) < 1) settled += 1; else settled = 0;
        lastTop = scrollAreaEl.scrollTop;
        if (settled >= 2 && centered) { light(); return; }
      } else {
        const index = allClips.findIndex((c) => c && c.name === name);
        if (index >= 0) {
          const row = Math.floor((index + saveGhostSlots()) / Math.max(1, virtualColumns));
          const top = (gridEl.offsetTop - scrollAreaEl.offsetTop) + row * virtualRowStride;
          scrollAreaEl.scrollTo({ top: Math.max(0, top - Math.max(0, (scrollAreaEl.clientHeight - virtualRowStride) / 2)), behavior: "smooth" });
          queueVirtualRender(false);
        }
      }
      const waited = Date.now() - started;
      if (waited > 4500 && !searched) {
        searched = true;
        clipSearchQuery = displayClipName(name);
        if (clipSearchInput) clipSearchInput.value = clipSearchQuery;
        scrollAreaEl.scrollTop = 0;
        renderLoadedClips(loadedClips);
      }
      if (waited > 8000) return;
      revealTimer = setTimeout(step, 120);
    };
    step();
  }

  // a see all on a shelf of the home view, or a show in clips on one clip, leaves a note in storage, since the clips view is another page; it is read when this view shows, and a note older than a few seconds is a leftover that is dropped
  function applyPendingClipsFilter() {
    let pending = null;
    try {
      pending = JSON.parse(localStorage.getItem(OPEN_FILTER_KEY) || "null");
      localStorage.removeItem(OPEN_FILTER_KEY);
    } catch {}
    if (!pending || !(Date.now() - Number(pending.at) < 5000)) return;
    if (pending.reveal) { revealClip(String(pending.reveal)); return; }
    if (pending.filter === "favorites") selectClipFilter("favorites");
  }

  updateClipSortControl();
  if (clipSearchInput) {
    clipSearchInput.addEventListener("input", () => {
      clipSearchQuery = clipSearchInput.value || "";
      scrollAreaEl.scrollTop = 0;
      // no reset of the grid here: a card whose clip is still in the list stays as it is with its picture, and a reset let go of every one of them and brought them back one at a time, which was the flash
      renderLoadedClips(loadedClips);
    });
  }
  if (sortButton && sortWrap) {
    sortButton.addEventListener("click", (e) => {
      e.stopPropagation();
      if (sortWrap.classList.contains("is-open")) closeClipSortMenu();
      else openClipSortMenu();
    });
    sortOptionBtns.forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        const next = CLIP_SORT_VALUES.has(btn.dataset.sort) ? btn.dataset.sort : "newest";
        closeClipSortMenu();
        if (next === activeSort) return;
        activeSort = next;
        if (activeFilter === "screenshots") {
          screenshotGallery.update(clipSearchQuery, activeSort);
          updateClipSortControl();
          return;
        }
        clipsPageVersion = "";
        clipsTotal = 0;
        clipsTotalKnown = false;
        clipsNextOffset = 0;
        clipsLoadingMore = false;
        favoriteResolveKey = "";
        scheduleFavoriteResolve(0);
        setClipSortLoading(true);
        scrollAreaEl.scrollTop = 0;
        updateClipSortControl();
        flashGridSwap();
        scheduleNextPoll(0);
      });
    });
    if (sortMenu) {
      sortMenu.querySelectorAll(".clip-sort-grouprow").forEach((row) => {
        row.addEventListener("click", (e) => {
          e.stopPropagation();
          const g = row.closest(".clip-sort-group");
          if (!g) return;
          const willExpand = !g.classList.contains("is-expanded");
          if (willExpand) {
            // accordion -- opening one group collapses the other so the menu stays short
            sortMenu.querySelectorAll(".clip-sort-group.is-expanded").forEach((other) => {
              if (other === g) return;
              other.classList.remove("is-expanded");
              const orow = other.querySelector(".clip-sort-grouprow");
              if (orow) orow.setAttribute("aria-expanded", "false");
            });
          }
          g.classList.toggle("is-expanded", willExpand);
          row.setAttribute("aria-expanded", willExpand ? "true" : "false");
        });
      });
    }
    document.addEventListener("click", (e) => {
      if (!sortWrap.classList.contains("is-open")) return;
      if (e.target.closest && e.target.closest("#clip-sort")) return;
      closeClipSortMenu();
    });
  }

  scrollAreaEl.addEventListener("scroll", () => {
    closeClipSortMenu();
    lastScrollActivityAt = performance.now ? performance.now() : Date.now();
    stopActivePreviewForScroll();
    if (fullscreenHostTransition) {
      deferVirtualRenderUntilFullscreenSettles(false);
      return;
    }
    queueVirtualRender(false);
    loadMoreClipsIfNeeded(false);
  }, { passive: true });
  // window resizes (especially maximize) reflow the grid via queuevirtualrender, which moves cards thru a documentfragment. the render itself guards against tearing down a fullscreened card (see rendervirtualwindow), so this handler just kicks the renderer and lets that guard suppress the work when needed.
  window.addEventListener("resize", () => {
    if (resizeRenderRaf) cancelAnimationFrame(resizeRenderRaf);
    resizeRenderRaf = requestAnimationFrame(() => {
      resizeRenderRaf = 0;
      if (isFullscreenOrHostTransition()) {
        deferVirtualRenderUntilFullscreenSettles(true);
        return;
      }
      measureVirtualRow();
      queueVirtualRender(true);
    });
  });
  function onFullscreenChange() {
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
    updateFullscreenBodyState();
    if (!fsEl && pendingRenderOnFsExit) {
      pendingRenderOnFsExit = false;
      deferVirtualRenderUntilFullscreenSettles(true);
      flushDeferredFullscreenRender(220);
    }
  }
  document.addEventListener("fullscreenchange", onFullscreenChange);
  document.addEventListener("webkitfullscreenchange", onFullscreenChange);

  // the clips window is hidden (not destroyed) on close now, so its js state survives a close/reopen. put every session-only bit back to a fresh-window default when its shown again. deliberately leaves alone: in-flight upload/compress tracking (bulkUpload*, localState, pendingLinkCreations -- real helper work) and favorites (persisted).
  function resetClipsWindowState() {
    document.querySelectorAll(".card video").forEach(resetPlaybackRate);
    const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
    if (fsEl) { try { (document.exitFullscreen || document.webkitExitFullscreen).call(document); } catch {} }
    // a fresh-window reset owns the scroll position (it forces it to 0 below); drop any pending post-fullscreen restore so it cant fire late and yank the list back to where fullscreen was entered.
    fullscreenSavedScrollTop = null;
    // theater mode is a session-only view state (body.is-clip-theater + is-theater-card) -- a hidden-then-reshown window must land back on the plain grid. no-ops if we werent in theater.
    exitTheaterMode();

    const renameInput = document.querySelector(".name-edit-input");
    if (renameInput) renameInput.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

    closeAllMenus();
    resetAllArmedDeleteButtons();
    if (typeof closeClipSortMenu === "function") closeClipSortMenu();
    document.querySelectorAll(".settings-wrap.is-open").forEach((w) => w.classList.remove("is-open"));
    closeEditor();
    document.querySelectorAll(".modal-veil.is-open").forEach((v) => {
      if (trimSaving && (v === trimVeil || v === saveVeil)) return;
      v.classList.remove("is-open");
    });
    resetErrorPopup();

    if (activePreview) { try { abortVideoLoad(activePreview); } catch {} activePreview = null; }

    if (selectedClipNames.size) {
      selectedClipNames.clear();
      resetBulkDeleteButton();
      refreshBulkSelectionUi();
    }

    activeFilter = "all";
    syncClipView();
    if (screenshotGallery) screenshotGallery.refresh();
    updateTabs(loadedClips);

    activeSort = "newest";
    clipsPageVersion = "";
    clipsTotal = 0;
    clipsTotalKnown = false;
    clipsNextOffset = 0;
    clipsLoadingMore = false;
    favoriteResolveKey = "";
    scheduleFavoriteResolve(0);
    refreshDrafts();
    updateClipSortControl();

    didInitialClipRender = false;
    scrollAreaEl.scrollTop = 0;
    resetVirtualWindow();
    renderLoadedClips(loadedClips);
  }
  window.__replaykitResetClips = resetClipsWindowState;

  // the rail was pressed on this page while it already shows: that is the way back to the plain grid, out of the editor first and, when there is no editor, out of theater mode
  window.__rkReselect = function () {
    if (trimVeil.classList.contains("is-open") || trimProcessingVeil.classList.contains("is-open")) {
      closeEditor();
      return true;
    }
    return exitTheaterMode();
  };

  // the editor survives a trip to another view of the obs window: the video stops, and the rest of the reset waits for the window to go away (tray, close), which drops the edit to a draft like before
  let editorHeldAway = false;
  function editorKeptForReturn() {
    const open = trimVeil.classList.contains("is-open") || trimProcessingVeil.classList.contains("is-open");
    return open && !!(window.RKWindow && window.RKWindow.windowUp && window.RKWindow.windowUp());
  }
  function holdEditorWhileAway() {
    editorHeldAway = true;
    const editor = editorApi();
    if (editor) editor.pause();
    closeAllMenus();
  }
  window.addEventListener("replaykit-window", (e) => {
    if (!editorHeldAway || !e.detail || e.detail.windowUp !== false) return;
    editorHeldAway = false;
    resetClipsWindowState();
  });
  document.addEventListener("visibilitychange", () => { if (!document.hidden) editorHeldAway = false; });

  let wasHiddenSinceShown = false;
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      wasHiddenSinceShown = true;
      if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
      // dont tear down a fullscreened preview when the popup is backgrounded (alt-tab, system notification, etc). aborting clears the <video>s src, which forces an exit from fullscreen -- and the user is presumably still actively watching it.
      const fsEl = document.fullscreenElement || document.webkitFullscreenElement;
      if (activePreview && !(fsEl && (activePreview === fsEl || (fsEl.contains && fsEl.contains(activePreview))))) {
        abortVideoLoad(activePreview);
        activePreview = null;
      }
      // embedded in the obs window this page sits behind another view now, so the reset is done while nobody looks and the next show finds a clean grid instead of watching it reset; an open editor is kept for when the user comes back, unless the window itself went away
      if (window.RKEmbedded) {
        wasHiddenSinceShown = false;
        if (editorKeptForReturn()) holdEditorWhileAway();
        else resetClipsWindowState();
      }
    } else {
      if (wasHiddenSinceShown) {
        wasHiddenSinceShown = false;
        resetClipsWindowState();
      }
      if (!HOME_MODE) applyPendingClipsFilter();
      poll();
      queueVirtualRender(true);
      // the clips view may have changed favorites while this one was hidden
      hydrateClipUiState();
    }
  });

  // fallback for CEF hosts where hide()/show() lands as a window focus change rather than a visibilitychange -- only fires the reset if we already saw a hidden state, so an ordinary alt-tab back never triggers it.
  window.addEventListener("focus", () => {
    if (!wasHiddenSinceShown || document.hidden) return;
    wasHiddenSinceShown = false;
    resetClipsWindowState();
    poll();
    queueVirtualRender(true);
  });

  setInterval(() => {
    if (document.hidden) return;
    document.querySelectorAll(".age").forEach(el => {
      const m = parseInt(el.dataset.mtime, 10);
      if (m) el.textContent = formatTimeAgo(m);
    });
    document.querySelectorAll(".card").forEach(card => {
      const clip = lastClipsByName.get(card.dataset.name);
      if (clip && clip.streamable_url) renderActions(card, clip, true);
    });
  }, 60 * 1000);

  // poll-loop watchdog. CEF hosts dont reliably deliver visibilitychange on the windows own hide()/show(), so the loop can stall after the popup is backgrounded (e.g. during a helper restart) and never re-arm -- leaving a stuck "Helper offline". if were visible and havent polled in a while, kick it.
  setInterval(() => {
    if (!document.hidden && !pollInFlight && Date.now() - lastPollAt > 6000) poll();
  }, 3000);

  // "clip too big -> compress temp copy and upload" flow. when /upload returns toobig=true (helper rejected becuase the clip exceeds the current auth states size cap), we surface a confirmation modal. accepting posts /compress to the helper, which creates a temp compressed mp4, uploads that temp file, and records the streamable link against the original clip name.
  const compressVeil   = document.getElementById("compress-veil");
  const compressBody   = document.getElementById("compress-body");
  const compressGo     = document.getElementById("compress-go");
  const compressCancel = document.getElementById("compress-cancel");

  let pendingCompressFile = null;

  function openCompressModal(filename, sizeMb, capMb) {
    pendingCompressFile = filename;
    compressBody.innerHTML =
      "<p><code>" + escapeHtml(filename) + "</code> is <b>" + sizeMb +
      " MB</b>, which is over Streamable's <b>" + capMb +
      " MB</b> limit for your current plan.</p>" +
      "<p>Create a temporary compressed copy and upload that instead?</p>";
    compressVeil.classList.add("is-open");
  }
  function closeCompressModal() {
    compressVeil.classList.remove("is-open");
    pendingCompressFile = null;
  }

  compressCancel.addEventListener("click", closeCompressModal);
  compressVeil.addEventListener("mousedown", (e) => {
    if (e.target === compressVeil) closeCompressModal();
  });
  compressVeil.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { e.preventDefault(); closeCompressModal(); }
  });

  compressGo.addEventListener("click", async () => {
    if (!pendingCompressFile) { closeCompressModal(); return; }
    const file = pendingCompressFile;
    closeCompressModal();
    try {
      const res = await fetch(HELPER + "/compress?file=" + encodeURIComponent(file)
                              + "&t=" + Date.now(),
                              { method: "POST", cache: "no-store" });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || !data.ok) {
        throw new Error(data.message || ("HTTP " + res.status));
      }
      cancelledUploads.delete(file);
      markLocalState(file, "compressing");
      anyUploading = true;
      scheduleNextPoll(POLL_FAST_MS);
      rerenderCard(file);
      showToast("Compressing temp copy for " + escapeHtml(file) + ".", "info");
      scheduleToastHide(3500);
    } catch (e) {
      showToast("Couldn't start compression: " + (e.message || e));
      scheduleToastHide(TOAST_ERROR_MS);
    }
  });

  // The mode picker starts a compression that replaces the clip only when the output is smaller.
  const compressModeVeil     = document.getElementById("compress-mode-veil");
  const compressModeModal    = document.getElementById("compress-mode-modal");
  const compressModeCancel   = document.getElementById("compress-mode-cancel");
  const compressModeClipName = document.getElementById("compress-mode-clip-name");
  const compressModeCards    = compressModeVeil.querySelectorAll(".compress-mode-card");

  // optional resolution reduction folded into compress-overwrite. state lives in localStorage -- its a clips-window setting, not a ReplayKit setting.
  const compressDsRow      = document.getElementById("compress-downsample-row");
  const compressDsToggle   = document.getElementById("compress-downsample-toggle");
  const compressDsDetail   = document.getElementById("compress-downsample-detail");
  const compressDsSelect   = RKCombo.bind(document.getElementById("compress-downsample-select"), writeCompressDsPrefs);
  const COMPRESS_DS_ON_KEY = "replaykit_compress_downsample_on";
  const COMPRESS_DS_H_KEY  = "replaykit_compress_downsample_height";
  // common share/export heights; the modal only offers the ones below the clips own height.
  const COMPRESS_DS_LADDER = [1440, 1080, 900, 720, 540, 480, 360];

  function readCompressDsPrefs() {
    let on = false, height = 0;
    try { on = localStorage.getItem(COMPRESS_DS_ON_KEY) === "1"; } catch (_) {}
    try { height = parseInt(localStorage.getItem(COMPRESS_DS_H_KEY) || "0", 10) || 0; } catch (_) {}
    return { on, height };
  }
  function writeCompressDsPrefs() {
    try { localStorage.setItem(COMPRESS_DS_ON_KEY, compressDsToggle.checked ? "1" : "0"); } catch (_) {}
    try { localStorage.setItem(COMPRESS_DS_H_KEY, compressDsSelect.value || "0"); } catch (_) {}
  }
  function syncCompressDsDetail() {
    compressDsDetail.hidden = !compressDsToggle.checked || compressDsRow.hidden;
    if (compressDsDetail.hidden) compressDsSelect.close();
  }
  // fills the dropdown from the clips own resolution. returns false (and the caller hides the whole section) when the resolution is unknown or already at/below the smallest rung.
  function buildCompressDsOptions(srcW, srcH) {
    const heights = COMPRESS_DS_LADDER.filter((h) => h < srcH);
    compressDsSelect.setOptions(heights.map((h) => {
      const w = srcW && srcH ? Math.round((srcW * h / srcH) / 2) * 2 : 0;
      return { value:String(h), label:w ? (w + " × " + h) : (h + "p") };
    }));
    if (!heights.length) return false;
    return true;
  }
  function currentCompressScaleHeight() {
    if (compressDsRow.hidden || !compressDsToggle.checked) return 0;
    const h = parseInt(compressDsSelect.value || "0", 10);
    return h > 0 ? h : 0;
  }

  let pendingCompressOverwriteFile = null;

  // apply the compress-history rules to the modal: clip already at slow -> lock the modal entirely, show the "already at max compression" banner, no cards clickable. clip already at fast -> fast card disabled, smaller still availible (fast -> slow upgrade path). unmarked -> both cards availible (defualt). the rules are also enforced server-side; this is the ux layer so the user never has to click into an error to discover them.
  function applyCompressModeRules(existingMode) {
    compressModeModal.classList.remove("is-locked");
    compressModeCards.forEach(function (card) {
      card.classList.remove("is-disabled");
      card.removeAttribute("aria-disabled");
    });
    if (existingMode === "slow") {
      compressModeModal.classList.add("is-locked");
      compressModeCards.forEach(function (card) {
        card.classList.add("is-disabled");
        card.setAttribute("aria-disabled", "true");
      });
      return "locked";
    }
    if (existingMode === "fast") {
      compressModeCards.forEach(function (card) {
        if (card.dataset.mode === "fast") {
          card.classList.add("is-disabled");
          card.setAttribute("aria-disabled", "true");
        }
      });
      return "fast-disabled";
    }
    return "open";
  }

  function openCompressModePicker(filename) {
    pendingCompressOverwriteFile = filename;
    compressModeClipName.textContent = filename || "";
    compressModeClipName.title = filename || "";

    // look up the clips hidden compress marker (mirrored by the helper from the mp4s comment atom). cards / banner are tuned accordingly before the modal becomes visible.
    let existingMode = "";
    const clip = filename ? lastClipsByName.get(filename) : null;
    if (clip && (clip.cmp_mode === "fast" || clip.cmp_mode === "slow")) {
      existingMode = clip.cmp_mode;
    }
    const result = applyCompressModeRules(existingMode);

    // downsample section: build the ladder from this clips own resolution; hide the whole option if we dont know it (unindexed) or its already at/below the smallest rung.
    const srcW = clip && clip.width > 0 ? clip.width : 0;
    const srcH = clip && clip.height > 0 ? clip.height : 0;
    const dsAvailable = srcH > 0 && buildCompressDsOptions(srcW, srcH);
    compressDsRow.hidden = !dsAvailable;
    if (dsAvailable) {
      const prefs = readCompressDsPrefs();
      compressDsToggle.checked = prefs.on;
      const opts = Array.from(compressDsSelect.options).map((o) => o.value);
      if (prefs.height && opts.includes(String(prefs.height))) compressDsSelect.value = String(prefs.height);
      else compressDsSelect.value = opts.includes("1080") ? "1080" : opts[0];
    }
    syncCompressDsDetail();

    compressModeVeil.classList.add("is-open");
    // focus the first enabled card so enter commits to a valid option and the user can navigate with arrow keys / tab. if everything is locked, focus cancel.
    if (result === "locked") {
      try { compressModeCancel.focus(); } catch (_) {}
      return;
    }
    const firstEnabled = compressModeVeil.querySelector(".compress-mode-card:not(.is-disabled)");
    if (firstEnabled) { try { firstEnabled.focus(); } catch (_) {} }
  }
  function closeCompressModePicker() {
    compressDsSelect.close();
    compressModeVeil.classList.remove("is-open");
    pendingCompressOverwriteFile = null;
  }

  // centralised so we dont drift between the modal cards and any other caller that might want to start an overwrite-compress. the server enforces the same compress-history rules as the modal; alreadycompressed comes back as 409 with a message we surface verbatim so the user sees the same wording either way.
  async function startCompressOverwrite(file, mode, scaleHeight) {
    if (!file) return;
    scaleHeight = scaleHeight > 0 ? scaleHeight : 0;
    try {
      const res = await fetch(HELPER + "/compress-overwrite?file=" + encodeURIComponent(file)
                              + "&mode=" + encodeURIComponent(mode)
                              + (scaleHeight > 0 ? "&scaleHeight=" + scaleHeight : "")
                              + "&t=" + Date.now(),
                              { method: "POST", cache: "no-store" });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || !data.ok) {
        if (data && data.alreadyCompressed) {
          showToast(data.message || "Already compressed.", "info");
          scheduleToastHide(4000);
          return;
        }
        throw new Error(data.message || ("HTTP " + res.status));
      }
      cancelledUploads.delete(file);
      markLocalState(file, "compressing");
      anyUploading = true;
      scheduleNextPoll(POLL_FAST_MS);
      rerenderCard(file);
      let label = mode === "fast" ? "Compressing (fast)" : "Compressing (smaller)";
      if (scaleHeight > 0) label += " -> " + scaleHeight + "p";
      showToast(label + " - " + escapeHtml(file), "info");
      scheduleToastHide(3000);
    } catch (err) {
      showToast("Couldn't start compress: " + (err.message || err));
      scheduleToastHide(TOAST_ERROR_MS);
    }
  }

  compressModeCards.forEach((card) => {
    card.addEventListener("click", () => {
      // defensive: css pointer-events:none already blocks the click for .is-disabled cards, but if a future tweak loosens that css we still want the rule enforced here. disabled cards simply do nothing instead of opening a confusing error.
      if (card.classList.contains("is-disabled")) return;
      const file = pendingCompressOverwriteFile;
      const mode = card.dataset.mode || "smaller";
      const scaleHeight = currentCompressScaleHeight();
      closeCompressModePicker();
      startCompressOverwrite(file, mode, scaleHeight);
    });
  });
  compressDsToggle.addEventListener("change", () => { syncCompressDsDetail(); writeCompressDsPrefs(); });
  compressModeCancel.addEventListener("click", closeCompressModePicker);
  compressModeVeil.addEventListener("mousedown", (e) => {
    if (e.target === compressModeVeil) closeCompressModePicker();
  });
  compressModeVeil.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { e.preventDefault(); closeCompressModePicker(); }
  });

  // compress all: bulk fast-compress of every clip that isnt already marked. client-side queue with busy-retry so the servers concurrency limiter (steady-state + cpu-gated burst) does the actual scheduling without us having to mirror its state. fast mode only -- bulk compress on smaller would take hours on a typical folder; users who want max compression on a specific clip still have the per-card 3-dot menu.
  const clipFolderBtn = document.getElementById("clip-folder-btn");
  if (clipFolderBtn) clipFolderBtn.addEventListener("click", () => {
    fetch(HELPER + (activeFilter === "screenshots" ? "/open-screenshot-folder" : "/open-clip-folder"), { method: "POST", cache: "no-store" }).catch(() => {});
  });

  const compressAllBtn        = document.getElementById("compress-all-btn");
  const compressAllLabelEl    = document.getElementById("compress-all-label");
  const compressAllVeil       = document.getElementById("compress-all-veil");
  const compressAllBody       = document.getElementById("compress-all-body");
  const compressAllGo         = document.getElementById("compress-all-go");
  const compressAllCancel     = document.getElementById("compress-all-cancel");
  const bulkUploadBtn         = document.getElementById("bulk-upload");
  const bulkUploadStopBtn     = document.getElementById("bulk-upload-stop");
  const bulkCompressBtn       = document.getElementById("bulk-compress");
  const bulkDeleteBtn         = document.getElementById("bulk-delete");
  const bulkClearBtn          = document.getElementById("bulk-clear");

  // mutated by the queue runner. when true, the button label flips to "stop (x/y)" and clicking it stops scheduling new jobs. in-flight ffmpeg encodes continue -- we dont kill them mid-run becuase the servers atomic-replace is what makes that safe.
  let compressAllRunning = false;
  let compressAllAbort   = false;
  let pendingBulkCompressQueue = null;

  function compressAllCandidates() {
    // walk the full known clips list. skip anything already marked fast or slow (the rules say re-fast is a no-op, and slow is already maximally compressed), and anything currently mid-job so we dont pile on top of a manual operation.
    const list = [];
    for (const clip of allClips) {
      if (!clip || !clip.name) continue;
      if (clip.cmp_mode === "fast" || clip.cmp_mode === "slow") continue;
      const st = localState.get(clip.name);
      if (st === "compressing" || st === "uploading") continue;
      list.push(clip.name);
    }
    return list;
  }

  function selectedCompressCandidates() {
    return Array.from(selectedClipNames).filter((name) => {
      const clip = lastClipsByName.get(name) || loadedClips.find((item) => item && item.name === name);
      if (!clip || clip.cmp_mode === "fast" || clip.cmp_mode === "slow") return false;
      const state = localState.get(name);
      return state !== "compressing" && state !== "uploading";
    });
  }

  function selectedUploadCandidates() {
    return Array.from(selectedClipNames).filter((name) => {
      const clip = lastClipsByName.get(name) || loadedClips.find((item) => item && item.name === name);
      if (!clip || clip.streamable_url) return false;
      const state = localState.get(name);
      return state !== "compressing" && state !== "uploading";
    });
  }

  let bulkUploadRunning = false;
  let bulkUploadAbort = false;
  let bulkUploadProgress = { done: 0, total: 0 };

  // single source of truth for BOTH the upload icon (#bulk-upload) and the "Stop uploads" text button. stop mode = scheduling (bulkUploadRunning) OR clips this batch handed to the helper are still uploading (bulkUploadInFlight). in stop mode both buttons stay live so either one cancels the whole batch; outside it the icon is the normal Upload trigger. verbose form for the icons floating tooltip (no width limit).
  function bulkStopLabel() {
    if (bulkUploadAbort) return "Stopping...";
    const up = bulkUploadInFlight.size;
    const queued = bulkUploadQueued.size;
    if (bulkUploadRunning || queued > 0) {
      return queued > 0
        ? "Stop — " + up + " uploading, " + queued + " queued"
        : "Stop uploads (" + up + ")";
    }
    return "Cancel " + up + " upload" + (up === 1 ? "" : "s");
  }
  // terse form for the #bulk-upload-stop text button, which shares the toolbar row with Clear + Sort.
  function bulkStopLabelShort() {
    if (bulkUploadAbort) return "Stopping...";
    const up = bulkUploadInFlight.size;
    const queued = bulkUploadQueued.size;
    if (queued > 0) return "Stop (" + queued + " queued)";
    if (bulkUploadRunning) return "Stop uploads";
    return "Cancel " + up + " upload" + (up === 1 ? "" : "s");
  }
  function syncBulkUploadButtons() {
    const stopMode = bulkUploadRunning || bulkUploadInFlight.size > 0;
    if (bulkUploadStopBtn) {
      bulkUploadStopBtn.hidden = !stopMode;
      if (stopMode) {
        bulkUploadStopBtn.disabled = bulkUploadAbort;
        bulkUploadStopBtn.textContent = bulkStopLabelShort();
      }
    }
    if (!bulkUploadBtn) return;
    bulkUploadBtn.classList.toggle("is-running", stopMode);
    let tip, aria;
    if (stopMode) {
      bulkUploadBtn.disabled = bulkUploadAbort;
      tip = bulkStopLabel();
      aria = tip;
    } else {
      const uploadable = selectedUploadCandidates().length;
      bulkUploadBtn.disabled = selectedClipNames.size > 0 && uploadable === 0;
      tip = uploadable ? "Upload" : "No selected clips need uploading";
      aria = uploadable ? "Upload selected clips" : tip;
    }
    bulkUploadBtn.dataset.tooltip = tip;
    bulkUploadBtn.setAttribute("aria-label", aria);
  }

  // thin wrapper kept for the existing runSelectedUploads call sites -- records progress then defers to the shared sync.
  function updateBulkUploadButton(mode, done, total) {
    if (mode === "running") bulkUploadProgress = { done: done || 0, total: total || 0 };
    syncBulkUploadButtons();
  }

  // abort scheduling + cancel every upload this batch already handed to the helper (quiet, one summary toast). shared by the icon (in stop mode) and the text button.
  function stopBulkUploads() {
    if (!bulkUploadRunning && bulkUploadInFlight.size === 0) return;
    bulkUploadAbort = true;
    syncBulkUploadButtons();
    const inflight = Array.from(bulkUploadInFlight);
    bulkUploadInFlight.clear();
    inflight.forEach((name) => {
      // pass the requestId so the helper matches the job directly rather than by clip name.
      const job = lastUploadJobsByName.get(name);
      const pend = pendingLinkCreations.get(name);
      cancelActiveUpload(name, (job && job.requestId) || (pend && pend.requestId) || "", true);
    });
    if (inflight.length) {
      showToast("Cancelled " + inflight.length + " upload" + (inflight.length === 1 ? "" : "s") + ".", "info");
      scheduleToastHide(2500);
    }
    if (!bulkUploadRunning) bulkUploadAbort = false;
    syncBulkUploadButtons();
  }

  // drains the bulk upload selection through the helpers concurrency limiter one accepted job at a time. the helper only runs MAX_CONCURRENT_VIDEO_JOBS at once and returns busy=true past that, so rather than firing every /upload back to back and marking the rejected ones failed, we keep each clip on its card as "Queued" and retry it until a slot frees -- a clip only counts as failed on a real error. bulkUploadAbort (the button doubles as Stop) halts scheduling; clips already handed to the helper keep going and can be cancelled from their own card.
  async function runSelectedUploads(names) {
    if (bulkUploadRunning) return;
    const list = names.slice();
    const total = list.length;
    let started = 0;
    let done = 0;
    const failed = [];

    bulkUploadRunning = true;
    bulkUploadAbort = false;
    bulkUploadQueued.clear();
    // lock selection-clear for the run, same as deleteSelectedClips -- clearing hides the whole bulk bar and with it the Stop button.
    if (bulkClearBtn) bulkClearBtn.disabled = true;
    // show every clip as queued up front so the selection visibly becomes a pipeline instead of a stall.
    for (const name of list) {
      cancelledUploads.delete(name);
      bulkUploadQueued.add(name);
      if (!hasPendingLinkCreation(name)) pendingLinkCreations.set(name, { requestId: "", startedAt: Date.now() });
      markLocalState(name, "uploading");
      rerenderCard(name);
    }
    anyUploading = true;
    updateBulkUploadButton("running", done, total);

    for (const name of list) {
      if (bulkUploadAbort) break;
      done++;
      // cancelled from its own card while waiting, or linked in the meantime -- nothing to hand off.
      if (!bulkUploadQueued.has(name)) { updateBulkUploadButton("running", done, total); continue; }
      const clip = lastClipsByName.get(name);
      if (clip && clip.streamable_url) {
        bulkUploadQueued.delete(name);
        pendingLinkCreations.delete(name);
        localState.delete(name);
        updateBulkUploadButton("running", done, total);
        continue;
      }

      const pending = pendingLinkCreations.get(name) || { requestId: "", startedAt: Date.now() };
      pendingLinkCreations.set(name, pending);
      let handedOff = false;
      let attempts = 0;
      while (!handedOff && !bulkUploadAbort && bulkUploadQueued.has(name)) {
        try {
          const upload = await triggerUpload(name, true);
          pending.requestId = String((upload && upload.requestId) || "");
          started++;
          handedOff = true;
          bulkUploadInFlight.add(name);
        } catch (e) {
          if (e && e.busy) {
            // at the cap -- hold the card and back off before retrying this same clip.
            attempts++;
            await new Promise((r) => setTimeout(r, Math.min(4000, 700 + attempts * 300)));
          } else {
            pendingLinkCreations.delete(name);
            localState.delete(name);
            failed.push(name);
            handedOff = true;
          }
        }
      }
      bulkUploadQueued.delete(name);
      if (!handedOff) {
        // fell out on abort/cancel before the helper took it -- roll the card back to Create Link.
        pendingLinkCreations.delete(name);
        localState.delete(name);
      }
      rerenderCard(name);
      updateBulkUploadButton("running", done, total);
    }

    const aborted = bulkUploadAbort;
    if (aborted) {
      // clips still only queued (never handed to the helper) go back to Create Link.
      for (const name of Array.from(bulkUploadQueued)) {
        pendingLinkCreations.delete(name);
        localState.delete(name);
        rerenderCard(name);
      }
      // and cancel anything that slipped through to the helper between the Stop click and the loop breaking (the click handler cleared the set, so this catches late additions).
      for (const name of Array.from(bulkUploadInFlight)) {
        const job = lastUploadJobsByName.get(name);
        const pend = pendingLinkCreations.get(name);
        cancelActiveUpload(name, (job && job.requestId) || (pend && pend.requestId) || "", true);
      }
      bulkUploadInFlight.clear();
    }
    bulkUploadQueued.clear();
    bulkUploadRunning = false;
    bulkUploadAbort = false;
    anyUploading = localState.size > 0;
    if (bulkClearBtn) bulkClearBtn.disabled = false;
    updateBulkUploadButton("idle");
    scheduleNextPoll(POLL_FAST_MS);
    refreshBulkSelectionUi();
    if (started) {
      showToast("Upload: queued " + started + " selected clip" + (started === 1 ? "" : "s") + (aborted ? " before stopping." : "."), "info");
      scheduleToastHide(3500);
    }
    if (failed.length) {
      showToast("Retry the clip" + (failed.length === 1 ? "" : "s") + ".", "bad", failed.length + " selected clip" + (failed.length === 1 ? " was" : "s were") + " not queued for upload");
      scheduleToastHide(TOAST_ERROR_MS);
    }
  }

  function setCompressAllButton(state, info) {
    if (!compressAllBtn) return;
    compressAllBtn.classList.toggle("is-running", state === "running");
    compressAllBtn.classList.toggle("is-disabled", state === "disabled");
    if (state === "running" && info) {
      compressAllLabelEl.innerHTML = "Stop "
        + '<span class="progress">(' + info.done + " / " + info.total + ")</span>";
      compressAllBtn.title = "Click to stop scheduling new compresses. "
        + "In-flight encodes will finish.";
    } else if (state === "disabled") {
      compressAllLabelEl.textContent = "Compress All";
      compressAllBtn.title = "Every clip is already compressed.";
    } else {
      compressAllLabelEl.textContent = "Compress All";
      compressAllBtn.title = "Fast-compress every clip that isn't already compressed.";
    }
    if (!bulkCompressBtn) return;
    if (state === "running" && info) {
      bulkCompressBtn.classList.add("is-running");
      bulkCompressBtn.dataset.tooltip = "Stop compression queue (" + info.done + " / " + info.total + ")";
      bulkCompressBtn.setAttribute("aria-label", bulkCompressBtn.dataset.tooltip);
      bulkCompressBtn.disabled = false;
    } else {
      const available = selectedCompressCandidates().length;
      bulkCompressBtn.classList.remove("is-running");
      bulkCompressBtn.dataset.tooltip = available ? "Compress" : "No selected clips can be compressed";
      bulkCompressBtn.setAttribute("aria-label", bulkCompressBtn.dataset.tooltip);
      bulkCompressBtn.disabled = selectedClipNames.size > 0 && available === 0;
    }
  }

  function refreshCompressAllButton() {
    if (!compressAllBtn) return;
    if (compressAllRunning) return;
    const remaining = compressAllCandidates().length;
    if (remaining === 0) {
      setCompressAllButton("disabled");
    } else {
      setCompressAllButton("idle");
    }
  }

  function openCompressAllModal() {
    pendingBulkCompressQueue = null;
    const candidates = compressAllCandidates();
    const total = allClips.length;
    const skipped = total - candidates.length;
    compressAllBody.innerHTML =
      "<p>Fast-compress <b>" + candidates.length + "</b> clip"
        + (candidates.length === 1 ? "" : "s") + ".</p>"
      + (skipped > 0
          ? "<p><b>" + skipped + "</b> clip" + (skipped === 1 ? "" : "s")
            + " will be skipped (already compressed or busy).</p>"
          : "")
      + "<p>Each clip's original file will be replaced. "
        + "Only Fast mode runs in bulk &mdash; for Smaller, use a clip's "
        + "3-dot menu individually.</p>";
    compressAllGo.textContent = "Compress all";
    compressAllGo.disabled = (candidates.length === 0);
    compressAllVeil.classList.add("is-open");
  }
  function closeCompressAllModal() {
    compressAllVeil.classList.remove("is-open");
    pendingBulkCompressQueue = null;
    compressAllGo.textContent = "Compress all";
  }

  function openSelectedCompressModal() {
    const candidates = selectedCompressCandidates();
    if (!candidates.length) return;
    pendingBulkCompressQueue = candidates.slice();
    const skipped = selectedClipNames.size - candidates.length;
    compressAllBody.innerHTML =
      "<p>Fast-compress <b>" + candidates.length + "</b> selected clip" + (candidates.length === 1 ? "" : "s") + ".</p>"
      + (skipped > 0
          ? "<p><b>" + skipped + "</b> selected clip" + (skipped === 1 ? " was" : "s were") + " skipped because it is already compressed or busy.</p>"
          : "")
      + "<p>Each source clip is replaced with a smaller fast-compressed version. Clips are queued through ReplayKit's existing limiter, so they do not all encode at once.</p>";
    compressAllGo.textContent = "Compress selected";
    compressAllGo.disabled = false;
    compressAllVeil.classList.add("is-open");
  }

  // worker that drains the queue. we fire one request, wait for the response, then immediately fire the next. the servers get-uploadjobstartdecision returns busy=true when at the cap, so we back off briefly and retry the same clip. cpu-low bursts above the steady-state cap happen server-side -- we dont need to mirror the logic here, just keep firing.
  async function runCompressAll(queue, operationLabel) {
    compressAllRunning = true;
    compressAllAbort = false;
    const total = queue.length;
    let done = 0;
    setCompressAllButton("running", { done, total });

    while (queue.length > 0 && !compressAllAbort) {
      const name = queue[0];
      let started = false;
      let attempts = 0;
      while (!started && !compressAllAbort) {
        try {
          const res = await fetch(HELPER + "/compress-overwrite?file="
              + encodeURIComponent(name) + "&mode=fast&t=" + Date.now(),
              { method: "POST", cache: "no-store" });
          const data = await res.json().catch(() => ({}));
          if (res.ok && data.ok) {
            started = true;
            cancelledUploads.delete(name);
            markLocalState(name, "compressing");
            anyUploading = true;
            scheduleNextPoll(POLL_FAST_MS);
            rerenderCard(name);
          } else if (data && data.alreadyCompressed) {
            // race: marker appeared between candidate scan and now. skip silently.
            started = true;
          } else if (data && data.busy) {
            // server is at the (steady or burst) cap. short wait.
            attempts++;
            const wait = Math.min(3000, 600 + attempts * 200);
            await new Promise(r => setTimeout(r, wait));
          } else {
            // genuine error -- skip this clip, dont get stuck.
            started = true;
          }
        } catch (e) {
          attempts++;
          const wait = Math.min(5000, 800 + attempts * 400);
          await new Promise(r => setTimeout(r, wait));
          if (attempts > 8) started = true; // bail to avoid infinite loop
        }
      }
      queue.shift();
      done++;
      setCompressAllButton("running", { done, total });
    }

    compressAllRunning = false;
    compressAllAbort = false;
    refreshCompressAllButton();
    if (done > 0) {
      showToast((operationLabel || "Compress All") + ": scheduled " + done + " clip"
        + (done === 1 ? "" : "s") + " for compression.", "info");
      scheduleToastHide(4000);
    }
  }

  if (compressAllBtn) compressAllBtn.addEventListener("click", () => {
    if (compressAllBtn.classList.contains("is-disabled")) return;
    if (compressAllRunning) {
      // button doubles as a stop when running. aborting just stops scheduling new clips. without also cancelling the in-flight encodes, ffmpeg keeps burning the gpu/cpu on whatevers already running, which is what the user wanted to interrupt. walk localstate for anything currently in "compressing" and post cancel for each.
      compressAllAbort = true;
      const inflight = [];
      localState.forEach((s, name) => {
        if (s === "compressing") inflight.push(name);
      });
      inflight.forEach((name) => {
        fetch(HELPER + "/cancel-upload?file=" + encodeURIComponent(name)
              + "&t=" + Date.now(),
              { method: "POST", cache: "no-store" }).catch(() => {});
      });
      return;
    }
    openCompressAllModal();
  });
  compressAllCancel.addEventListener("click", closeCompressAllModal);
  compressAllVeil.addEventListener("mousedown", (e) => {
    if (e.target === compressAllVeil) closeCompressAllModal();
  });
  compressAllVeil.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { e.preventDefault(); closeCompressAllModal(); }
  });
  compressAllGo.addEventListener("click", () => {
    const queue = pendingBulkCompressQueue ? pendingBulkCompressQueue.slice() : compressAllCandidates();
    const label = pendingBulkCompressQueue ? "Compress selected" : "Compress All";
    closeCompressAllModal();
    if (queue.length === 0) return;
    runCompressAll(queue, label);
  });

  let bulkDeleteArmed = false;
  let bulkDeleteArmTimer = 0;
  function resetBulkDeleteButton() {
    bulkDeleteArmed = false;
    if (bulkDeleteArmTimer) { clearTimeout(bulkDeleteArmTimer); bulkDeleteArmTimer = 0; }
    if (!bulkDeleteBtn) return;
    bulkDeleteBtn.classList.remove("is-armed");
    bulkDeleteBtn.dataset.tooltip = "Delete";
    bulkDeleteBtn.setAttribute("aria-label", "Delete selected clips");
  }

  async function deleteSelectedClips() {
    const names = Array.from(selectedClipNames);
    if (!names.length) return;
    if (bulkDeleteBtn) bulkDeleteBtn.disabled = true;
    if (bulkClearBtn) bulkClearBtn.disabled = true;
    const deleted = [];
    const failed = [];
    let firstFailure = "";
    for (let i = 0; i < names.length; i++) {
      if (bulkDeleteBtn) {
        bulkDeleteBtn.dataset.tooltip = "Deleting " + (i + 1) + " / " + names.length;
        bulkDeleteBtn.setAttribute("aria-label", bulkDeleteBtn.dataset.tooltip);
      }
      try {
        const res = await fetch(HELPER + "/delete?file=" + encodeURIComponent(names[i]), {
          method: "POST", cache: "no-store",
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok || !data.ok) throw new Error(data.message || "Delete failed");
        deleted.push(names[i]);
      } catch (err) {
        failed.push(names[i]);
        if (!firstFailure) firstFailure = (err && err.message) || "";
      }
    }
    const deletedSet = new Set(deleted);
    deleted.forEach((name) => {
      selectedClipNames.delete(name);
      localState.delete(name);
      setFavorite(name, false);
      lastClipsByName.delete(name);
    });
    if (deletedSet.size) {
      allClips = allClips.filter((clip) => !deletedSet.has(clip.name));
      loadedClips = loadedClips.filter((clip) => !deletedSet.has(clip.name));
      resolvedFavoriteClips = resolvedFavoriteClips.filter((clip) => !deletedSet.has(clip.name));
      clipsTotal = Math.max(0, clipsTotal - deletedSet.size);
      clipsNextOffset = Math.min(clipsNextOffset, clipsTotal);
      renderLoadedClips(loadedClips);
      scheduleNextPoll(50);
    }
    if (bulkDeleteBtn) bulkDeleteBtn.disabled = false;
    if (bulkClearBtn) bulkClearBtn.disabled = false;
    resetBulkDeleteButton();
    refreshBulkSelectionUi();
    if (deleted.length) {
      showToast("Deleted " + deleted.length + " selected clip" + (deleted.length === 1 ? "" : "s") + ".", "info");
      scheduleToastHide(3000);
    }
    if (failed.length) {
      showToast(escapeHtml(firstFailure), "bad", failed.length + " clip" + (failed.length === 1 ? " was" : "s were") + " not deleted");
      scheduleToastHide(TOAST_ERROR_MS);
    }
  }

  if (bulkCompressBtn) {
    bulkCompressBtn.addEventListener("click", () => {
      if (compressAllRunning) {
        compressAllAbort = true;
        return;
      }
      const queue = selectedCompressCandidates();
      if (!queue.length) return;
      openSelectedCompressModal();
    });
  }
  if (bulkUploadBtn) {
    bulkUploadBtn.addEventListener("click", () => {
      // in stop mode (scheduling, or the batch still uploading) the icon cancels the whole batch, same as the text button.
      if (bulkUploadRunning || bulkUploadInFlight.size > 0) {
        stopBulkUploads();
        return;
      }
      const queue = selectedUploadCandidates();
      if (!queue.length) return;
      runSelectedUploads(queue);
    });
  }
  if (bulkUploadStopBtn) {
    bulkUploadStopBtn.addEventListener("click", () => {
      if (bulkUploadStopBtn.hidden || bulkUploadStopBtn.disabled) return;
      stopBulkUploads();
    });
  }
  if (bulkDeleteBtn) {
    bulkDeleteBtn.addEventListener("click", () => {
      if (!selectedClipNames.size) return;
      if (!bulkDeleteArmed) {
        bulkDeleteArmed = true;
        bulkDeleteBtn.classList.add("is-armed");
        bulkDeleteBtn.dataset.tooltip = "Confirm delete (" + selectedClipNames.size + ")";
        bulkDeleteBtn.setAttribute("aria-label", bulkDeleteBtn.dataset.tooltip);
        bulkDeleteArmTimer = setTimeout(resetBulkDeleteButton, 3000);
        return;
      }
      resetBulkDeleteButton();
      deleteSelectedClips();
    });
  }
  if (bulkClearBtn) bulkClearBtn.addEventListener("click", clearClipSelection);

  // tooltips come from the shared _tooltip partial; the page-specific rule is that a trigger goes quiet while its own menu is open, since the panel already says what it is -- the 3-dot tip used to sit on top of the menus first row and hide it
  if (window.RKTooltip) {
    RKTooltip.suppressWhen((btn) => (btn === sortButton && sortWrap && sortWrap.classList.contains("is-open")) || (btn.classList.contains("menu-btn") && !!btn.closest(".card.menu-open")));
  }

  // keep the buttons enabled/disabled state in sync with the current clip list every render.
  const _origRender = render;
  render = function (clips) {
    const r = _origRender.apply(this, arguments);
    refreshCompressAllButton();
    refreshBulkSelectionUi();
    return r;
  };

  const trimVeil         = document.getElementById("trim-veil");
  const trimProcessingVeil   = document.getElementById("trim-processing-veil");
  const saveVeil         = document.getElementById("save-veil");
  let trimSaving = null;
  let editorLoad = null;
  let editorRequest = 0;
  const editorSessionId = crypto.randomUUID();
  let editorSessionRegistered = false, editorSessionRevision = 0;
  let editorSessionWrites = Promise.resolve();
  function editorSessionBody(action, names) {
    return { view:HOME_MODE ? "home" : "clips", id:editorSessionId, revision:++editorSessionRevision, action, names };
  }
  async function sendEditorSession(body, keepalive) {
    const res = await fetch(HELPER + "/editor/session", { method:"POST", cache:"no-store", keepalive:!!keepalive,
      headers:{ "Content-Type":"application/json" }, body:JSON.stringify(body) });
    const data = await res.json();
    if (!res.ok || !data.ok) throw new Error(data.message || "The editor clip could not be protected.");
  }
  function protectEditorSources(names) {
    const body = editorSessionBody("add", names);
    editorSessionWrites = editorSessionWrites.catch(() => {}).then(async () => {
      if (!editorSessionRegistered) {
        await sendEditorSession({ view:body.view, id:editorSessionId, action:"register" });
        editorSessionRegistered = true;
      }
      await sendEditorSession(body);
    });
    return editorSessionWrites;
  }
  function releaseEditorSources() {
    const body = editorSessionBody("clear");
    editorSessionWrites = editorSessionWrites.catch(() => {}).then(() => editorSessionRegistered ? sendEditorSession(body, true) : undefined);
    editorSessionWrites.catch((err) => showToast(escapeHtml(err.message), "bad", "Editor"));
  }
  window.addEventListener("pagehide", () => {
    if (editorSessionRegistered) navigator.sendBeacon(HELPER + "/editor/session", new Blob([JSON.stringify(editorSessionBody("clear"))], { type:"application/json" }));
  });
  // the signal every listener and timer of the editor hangs off, and the style and script tags the loader put in the head
  let editorScope = null;
  let editorNodes = [];
  let editorUsedAt = 0;
  const EDITOR_IDLE_MS = 30000;

  function editorApi() { return window.__replaykitEditorReady ? window.__rkEditorApi : null; }

  function loadEditor() {
    if (editorLoad) return editorLoad;
    editorLoad = fetch(HELPER + "/editor-view", { cache: "no-store" }).then(async (res) => {
      if (!res.ok) throw new Error("Editor resources could not be loaded");
      const doc = new DOMParser().parseFromString(await res.text(), "text/html");
      const editor = doc.getElementById("trim-veil"), saving = doc.getElementById("save-veil");
      if (!editor || !saving || !doc.querySelector("script")) throw new Error("Editor resources are incomplete");
      if (doc.querySelector("script[src]")) throw new Error("Unexpected editor resource");
      editorScope = new AbortController();
      window.__rkEditorScope = { signal: editorScope.signal };
      trimVeil.replaceChildren(...Array.from(editor.childNodes));
      saveVeil.replaceChildren(...Array.from(saving.childNodes));
      // the icons of the editor come after the ones of the page were filled, so they are filled the same way here
      for (const el of [...trimVeil.querySelectorAll("svg[data-i]"), ...saveVeil.querySelectorAll("svg[data-i]")]) el.innerHTML = _svgInner(_RKI[el.dataset.i]);
      for (const node of doc.querySelectorAll("style, script")) {
        const copy = document.createElement(node.localName);
        copy.textContent = node.textContent;
        document.head.appendChild(copy);
        editorNodes.push(copy);
      }
      if (!editorApi()) throw new Error("Editor initialization failed");
      applyClipHovers(trimVeil);
      applyClipHovers(saveVeil);
      editorUsedAt = Date.now();
    }).catch((err) => {
      // whatever got onto the page is taken off again, so the next edit starts from a clean page
      unloadEditor();
      throw err;
    });
    return editorLoad;
  }

  // the editor is taken off the page whole: its parts let go of their media, the signal takes every listener and timer it hung on the window, the document and the veils, and with its markup, its tags and its globals gone nothing reaches it any more, so the collector frees all of it; the next edit loads it again
  function unloadEditor() {
    const api = window.__rkEditorApi;
    try { if (api) api.release(); } catch {}
    if (editorScope) editorScope.abort();
    for (const media of [...trimVeil.querySelectorAll("video, audio"), ...saveVeil.querySelectorAll("video, audio")]) {
      if (!media.getAttribute("src")) continue;
      try { media.pause(); media.removeAttribute("src"); media.load(); } catch {}
    }
    for (const canvas of [...trimVeil.querySelectorAll("canvas"), ...saveVeil.querySelectorAll("canvas")]) { canvas.width = 0; canvas.height = 0; }
    trimVeil.replaceChildren();
    saveVeil.replaceChildren();
    editorNodes.forEach((node) => node.remove());
    editorNodes = [];
    editorScope = null;
    for (const name of ["__rkEditorApi", "__rkEditorScope", "RKEditorFx", "RKEditorLanes", "RKEditorTools"]) delete window[name];
    window.__replaykitEditorReady = false;
    editorLoad = null;
  }

  function editorInUse() {
    return !!trimSaving || editorHeldAway || trimVeil.classList.contains("is-open") || trimProcessingVeil.classList.contains("is-open") || saveVeil.classList.contains("is-open");
  }

  // a closed editor that has gone unused for EDITOR_IDLE_MS is unloaded, while the page itself stays
  setInterval(() => {
    if (!editorApi()) return;
    if (editorInUse()) { editorUsedAt = Date.now(); return; }
    if (Date.now() - editorUsedAt >= EDITOR_IDLE_MS) unloadEditor();
  }, 5000);

  // what the quick action menu under the logo offers for this page: the editor while it is open, else the clip folder. the helper keeps the list for the plugin, and it is only sent again when it changed
  const QUICK_VIEW = HOME_MODE ? "home" : "clips";
  let quickSent = "";
  function publishQuickActions() {
    const api = editorApi();
    const editing = !!api && trimVeil.classList.contains("is-open");
    const actions = editing ? api.quickActions() : [{ id:"open-folder", label:"Open clips folder", icon:"action/folder-open" }];
    const body = JSON.stringify({ actions });
    if (body === quickSent) return;
    quickSent = body;
    fetch(HELPER + "/quick-actions/state?view=" + QUICK_VIEW, { method:"POST", cache:"no-store", headers:{ "Content-Type":"application/json" }, body }).catch(() => { quickSent = ""; });
  }
  window.__rkPublishQuickActions = publishQuickActions;
  window.__rkQuickAction = function (id) {
    const api = editorApi();
    if (api && trimVeil.classList.contains("is-open")) { api.quickAction(id); return; }
    if (id === "open-folder") fetch(HELPER + "/open-clip-folder", { method:"POST", cache:"no-store" }).catch(() => {});
  };
  // the editor opening or closing changes the list
  new MutationObserver(publishQuickActions).observe(trimVeil, { attributes:true, attributeFilter:["class"] });
  publishQuickActions();

  // the ways out of the editor that this page has, whether it is loaded or still on its way
  function closeEditor() {
    cancelEditorLoading();
    const api = editorApi();
    return api ? api.close() : true;
  }

  function cancelEditorLoading() {
    ++editorRequest;
    if (!window.__replaykitEditorReady) {
      trimProcessingVeil.classList.remove("is-open");
      releaseEditorSources();
    }
  }

  async function openEditor(clip) {
    const request = ++editorRequest;
    trimProcessingVeil.classList.add("is-open");
    document.getElementById("trim-processing-stage").textContent = "Preparing the editor";
    try {
      await protectEditorSources([clip.name]);
      if (request !== editorRequest) return;
      await loadEditor();
      if (request !== editorRequest) return;
      const blocked = clipBusyWith(clip.name);
      if (blocked) { trimProcessingVeil.classList.remove("is-open"); releaseEditorSources(); showToast(blocked, "bad", "Editor"); return; }
      editorApi().open(clip);
    } catch (err) {
      if (request !== editorRequest) return;
      trimProcessingVeil.classList.remove("is-open");
      releaseEditorSources();
      showToast(escapeHtml(err.message || "The editor could not be loaded. Reopen ReplayKit and try again."), "bad", "Editor");
    }
  }

  document.getElementById("trim-processing-cancel").addEventListener("click", () => {
    if (!window.__replaykitEditorReady) cancelEditorLoading();
  });
  trimProcessingVeil.addEventListener("mousedown", (e) => {
    if (!window.__replaykitEditorReady && e.target === trimProcessingVeil) cancelEditorLoading();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Escape" || window.__replaykitEditorReady || !trimProcessingVeil.classList.contains("is-open")) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    cancelEditorLoading();
  }, true);

  // Keep client-owned work. Accepted helper jobs continue without this page and restore their status when it opens again.
  window.__rkCanUnload = function () {
    if (trimSaving || editorHeldAway) return false;
    if (trimVeil.classList.contains("is-open") || trimProcessingVeil.classList.contains("is-open") || document.querySelector(".modal-veil.is-open")) return false;
    if (compressAllRunning || bulkUploadRunning || bulkUploadQueued.size > 0) return false;
    if (linkTransitionBusy.size > 0 || inFlightCancels.size > 0) return false;
    return true;
  };

  hydrateClipUiState();
  refreshDrafts();
  if (screenshotGallery) screenshotGallery.refresh();
  // the caps gate Create Link, so fetch them before the first paint and repaint once they land; re-fetched on re-show because signing in or out between sessions changes what Streamable will accept.
  refreshUploadLimits().then(() => queueVirtualRender(true));
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") refreshUploadLimits().then(() => queueVirtualRender(true));
  });
  if (HOME_MODE && typeof ResizeObserver === "function") {
    let homeWidth = scrollAreaEl.clientWidth;
    new ResizeObserver(() => {
      if (scrollAreaEl.clientWidth === homeWidth) return;
      homeWidth = scrollAreaEl.clientWidth;
      // a row holds as many cards as the width has room for, so a new width draws the shelves again, thru the guards of the fullscreen and the theater
      if (homeRenderedOnce) renderLoadedClips(loadedClips);
    }).observe(scrollAreaEl);
  }
  if (HOME_MODE && window.RKHomeDashboard) {
    RKHomeDashboard.mount(document.getElementById("hm-root"));
    // the shell page holds back the first reveal of this view until the summary, the first clips and the favorites are in; a favorites list that never answers must not hold it for good
    setTimeout(resolveFirstRender, 3000);
    window.__rkReady = Promise.all([RKHomeDashboard.ready, firstRender]);
  }
  poll();
