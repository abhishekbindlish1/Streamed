(function () {
  'use strict';

  var CATALOG_URL = 'data/live-demo.json';
  var DEMO_MODE = !!(window.STREAMED_CONFIG && window.STREAMED_CONFIG.DEMO_MODE);
  var USE_INTERNAL_API = !DEMO_MODE;
  var demoNotice = document.getElementById('demoNotice');
  if (demoNotice) demoNotice.hidden = !DEMO_MODE;
  var PROFILE_STORAGE_KEY = 'STREAMED_PROFILES';
  var ACTIVE_PROFILE_KEY = 'STREAMED_ACTIVE_PROFILE';
  var TEAM_CATALOG_CACHE_KEY = 'STREAMED_TEAM_CATALOG_CACHE';
  var AVATAR_STYLES = ['avataaars', 'adventurer', 'lorelei', 'micah', 'personas', 'notionists', 'pixel-art', 'bottts'];
  var AVATAR_BACKGROUNDS = ['b6e3f4', 'c0aede', 'ffd5dc', 'ffdfbf', 'd1d4f9', 'c7f9cc', 'fef3c7', 'fbcfe8'];
  var IS_WEBOS_RUNTIME = !!(window.PalmSystem || window.webOSSystem) || /Web[0O]S|NetCast/i.test(navigator.userAgent || '');
  var IS_TV_RUNTIME = IS_WEBOS_RUNTIME || /Tizen|SMART-TV|SmartTV|SamsungBrowser|TV/i.test(navigator.userAgent || '');
  document.documentElement.classList.toggle('tv-mode', IS_TV_RUNTIME);

  var state = {
    sports: [],
    matches: [],
    profiles: loadProfiles(),
    activeProfileId: '',
    profileOpen: true,
    profileManageMode: false,
    avatarPickerProfileId: '',
    catalogLoaded: false,
    activeSport: 'for-you',
    matchCacheVersion: 0,
    matchesBySportCache: {},
    sportCountsCache: null,
    matchesById: {},
    focusables: [],
    focusedIndex: 0,
    focusedKey: '',
    focusRevealFrame: null,
    focusRevealNode: null,
    favourites: [],
    leagueFavourites: [],
    teamFavourites: [],
    recentSearches: [],
    localTeamCatalog: [],
    teamBackfill: [],
    teamStandings: {},
    teamStandingsLoading: {},
    expandedLeagues: [],
    refreshTimer: null,
    refreshMatchId: '',
    refreshLoading: false,
    selectedMatchId: '',
    heroMatchId: '',
    autoPlayTimer: null,
    heroRenderTimer: null,
    navSelectTimer: null,
    previewMatchId: '',
    playbackMatchId: '',
    playbackStreams: [],
    playbackStreamIndex: 0,
    embeddedPlaybackTimers: [],
    streamResolutionCache: {},
    streamResolutionPending: {},
    playbackSwitchTimer: null,
    playbackStartAttemptTimers: [],
    playbackPausedByUser: false,
    playerFocusTimer: null,
    playerEngine: '',
    playerHls: null,
    avplayReady: false,
    avplayBuffering: false,
    avplaySession: 0,
    embedPlaybackPaused: false,
    previewVolume: 0.55,
    previewMuted: true,
    previewHls: null,
    userInteracted: false,
    userScrolledContent: false,
    catalogSource: 'local',
    searchOpen: false,
    teamHubOpen: false,
    streamPickerOpen: false,
    streamPickerMatchId: '',
    streamPickerSelectedStreamKey: '',
    streamPickerLoading: false,
    teamHubSearchQuery: '',
    searchQuery: '',
    navMoreOpen: false
  };

  var els = {
    sportsList: document.getElementById('sportsList'),
    matchesGrid: document.getElementById('matchesGrid'),
    content: document.querySelector('.content'),
    heroFeature: document.getElementById('heroFeature'),
    statusText: document.getElementById('statusText'),
    activeSportLabel: document.getElementById('activeSportLabel'),
    pageTitle: document.getElementById('pageTitle'),
    clock: document.getElementById('clock'),
    playerView: document.getElementById('playerView'),
    avplayPlayer: document.getElementById('avplayPlayer'),
    videoPlayer: document.getElementById('videoPlayer'),
    embedPlayer: document.getElementById('embedPlayer'),
    backButton: document.getElementById('backButton'),
    playerControls: document.getElementById('playerControls'),
    playerPlayButton: document.getElementById('playerPlayButton'),
    playerPrevButton: document.getElementById('playerPrevButton'),
    playerNextButton: document.getElementById('playerNextButton'),
    playerStreamLabel: document.getElementById('playerStreamLabel'),
    playerLeague: document.getElementById('playerLeague'),
    playerTitle: document.getElementById('playerTitle'),
    profileView: document.getElementById('profileView'),
    profileTitle: document.getElementById('profileTitle'),
    profileList: document.getElementById('profileList'),
    profileAvatarPicker: document.getElementById('profileAvatarPicker'),
    profileManageButton: document.getElementById('profileManageButton'),
    profileDoneButton: document.getElementById('profileDoneButton'),
    profileButton: document.getElementById('profileButton'),
    searchButton: document.getElementById('searchButton'),
    teamHubOverlay: document.getElementById('teamHubOverlay'),
    teamHubCloseButton: document.getElementById('teamHubCloseButton'),
    teamHubBody: document.getElementById('teamHubBody'),
    streamPickerOverlay: document.getElementById('streamPickerOverlay'),
    streamPickerCloseButton: document.getElementById('streamPickerCloseButton'),
    streamPickerLeague: document.getElementById('streamPickerLeague'),
    streamPickerTitle: document.getElementById('streamPickerTitle'),
    streamPickerBody: document.getElementById('streamPickerBody'),
    searchOverlay: document.getElementById('searchOverlay'),
    searchCloseButton: document.getElementById('searchCloseButton'),
    searchInput: document.getElementById('searchInput'),
    searchKeyboard: document.getElementById('searchKeyboard'),
    searchResults: document.getElementById('searchResults')
  };

  function setStatus(message) {
    els.statusText.textContent = message;
  }

  function reindexMatches() {
    var byId = {};
    state.matches.forEach(function (match) {
      if (match && match.id) byId[match.id] = match;
    });
    state.matchesById = byId;
  }

  function normalizeMatches(payload) {
    return payload.matches.map(normalizeLocalMatch);
  }

  function normalizeLocalMatch(match) {
    return {
      id: match.id,
      sport: match.sport,
      competition: match.competition,
      title: match.title,
      startTime: match.startTime,
      status: match.status || 'scheduled',
      streams: match.streams || [],
      streamSources: match.streamSources || [],
      poster: match.poster || '',
      posterUrl: match.posterUrl || getPosterUrl(match),
      popular: !!match.popular,
      teams: getTeamsWithBadgeUrls(match.teams),
      scoreText: match.scoreText || '',
      resultText: match.resultText || '',
      venue: match.venue || '',
      source: match.source || 'local'
    };
  }

  function mapApiMatch(match, status) {
    return normalizeLocalMatch({
      id: match.id,
      sport: match.category,
      competition: match.category,
      title: match.title,
      startTime: match.date,
      status: status || inferStatus(match.date),
      streams: [],
      streamSources: match.sources || [],
      poster: match.poster || '',
      posterUrl: match.posterUrl || getPosterUrl(match),
      popular: !!match.popular,
      teams: getTeamsWithBadgeUrls(match.teams),
      source: 'internal-api'
    });
  }

  function getPosterUrl(match) {
    if (!window.StreamedAPI) return match.poster || '';
    if (match.poster && /^https?:\/\//i.test(String(match.poster))) return match.poster;
    if (match.poster && String(match.poster).indexOf('/api/images/') === 0) return window.StreamedAPI.buildUrl(match.poster);
    if (match.poster) return window.StreamedAPI.getProxiedImageUrl(match.poster);
    var homeBadge = match.teams && match.teams.home && match.teams.home.badge;
    var awayBadge = match.teams && match.teams.away && match.teams.away.badge;
    if (homeBadge && awayBadge) return window.StreamedAPI.getMatchPosterUrl(homeBadge, awayBadge);
    return '';
  }

  function getTeamsWithBadgeUrls(teams) {
    if (!teams || !window.StreamedAPI) return teams || null;
    return {
      home: teams.home ? {
        name: teams.home.name,
        badge: teams.home.badge,
        badgeUrl: window.StreamedAPI.getTeamBadgeUrl(teams.home.badge)
      } : undefined,
      away: teams.away ? {
        name: teams.away.name,
        badge: teams.away.badge,
        badgeUrl: window.StreamedAPI.getTeamBadgeUrl(teams.away.badge)
      } : undefined
    };
  }

  function isAbsoluteUrl(value) {
    return /^https?:\/\//i.test(String(value || ''));
  }

  function inferStatus(date) {
    var now = Date.now();
    var start = Number(date) || now;
    if (Math.abs(now - start) < 3 * 60 * 60 * 1000) return 'live';
    if (start < now) return 'recent';
    return 'scheduled';
  }

  function loadCatalog() {
    return loadInternalCatalog()
      .catch(function () { return loadLocalCatalog(); })
      .then(function () {
        state.catalogLoaded = true;
        render();
        setStatus('Use arrows and OK to choose a match.');
      })
      .catch(function (error) {
        renderError(error.message);
      });
  }

  function loadInternalCatalog() {
    if (!USE_INTERNAL_API || !window.StreamedAPI) return Promise.reject(new Error('Internal API client unavailable.'));

    return Promise.all([
      window.StreamedAPI.getSports().catch(function () { return []; }),
      window.StreamedAPI.getLiveMatches().catch(function () { return []; }),
      window.StreamedAPI.getTodaysMatches().catch(function () { return []; }),
      window.StreamedAPI.getAllPopularMatches().catch(function () { return []; })
    ]).then(function (groups) {
      var apiSports = normalizeApiSports(groups[0]);
      var matches = dedupeApiMatches(
        groups[1].map(function (match) { return mapApiMatch(match, 'live'); })
          .concat(groups[2].map(function (match) { return mapApiMatch(match); }))
          .concat(groups[3].map(function (match) { return mapApiMatch(match); }))
      );

      if (!matches.length) throw new Error('Internal API returned no matches.');
      state.matches = matches;
      reindexMatches();
      state.sports = orderSports(filterSportsWithMatches(apiSports.length ? apiSports : buildSportsFromMatches(matches), matches));
      refreshTeamCatalog();
      loadTeamHubBackfill();
      state.activeSport = getDefaultSport();
      bumpMatchCache();
      state.catalogSource = 'internal-api';
      setStatus('Loaded matches from internal backend.');
    });
  }

  function loadLocalCatalog() {
    // Packaged TV apps use file:// origins; preload their sample catalog instead
    // of depending on Fetch accepting that protocol. Live API mode ignores it.
    var localCatalog = DEMO_MODE && window.STREAMED_DEMO_CATALOG
      ? Promise.resolve(window.STREAMED_DEMO_CATALOG)
      : fetch(CATALOG_URL)
      .then(function (res) {
        if (!res.ok) throw new Error('Could not load match catalog.');
        return res.json();
      });
    return localCatalog
      .then(function (payload) {
        state.sports = orderSports(payload.sports);
        state.matches = normalizeMatches(payload);
        reindexMatches();
        refreshTeamCatalog();
        loadTeamHubBackfill();
        state.activeSport = getDefaultSport();
        bumpMatchCache();
        state.catalogSource = 'local';
        setStatus('Loaded local demo catalog.');
      });
  }

  function dedupeApiMatches(matches) {
    var byId = {};
    matches.forEach(function (match) {
      if (!byId[match.id]) byId[match.id] = match;
      if (match.status === 'live') byId[match.id].status = 'live';
      if (match.popular) byId[match.id].popular = true;
    });
    return Object.keys(byId).map(function (id) { return byId[id]; });
  }

  function normalizeApiSports(sports) {
    return (sports || []).filter(function (sport) {
      return sport && sport.id;
    }).map(function (sport) {
      return { id: sport.id, name: sport.name || toTitleCase(sport.id) };
    });
  }

  function filterSportsWithMatches(sports, matches) {
    var counts = {};
    matches.forEach(function (match) { counts[match.sport] = true; });
    return sports.filter(function (sport) { return counts[sport.id]; });
  }

  function buildSportsFromMatches(matches) {
    var seen = {};
    return matches.reduce(function (sports, match) {
      if (!match.sport || seen[match.sport]) return sports;
      seen[match.sport] = true;
      sports.push({ id: match.sport, name: toTitleCase(match.sport) });
      return sports;
    }, []);
  }

  function getDefaultSport() {
    return getNavSports()[0] ? getNavSports()[0].id : 'for-you';
  }

  function toTitleCase(value) {
    return String(value || '').replace(/[-_]/g, ' ').replace(/\b\w/g, function (letter) { return letter.toUpperCase(); });
  }

  function orderSports(sports) {
    return sports.slice().sort(function (a, b) {
      if (a.id === 'cricket') return -1;
      if (b.id === 'cricket') return 1;
      return 0;
    });
  }

  function safeParseArray(key) {
    try {
      var value = JSON.parse(window.localStorage.getItem(key) || '[]');
      return Array.isArray(value) ? value : [];
    } catch (error) {
      return [];
    }
  }

  function safeParseObject(key) {
    try {
      var value = JSON.parse(window.localStorage.getItem(key) || '{}');
      return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
    } catch (error) {
      return {};
    }
  }

  function loadProfiles() {
    var profiles = safeParseArray(PROFILE_STORAGE_KEY);
    if (profiles.length) {
      profiles = profiles.map(normalizeProfile);
      window.localStorage.setItem(PROFILE_STORAGE_KEY, JSON.stringify(profiles));
      return profiles;
    }
    profiles = [
      normalizeProfile({ id: 'abin', name: 'Abin', initial: 'A', accent: '#e50914', avatarPreset: 0 }),
      normalizeProfile({ id: 'guest', name: 'Guest', initial: 'G', accent: '#1264a3', avatarPreset: 2 }),
      normalizeProfile({ id: 'family', name: 'Family', initial: 'F', accent: '#46d369', avatarPreset: 4 })
    ];
    window.localStorage.setItem(PROFILE_STORAGE_KEY, JSON.stringify(profiles));
    return profiles;
  }

  function normalizeProfile(profile) {
    var name = profile.name || 'Profile';
    var id = profile.id || ('profile-' + Date.now());
    var preset = typeof profile.avatarPreset === 'number' ? profile.avatarPreset : 0;
    return {
      id: id,
      name: name,
      initial: profile.initial || name.charAt(0) || 'P',
      accent: profile.accent || '#e50914',
      avatarPreset: preset,
      avatarUrl: profile.avatarUrl || getAvatarPresetUrl({ id: id, name: name }, preset)
    };
  }

  function saveProfiles() {
    window.localStorage.setItem(PROFILE_STORAGE_KEY, JSON.stringify(state.profiles));
  }

  function getProfilePrefsKey(profileId) {
    return 'STREAMED_PROFILE_PREFS_' + profileId;
  }

  function getDefaultProfilePreferences() {
    return {
      favourites: [],
      leagueFavourites: [],
      teamFavourites: [],
      recentSearches: [],
      activeSport: 'cricket',
      previewMuted: true
    };
  }

  function loadProfilePreferences(profileId) {
    var defaults = getDefaultProfilePreferences();
    var saved = safeParseObject(getProfilePrefsKey(profileId));
    if (!Object.keys(saved).length && profileId === 'abin') {
      saved = {
        favourites: safeParseArray('STREAMED_FAVOURITES'),
        leagueFavourites: safeParseArray('STREAMED_LEAGUE_FAVOURITES'),
        teamFavourites: safeParseArray('STREAMED_TEAM_FAVOURITES'),
        recentSearches: safeParseArray('STREAMED_RECENT_SEARCHES'),
        activeSport: window.localStorage.getItem('STREAMED_ACTIVE_SPORT') || defaults.activeSport
      };
    }
    return {
      favourites: Array.isArray(saved.favourites) ? saved.favourites : defaults.favourites,
      leagueFavourites: Array.isArray(saved.leagueFavourites) ? saved.leagueFavourites : defaults.leagueFavourites,
      teamFavourites: Array.isArray(saved.teamFavourites) ? saved.teamFavourites : defaults.teamFavourites,
      recentSearches: Array.isArray(saved.recentSearches) ? saved.recentSearches : defaults.recentSearches,
      activeSport: saved.activeSport || defaults.activeSport,
      previewMuted: typeof saved.previewMuted === 'boolean' ? saved.previewMuted : defaults.previewMuted
    };
  }

  function saveProfilePreferences() {
    if (!state.activeProfileId) return;
    window.localStorage.setItem(getProfilePrefsKey(state.activeProfileId), JSON.stringify({
      favourites: state.favourites,
      leagueFavourites: state.leagueFavourites,
      teamFavourites: state.teamFavourites,
      recentSearches: state.recentSearches,
      activeSport: state.activeSport,
      previewMuted: state.previewMuted
    }));
  }

  function loadFavourites() {
    return state.activeProfileId ? loadProfilePreferences(state.activeProfileId).favourites : [];
  }

  function loadLeagueFavourites() {
    return state.activeProfileId ? loadProfilePreferences(state.activeProfileId).leagueFavourites : [];
  }

  function loadTeamFavourites() {
    return state.activeProfileId ? loadProfilePreferences(state.activeProfileId).teamFavourites : [];
  }

  function loadRecentSearches() {
    return state.activeProfileId ? loadProfilePreferences(state.activeProfileId).recentSearches : [];
  }

  function renderProfiles() {
    if (!els.profileList) return;
    var lastProfileId = window.localStorage.getItem(ACTIVE_PROFILE_KEY) || '';
    els.profileTitle.textContent = state.profileManageMode ? 'Manage Profiles' : "Who's watching?";
    els.profileManageButton.hidden = state.profileManageMode;
    els.profileDoneButton.hidden = !state.profileManageMode;
    els.profileList.className = 'profile-list' + (state.profileManageMode ? ' managing' : '');
    els.profileList.innerHTML = '';
    state.profiles.forEach(function (profile) {
      els.profileList.appendChild(state.profileManageMode ? createManageProfileCard(profile) : createProfileCard(profile, profile.id === lastProfileId));
    });
    els.profileList.appendChild(createAddProfileCard());
    renderAvatarPicker();
  }

  function createProfileCard(profile, recent) {
    var button = document.createElement('button');
    button.className = 'profile-card' + (recent ? ' recent' : '');
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.profileId = profile.id;
    button.innerHTML = [
      renderProfileAvatar(profile),
      '<strong>' + escapeHtml(profile.name) + '</strong>',
      recent ? '<small>Last used</small>' : ''
    ].join('');
    button.addEventListener('click', function () { selectProfile(profile.id); });
    return button;
  }

  function createManageProfileCard(profile) {
    var card = document.createElement('div');
    card.className = 'profile-card manage-profile-card';
    card.dataset.profileId = profile.id;
    card.innerHTML = [
      renderProfileAvatar(profile),
      '<strong>' + escapeHtml(profile.name) + '</strong>',
      '<small>Edit profile</small>',
      '<div class="profile-card-actions"></div>'
    ].join('');
    var actions = card.querySelector('.profile-card-actions');
    actions.appendChild(createProfileEditButton('Rename', function () { renameProfile(profile.id); }));
    actions.appendChild(createProfileEditButton('Profile image', function () { openAvatarPicker(profile.id); }));
    return card;
  }

  function createProfileEditButton(label, handler) {
    var button = document.createElement('button');
    button.className = 'profile-edit-button';
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.textContent = label;
    button.addEventListener('click', handler);
    return button;
  }

  function createAddProfileCard() {
    var button = document.createElement('button');
    button.className = 'profile-card add-profile-card';
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.innerHTML = '<span class="profile-avatar profile-avatar-empty">+</span><strong>Add profile</strong>' + (state.profileManageMode ? '<small>Create another profile</small>' : '');
    button.addEventListener('click', addProfile);
    return button;
  }

  function addProfile() {
    var number = state.profiles.length + 1;
    var profile = {
      id: 'profile-' + Date.now(),
      name: 'Profile ' + number,
      initial: String(number),
      accent: ['#e50914', '#1264a3', '#46d369', '#f5c451'][number % 4],
      avatarPreset: number % AVATAR_STYLES.length
    };
    profile.avatarUrl = '';
    state.profiles.push(profile);
    saveProfiles();
    renderProfiles();
    collectFocusables(true);
    var target = els.profileList.querySelector('[data-profile-id="' + profile.id + '"]');
    if (target) {
      var nested = target.querySelector && target.querySelector('[data-focusable="true"]');
      focusNode(nested || target);
    }
  }

  function renameProfile(profileId) {
    var profile = getProfileById(profileId);
    if (!profile) return;
    var value = window.prompt('Rename profile', profile.name);
    value = String(value || '').trim();
    if (!value) return;
    profile.name = value;
    profile.initial = value.charAt(0).toUpperCase();
    saveProfiles();
    renderProfiles();
    collectFocusables(true);
  }

  function openAvatarPicker(profileId) {
    state.avatarPickerProfileId = profileId;
    renderProfiles();
    collectFocusables(true);
    var first = els.profileAvatarPicker.querySelector('[data-focusable="true"]');
    if (first) focusNode(first);
  }

  function renderAvatarPicker() {
    if (!els.profileAvatarPicker) return;
    var profile = getProfileById(state.avatarPickerProfileId);
    els.profileAvatarPicker.hidden = !state.profileManageMode || !profile;
    els.profileAvatarPicker.innerHTML = '';
    if (!state.profileManageMode || !profile) return;
    var heading = document.createElement('div');
    heading.className = 'avatar-picker-heading';
    heading.innerHTML = '<strong>Select profile image</strong><span>' + escapeHtml(profile.name) + '</span>';
    els.profileAvatarPicker.appendChild(heading);
    var grid = document.createElement('div');
    grid.className = 'avatar-picker-grid';
    AVATAR_STYLES.forEach(function (style, index) {
      var button = document.createElement('button');
      button.className = 'avatar-choice' + (profile.avatarPreset === index ? ' active' : '');
      button.type = 'button';
      button.dataset.focusable = 'true';
      button.innerHTML = renderAvatarMarkup(profile, index, 'avatar-choice-preview') + '<span>' + escapeHtml(style) + '</span>';
      button.addEventListener('click', function () { selectAvatar(profile.id, index); });
      grid.appendChild(button);
    });
    els.profileAvatarPicker.appendChild(grid);
  }

  function selectAvatar(profileId, presetIndex) {
    var profile = getProfileById(profileId);
    if (!profile) return;
    profile.avatarPreset = presetIndex;
    profile.avatarUrl = '';
    saveProfiles();
    if (state.activeProfileId === profile.id) updateProfileBadge(profile);
    renderProfiles();
    collectFocusables(true);
  }

  function toggleProfileManageMode(enabled) {
    state.profileManageMode = enabled;
    if (!enabled) state.avatarPickerProfileId = '';
    renderProfiles();
    collectFocusables(true);
    var target = enabled ? els.profileList.querySelector('[data-focusable="true"]') : els.profileManageButton;
    if (target) focusNode(target);
  }

  function getProfileById(profileId) {
    return state.profiles.filter(function (profile) { return profile.id === profileId; })[0] || null;
  }

  function renderProfileAvatar(profile) {
    return renderAvatarMarkup(profile, profile.avatarPreset || 0, 'profile-avatar');
  }

  function renderAvatarMarkup(profile, presetIndex, className) {
    var preset = presetIndex % AVATAR_STYLES.length;
    var initial = escapeHtml((profile.initial || profile.name || 'P').charAt(0).toUpperCase());
    var src = getAvatarPresetUrl(profile, preset);
    return '<span class="' + className + ' avatar-local avatar-image-shell avatar-preset-' + preset + '" style="--profile-accent: ' + escapeHtml(profile.accent || '#e50914') + '"><img src="' + escapeHtml(src) + '" alt="" loading="eager" decoding="async" onerror="this.hidden=true"><span class="avatar-shape"></span><span class="avatar-mark">' + initial + '</span><span class="avatar-glint"></span></span>';
  }

  function getAvatarPresetUrl(profile, presetIndex) {
    return 'assets/profiles/avatar-' + (presetIndex % AVATAR_STYLES.length) + '.svg';
  }

  function selectProfile(profileId) {
    var profile = state.profiles.filter(function (item) { return item.id === profileId; })[0];
    if (!profile) return;
    var prefs = loadProfilePreferences(profile.id);
    state.activeProfileId = profile.id;
    state.profileOpen = false;
    state.favourites = prefs.favourites.slice();
    state.leagueFavourites = prefs.leagueFavourites.slice();
    state.teamFavourites = prefs.teamFavourites.slice();
    state.recentSearches = prefs.recentSearches.slice();
    state.activeSport = getDefaultSport();
    state.previewMuted = prefs.previewMuted;
    state.userScrolledContent = false;
    state.focusedIndex = 0;
    state.heroMatchId = '';
    state.previewMatchId = '';
    state.teamStandings = {};
    state.teamStandingsLoading = {};
    window.localStorage.setItem(ACTIVE_PROFILE_KEY, profile.id);
    updateProfileBadge(profile);
    els.profileView.hidden = true;
    document.querySelector('.app').classList.remove('profile-mode');
    if (state.catalogLoaded) {
      state.activeSport = getDefaultSport();
      render();
    } else {
      loadCatalog();
    }
  }

  function openProfiles() {
    stopHeroPreview();
    state.profileOpen = true;
    state.searchOpen = false;
    state.teamHubOpen = false;
    state.streamPickerOpen = false;
    els.searchOverlay.hidden = true;
    els.teamHubOverlay.hidden = true;
    els.streamPickerOverlay.hidden = true;
    els.profileView.hidden = false;
    state.profileManageMode = false;
    state.avatarPickerProfileId = '';
    document.querySelector('.app').classList.add('profile-mode');
    renderProfiles();
    collectFocusables(true);
    var active = state.activeProfileId ? els.profileList.querySelector('[data-profile-id="' + state.activeProfileId + '"]') : null;
    focusNode(active || state.focusables[0] || els.profileView);
  }

  function updateProfileBadge(profile) {
    if (!els.profileButton || !profile) return;
    els.profileButton.innerHTML = renderAvatarMarkup(profile, profile.avatarPreset || 0, 'profile-dot-avatar');
    els.profileButton.style.background = 'transparent';
    els.profileButton.setAttribute('aria-label', 'Switch profile. Current profile ' + profile.name);
  }

  function getStreamsForMatch(match) {
    return match.streams || [];
  }

  function saveFavourites() {
    saveProfilePreferences();
  }

  function saveLeagueFavourites() {
    saveProfilePreferences();
  }

  function saveTeamFavourites() {
    saveProfilePreferences();
  }

  function saveRecentSearches() {
    saveProfilePreferences();
  }

  function saveActiveSportPreference() {
    saveProfilePreferences();
  }

  function isFavourite(match) {
    return state.favourites.indexOf(match.id) >= 0;
  }

  function isLeagueFavourite(name) {
    return state.leagueFavourites.indexOf(name) >= 0;
  }

  function getTeamKey(team, match) {
    if (!team) return '';
    return normalizeTeamKey(team.badge || team.name || '') || (match.sport + '-' + normalizeTeamKey(team.name));
  }

  function normalizeTeamKey(value) {
    return String(value || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  }

  function getTeamIdentityKeys(team, match) {
    var keys = [getTeamKey(team, match), normalizeTeamKey(team && team.name)];
    var catalogTeam = findCatalogTeamForTeam(team, match);
    if (catalogTeam) keys.push(catalogTeam.key);
    return keys.filter(Boolean).filter(function (key, index, list) { return list.indexOf(key) === index; });
  }

  function isTeamFavourite(team, match) {
    return getTeamIdentityKeys(team, match).some(function (key) { return state.teamFavourites.indexOf(key) >= 0; });
  }

  function matchHasFavouriteTeam(match) {
    var home = match.teams && match.teams.home;
    var away = match.teams && match.teams.away;
    return isTeamFavourite(home, match) || isTeamFavourite(away, match);
  }

  function toggleTeamFavouriteByKey(key, name) {
    var index = state.teamFavourites.indexOf(key);
    if (index >= 0) {
      state.teamFavourites.splice(index, 1);
      setStatus('Removed ' + name + ' from followed teams.');
    } else {
      state.teamFavourites.push(key);
      setStatus('Following ' + name + '.');
    }
    saveTeamFavourites();
    bumpMatchCache();
    if (state.teamHubOpen) {
      renderTeamHubView();
      collectFocusables(true);
    } else {
      render(true);
    }
  }

  function toggleLeagueFavourite(name) {
    var index = state.leagueFavourites.indexOf(name);
    if (index >= 0) {
      state.leagueFavourites.splice(index, 1);
      setStatus('Removed league from favourites.');
    } else {
      state.leagueFavourites.push(name);
      setStatus('Added league to favourites.');
    }
    saveLeagueFavourites();
    bumpMatchCache();
    render();
  }

  function isLeagueExpanded(name) {
    return state.expandedLeagues.indexOf(name) >= 0;
  }

  function expandLeague(name) {
    if (!isLeagueExpanded(name)) state.expandedLeagues.push(name);
    setStatus('Showing all matches for ' + name + '.');
    render();
  }

  function getFocusedMatch() {
    var active = document.activeElement;
    var id = active && active.dataset ? active.dataset.matchId : '';
    if (!id) return null;
    return state.matches.filter(function (match) { return match.id === id; })[0] || null;
  }

  function toggleFocusedFavourite() {
    var match = getFocusedMatch();
    if (match) toggleFavourite(match);
  }

  function getFocusedLeague() {
    var active = document.activeElement;
    return active && active.dataset ? active.dataset.leagueName || '' : '';
  }

  function toggleFocusedLeagueFavourite() {
    var league = getFocusedLeague();
    if (league) toggleLeagueFavourite(league);
  }

  function toggleFavourite(match) {
    var index = state.favourites.indexOf(match.id);
    if (index >= 0) {
      state.favourites.splice(index, 1);
      setStatus('Removed from favourites.');
    } else {
      state.favourites.push(match.id);
      setStatus('Added to favourites.');
    }
    saveFavourites();
    bumpMatchCache();
    render();
  }

  function bumpMatchCache() {
    state.matchCacheVersion += 1;
    state.matchesBySportCache = {};
    state.sportCountsCache = null;
  }

  function render(skipFocus) {
    renderSports();
    renderMatches();
    if (state.searchOpen) renderSearchResults();
    if (state.teamHubOpen) renderTeamHubView();
    if (state.streamPickerOpen) renderStreamPickerView(findMatchById(state.streamPickerMatchId));
    collectFocusables(skipFocus);
  }

  function openSearch() {
    state.searchOpen = true;
    state.teamHubOpen = false;
    state.streamPickerOpen = false;
    els.teamHubOverlay.hidden = true;
    els.streamPickerOverlay.hidden = true;
    els.searchOverlay.hidden = false;
    renderSearchKeyboard();
    renderSearchResults();
    collectFocusables(true);
    focusNode(els.searchInput);
  }

  function closeSearch() {
    rememberSearch(state.searchQuery);
    state.searchOpen = false;
    els.searchOverlay.hidden = true;
    els.searchInput.blur();
    focusCurrent();
  }

  function openTeamHub() {
    state.teamHubOpen = true;
    state.searchOpen = false;
    state.streamPickerOpen = false;
    els.searchOverlay.hidden = true;
    els.streamPickerOverlay.hidden = true;
    els.teamHubOverlay.hidden = false;
    renderTeamHubView();
    collectFocusables(true);
    var first = els.teamHubOverlay.querySelector('[data-focusable="true"]');
    if (first) focusNode(first);
  }

  function closeTeamHub() {
    state.teamHubOpen = false;
    els.teamHubOverlay.hidden = true;
    render(true);
    focusCurrent();
  }

  function renderSearchResults() {
    if (!els.searchResults || !state.searchOpen) return;

    var query = state.searchQuery.trim().toLowerCase();
    var suggestions = getSearchSuggestions(query);
    var results = query ? state.matches.filter(function (match) {
      return matchesSearch(match, query);
    }).sort(rankMatches).slice(0, 14) : getSuggestedMatches().slice(0, 10);

    els.searchResults.innerHTML = '';
    renderSearchList('Suggestions', suggestions, renderSuggestionResultButton);
    renderSearchList(query ? 'Matches' : 'For you', results, renderMatchResultButton);

    if (!suggestions.length && !results.length) {
      els.searchResults.innerHTML = '<div class="empty-state search-empty">Follow teams or search once to build suggestions.</div>';
    }
  }

  function renderSearchList(title, items, renderer) {
    if (!items.length) return;
    var section = document.createElement('section');
    section.className = 'search-result-section';
    var heading = document.createElement('h2');
    heading.className = 'match-rail-title';
    heading.textContent = title;
    var list = document.createElement('div');
    list.className = 'search-result-list';
    items.forEach(function (item) { list.appendChild(renderer(item)); });
    section.appendChild(heading);
    section.appendChild(list);
    els.searchResults.appendChild(section);
  }

  function renderSuggestionResultButton(suggestion) {
    var button = document.createElement('button');
    button.className = 'search-result-row suggestion-' + suggestion.type;
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.innerHTML = '<span class="result-kind">' + escapeHtml(suggestion.type) + '</span><strong>' + escapeHtml(suggestion.label) + '</strong>';
    button.addEventListener('click', function () { applySearchSuggestion(suggestion.label); });
    return button;
  }

  function renderMatchResultButton(match) {
    var button = document.createElement('button');
    button.className = 'search-result-row match-result-row';
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.matchId = match.id;
    button.innerHTML = [
      '<span class="result-kind">' + escapeHtml(match.status) + '</span>',
      '<strong>' + escapeHtml(match.title) + '</strong>',
      '<span>' + escapeHtml(match.competition || match.sport) + ' · ' + escapeHtml(formatStart(match.startTime)) + '</span>'
    ].join('');
    button.addEventListener('click', function () {
      rememberSearch(state.searchQuery || match.title);
      closeSearch();
      state.activeSport = match.sport;
      saveActiveSportPreference();
      state.focusedKey = 'sport:' + match.sport;
      state.heroMatchId = match.id;
      render(true);
      focusMatchById(match.id);
    });
    return button;
  }

  function getSuggestedMatches() {
    return state.matches.filter(function (match) {
      return matchHasFavouriteTeam(match) || isFavourite(match) || isLeagueFavourite(match.competition);
    }).sort(orderLiveScheduledRecent);
  }

  function getSearchSuggestions(query) {
    var items = [];
    state.recentSearches.forEach(function (term) { items.push({ type: 'recent', label: term, weight: 300 }); });
    getFavouriteTeamNames().forEach(function (name) { items.push({ type: 'team', label: name, weight: 260 }); });
    state.leagueFavourites.forEach(function (name) { items.push({ type: 'league', label: name, weight: 220 }); });
    state.matches.filter(isFavourite).forEach(function (match) { items.push({ type: 'match', label: match.title, weight: 200 }); });

    var seen = {};
    return items.filter(function (item) {
      var key = normalizeSearchText(item.label).trim();
      if (!key || seen[key]) return false;
      if (query && key.indexOf(normalizeSearchText(query).trim()) < 0) return false;
      seen[key] = true;
      return true;
    }).sort(function (a, b) {
      return b.weight - a.weight || a.label.localeCompare(b.label);
    }).slice(0, 12);
  }

  function getFavouriteTeamNames() {
    var names = [];
    var seen = {};
    state.matches.forEach(function (match) {
      getMatchTeams(match).forEach(function (team) {
        var key = getTeamKey(team, match);
        if (state.teamFavourites.indexOf(key) >= 0 && !seen[key]) {
          seen[key] = true;
          names.push(team.name || key);
        }
      });
    });
    return names;
  }

  function applySearchSuggestion(term) {
    state.searchQuery = term;
    els.searchInput.value = term;
    rememberSearch(term);
    renderSearchResults();
    collectFocusables(true);
    focusNode(els.searchInput);
  }

  function rememberSearch(term) {
    term = String(term || '').trim();
    if (!term) return;
    state.recentSearches = [term].concat(state.recentSearches.filter(function (item) {
      return item.toLowerCase() !== term.toLowerCase();
    })).slice(0, 8);
    saveRecentSearches();
  }

  function renderSearchKeyboard() {
    if (!els.searchKeyboard || els.searchKeyboard.dataset.ready) return;
    var rows = [
      ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p'],
      ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l'],
      ['z', 'x', 'c', 'v', 'b', 'n', 'm'],
      ['space', 'delete', 'clear']
    ];
    rows.forEach(function (keys) {
      var row = document.createElement('div');
      row.className = 'keyboard-row';
      keys.forEach(function (key) {
        var button = document.createElement('button');
        button.className = 'keyboard-key keyboard-key-' + key;
        button.type = 'button';
        button.dataset.focusable = 'true';
        button.dataset.keyValue = key;
        button.textContent = key === 'space' ? 'Space' : key === 'delete' ? 'Delete' : key === 'clear' ? 'Clear' : key.toUpperCase();
        button.addEventListener('click', function () { applyKeyboardKey(key); });
        row.appendChild(button);
      });
      els.searchKeyboard.appendChild(row);
    });
    els.searchKeyboard.dataset.ready = 'true';
  }

  function applyKeyboardKey(key) {
    var value = state.searchQuery;
    if (key === 'space') value += ' ';
    else if (key === 'delete') value = value.slice(0, -1);
    else if (key === 'clear') value = '';
    else value += key;
    state.searchQuery = value;
    els.searchInput.value = value;
    renderSearchResults();
    collectFocusables(true);
  }

  function getSearchText(match) {
    return normalizeSearchText([
      match.sport,
      match.competition,
      match.title,
      getMatchTeams(match).map(getTeamSearchText).join(' '),
      match.status,
      match.scoreText,
      match.resultText,
      match.venue,
      match.source
    ].join(' '));
  }

  function normalizeSearchText(value) {
    var text = String(value || '').toLowerCase();
    var aliases = {
      bangalore: ['bengaluru', 'rcb', 'royal challengers'],
      bengaluru: ['bangalore', 'rcb', 'royal challengers'],
      lucknow: ['lsg', 'super giants'],
      hyderabad: ['srh', 'sunrisers'],
      kolkata: ['kkr', 'knight riders'],
      mumbai: ['mi', 'indians'],
      chennai: ['csk', 'super kings'],
      delhi: ['dc', 'capitals'],
      punjab: ['pbks', 'kings'],
      rajasthan: ['rr', 'royals'],
      gujarat: ['gt', 'titans']
    };

    Object.keys(aliases).forEach(function (key) {
      if (text.indexOf(key) >= 0 || aliases[key].some(function (alias) { return text.indexOf(alias) >= 0; })) {
        text += ' ' + key + ' ' + aliases[key].join(' ');
      }
    });
    return text;
  }

  function matchesSearch(match, query) {
    var normalizedQuery = normalizeSearchText(query).trim();
    var haystack = getSearchText(match);
    return normalizedQuery.split(/\s+/).filter(Boolean).every(function (part) {
      return haystack.indexOf(part) >= 0;
    });
  }

  function rankMatches(a, b) {
    return matchRank(b) - matchRank(a) || new Date(b.startTime).getTime() - new Date(a.startTime).getTime();
  }

  function matchRank(match) {
    var rank = 0;
    if (match.status === 'live') rank += 1000;
    if (hasPlayableStreamSource(match)) rank += 140;
    else rank -= 520;
    if (matchHasFavouriteTeam(match)) rank += 300;
    if (isFavourite(match)) rank += 200;
    if (isLeagueFavourite(match.competition)) rank += 100;
    if (match.status === 'recent') rank += 20;
    return rank;
  }

  function hasPlayableStreamSource(match) {
    return !!(getStreamsForMatch(match).length || (match.streamSources && match.streamSources.length));
  }

  function renderSports() {
    els.sportsList.innerHTML = '';
    var fragment = document.createDocumentFragment();
    var navSports = getNavSports();
    var primarySports = getPrimaryNavSports(navSports);
    var overflowSports = navSports.filter(function (sport) {
      return primarySports.indexOf(sport) < 0;
    });

    var sportCounts = getSportCounts();
    primarySports.forEach(function (sport) {
      fragment.appendChild(createSportButton(sport, 'sport-button', sportCounts[sport.id] || 0));
    });

    if (overflowSports.length) {
      var moreWrap = document.createElement('div');
      moreWrap.className = 'nav-more';
      var activeOverflow = overflowSports.some(function (sport) { return sport.id === state.activeSport; });
      var moreButton = document.createElement('button');
      moreButton.className = 'sport-button more-button' + (activeOverflow ? ' active' : '');
      moreButton.type = 'button';
      moreButton.dataset.focusable = 'true';
      moreButton.dataset.navMoreToggle = 'true';
      moreButton.innerHTML = '<span>' + (activeOverflow ? escapeHtml(getActiveSportName(overflowSports)) : 'More') + '</span><span class="sport-count">' + overflowSports.length + '</span>';
      moreButton.setAttribute('aria-expanded', state.navMoreOpen ? 'true' : 'false');
      moreButton.addEventListener('click', function () {
        toggleNavMore(!state.navMoreOpen, true);
      });
      moreWrap.appendChild(moreButton);

      var menu = document.createElement('div');
      menu.className = 'nav-more-menu';
      if (!state.navMoreOpen) menu.hidden = true;
      overflowSports.forEach(function (sport) {
        menu.appendChild(createSportButton(sport, 'sport-button overflow-sport-button', sportCounts[sport.id] || 0));
      });
      moreWrap.appendChild(menu);
      fragment.appendChild(moreWrap);
    }
    els.sportsList.appendChild(fragment);
  }

  function toggleNavMore(open, focusMenu) {
    state.navMoreOpen = typeof open === 'boolean' ? open : !state.navMoreOpen;
    render(true);
    collectFocusables(true);
    if (state.navMoreOpen && focusMenu) {
      focusFirstNavMoreItem();
      return;
    }
    var toggle = els.sportsList.querySelector('.more-button');
    if (toggle) focusNode(toggle);
  }

  function focusFirstNavMoreItem() {
    var first = els.sportsList.querySelector('.nav-more-menu [data-sport-id]');
    if (first) focusNode(first);
  }

  function createSportButton(sport, className, count) {
    count = typeof count === 'number' ? count : getMatchesForSport(sport.id).length;
    var button = document.createElement('button');
    button.className = className + (sport.id === state.activeSport ? ' active' : '');
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.sportId = sport.id;
    button.innerHTML = '<span>' + escapeHtml(sport.name) + '</span><span class="sport-count">' + count + '</span>';
    button.title = sport.name;
    button.addEventListener('click', function () {
      selectSport(sport.id);
    });
    button.addEventListener('focus', function () {
      if (!IS_TV_RUNTIME || state.activeSport === sport.id) return;
      if (state.navSelectTimer) clearTimeout(state.navSelectTimer);
      state.navSelectTimer = setTimeout(function () {
        if (document.activeElement === button && els.playerView.hidden && !state.profileOpen && !state.searchOpen && !state.teamHubOpen) {
          selectSport(sport.id);
        }
      }, 3000);
    });
    button.addEventListener('blur', function () {
      if (state.navSelectTimer) clearTimeout(state.navSelectTimer);
      state.navSelectTimer = null;
    });
    return button;
  }

  function selectSport(sportId) {
    if (state.navSelectTimer) clearTimeout(state.navSelectTimer);
    state.navSelectTimer = null;
    if (state.activeSport !== sportId) {
      stopHeroPreview();
      state.activeSport = sportId;
      saveActiveSportPreference();
      state.focusedKey = 'sport:' + sportId;
      state.navMoreOpen = false;
      state.heroMatchId = '';
      render(true);
    }
    focusActiveSportSelector();
  }

  function getPrimaryNavSports(navSports) {
    var preferred = ['for-you', 'cricket', 'football', 'basketball', 'tennis'];
    var primary = [];
    preferred.forEach(function (id) {
      var sport = navSports.filter(function (item) { return item.id === id; })[0];
      if (sport && primary.indexOf(sport) < 0) primary.push(sport);
    });
    navSports.forEach(function (sport) {
      if (primary.length < 5 && primary.indexOf(sport) < 0) primary.push(sport);
    });
    return primary;
  }

  function getActiveSportName(sports) {
    var active = sports.filter(function (sport) { return sport.id === state.activeSport; })[0];
    return active ? active.name : 'More';
  }

  function getNavSports() {
    return [{ id: 'for-you', name: 'For You' }].concat(state.sports);
  }

  function getSportCounts() {
    if (state.sportCountsCache) return state.sportCountsCache;
    var counts = { 'for-you': 0 };
    state.matches.forEach(function (match) {
      counts[match.sport] = (counts[match.sport] || 0) + 1;
      if (matchHasFavouriteTeam(match)) counts['for-you'] += 1;
    });
    state.sportCountsCache = counts;
    return counts;
  }

  function getMatchesForSport(sportId) {
    var key = sportId + ':' + state.matchCacheVersion;
    if (state.matchesBySportCache[key]) return state.matchesBySportCache[key];
    var matches = sportId === 'for-you' ? state.matches.filter(matchHasFavouriteTeam) : state.matches.filter(function (match) {
      return match.sport === sportId;
    });
    state.matchesBySportCache[key] = matches.slice().sort(orderLiveScheduledRecent);
    return state.matchesBySportCache[key];
  }

  function renderMatches() {
    var sport = getNavSports().filter(function (item) { return item.id === state.activeSport; })[0];
    var matches = getMatchesForSport(state.activeSport);
    var featured = getHeroMatch(matches);
    var fragment = document.createDocumentFragment();

    els.activeSportLabel.textContent = sport ? sport.name : 'Live';
    els.pageTitle.textContent = state.activeSport === 'for-you' ? 'Your teams' : (matches.length ? 'Top live picks' : 'No matches');
    els.matchesGrid.innerHTML = '';
    if (featured) state.heroMatchId = featured.id;
    renderHero(featured);
    queueAutoPlay(featured);

    if (state.activeSport === 'for-you') {
      renderForYouRails(matches, fragment);
      if (!matches.length) {
        fragment.appendChild(createEmptyState('Follow teams from any match hero or add teams from your searches.'));
      }
      els.matchesGrid.appendChild(fragment);
      return;
    }

    if (!matches.length) {
      els.matchesGrid.innerHTML = '<div class="empty-state">No matches are available for this sport yet.</div>';
      return;
    }

    if (state.activeSport === 'cricket') {
      renderCricketRails(matches, fragment);
      els.matchesGrid.appendChild(fragment);
      return;
    }

    var row = createRail('Live and scheduled', matches);
    fragment.appendChild(row);
    els.matchesGrid.appendChild(fragment);
  }

  function getHeroMatch(matches) {
    if (state.heroMatchId) {
      var selected = matches.filter(function (match) { return match.id === state.heroMatchId; })[0];
      if (selected) return selected;
    }
    return matches.filter(function (match) { return match.status === 'live' && (match.streams.length || (match.streamSources && match.streamSources.length)); })[0] || matches[0];
  }

  function selectHeroMatch(match, immediate) {
    if (!match) return;
    if (IS_TV_RUNTIME && !immediate && state.heroMatchId !== match.id) {
      if (state.heroRenderTimer) clearTimeout(state.heroRenderTimer);
      setStatus(match.status === 'live' ? 'Press OK to view streams.' : 'Scheduled match selected.');
      state.heroRenderTimer = setTimeout(function () {
        state.heroRenderTimer = null;
        applyHeroMatch(match);
      }, 160);
      return;
    }
    applyHeroMatch(match);
  }

  function applyHeroMatch(match) {
    if (!match) return;
    if (state.heroMatchId !== match.id) {
      stopHeroPreview();
      state.heroMatchId = match.id;
      state.selectedMatchId = match.id;
      renderHero(match);
    }
    queueAutoPlay(match);
  }

  function hasUserScrolledContent() {
    return !!(els.content && state.userScrolledContent && els.content.scrollTop > 32);
  }

  function queueAutoPlay(match) {
    if (!match) return;
    if (state.autoPlayTimer) clearTimeout(state.autoPlayTimer);
    stopHeroPreview();
    setStatus(match.status === 'live' ? 'Press OK to view streams.' : 'Scheduled match selected.');
  }

  function renderForYouRails(matches, target) {
    target = target || els.matchesGrid;
    var live = matches.filter(function (match) { return match.status === 'live'; }).sort(rankMatches);
    var scheduled = matches.filter(function (match) { return match.status !== 'live' && match.status !== 'recent'; }).sort(sortOldestFirst);
    var recent = matches.filter(function (match) { return match.status === 'recent'; }).sort(sortNewestFirst).slice(0, 8);
    if (live.length) target.appendChild(createRail('Live streams', live));
    target.appendChild(createTeamHubLauncherRail(live.length, scheduled.length, recent.length));
    if (recent.length) target.appendChild(createRail('Recently finished', recent));
    if (scheduled.length) target.appendChild(createRail('Coming up', scheduled));
  }

  function createTeamHubLauncherRail(liveCount, scheduledCount, recentCount) {
    var section = document.createElement('section');
    section.className = 'match-rail team-hub-launcher';
    var header = document.createElement('div');
    header.className = 'match-rail-heading';
    var heading = document.createElement('h2');
    heading.className = 'match-rail-title';
    heading.textContent = 'Team Hub';
    header.appendChild(heading);

    var row = document.createElement('div');
    row.className = 'matches-row team-hub-row';
    var button = document.createElement('button');
    button.className = 'match-card team-hub-card';
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.innerHTML = [
      '<div class="team-hub-card-art"><span>Teams</span></div>',
      '<div class="tile-copy team-hub-card-copy">',
      '<strong>Manage followed teams</strong>',
      '<span class="competition">' + state.teamFavourites.length + ' followed · ' + liveCount + ' live</span>',
      '<span class="score-line">Standings, add/remove teams, and search-based suggestions</span>',
      '<span class="source-row">Open Team Hub</span>',
      '</div>'
    ].join('');
    button.addEventListener('click', openTeamHub);
    row.appendChild(button);
    section.appendChild(header);
    section.appendChild(row);
    return section;
  }

  function renderTeamHubView() {
    if (!els.teamHubBody) return;
    var followed = getFollowedTeamSummaries();
    var searchResults = getTeamHubSearchResults();
    els.teamHubBody.innerHTML = '';
    els.teamHubBody.appendChild(createTeamStandingBoard(followed));
    els.teamHubBody.appendChild(createTeamDiscoveryPanel(searchResults));
  }

  function createTeamStandingBoard(teams) {
    var panel = document.createElement('section');
    panel.className = 'team-standings-board';
    var header = document.createElement('div');
    header.className = 'team-panel-heading';
    header.innerHTML = [
      '<div><h3>Current standing</h3><span>' + teams.length + ' followed teams</span></div>',
      '<button class="team-action-button add" type="button" data-focusable="true">Refresh</button>'
    ].join('');
    header.querySelector('button').addEventListener('click', refreshTeamStandings);
    panel.appendChild(header);

    var grid = document.createElement('div');
    grid.className = 'team-standing-grid';
    if (teams.length) {
      teams.forEach(function (team) { grid.appendChild(renderTeamStandingCard(team)); });
    } else {
      grid.appendChild(createSmallEmptyState('No followed teams yet. Use search to add teams and build your hub.'));
    }
    panel.appendChild(grid);
    return panel;
  }

  function createTeamDiscoveryPanel(teams) {
    var panel = document.createElement('section');
    panel.className = 'teams-manager-panel team-discovery-panel';
    var labelId = 'teamHubSearchLabel';
    panel.innerHTML = [
      '<div class="team-panel-heading"><div><h3>Find teams</h3><span>Search the current match catalog</span></div></div>',
      '<label class="team-hub-search" id="' + labelId + '">',
      '<span>Search</span>',
      '<input data-focusable="true" type="search" value="' + escapeHtml(state.teamHubSearchQuery) + '" placeholder="Team, league, or sport">',
      '</label>'
    ].join('');
    var input = panel.querySelector('input');
    input.addEventListener('input', function () {
      state.teamHubSearchQuery = input.value;
      renderTeamHubView();
      collectFocusables(true);
      var nextInput = els.teamHubBody.querySelector('.team-hub-search input');
      if (nextInput) {
        focusNode(nextInput);
        nextInput.setSelectionRange(nextInput.value.length, nextInput.value.length);
      }
    });

    var list = document.createElement('div');
    list.className = 'team-manager-list team-search-results';
    if (teams.length) {
      teams.forEach(function (team) { list.appendChild(renderTeamSearchCard(team)); });
    } else {
      list.appendChild(createSmallEmptyState(state.teamHubSearchQuery ? 'No teams matched that search.' : 'Start typing to find more teams, or pick from suggested teams below.'));
      if (!state.teamHubSearchQuery) {
        getSearchBasedTeamSuggestions().forEach(function (team) { list.appendChild(renderTeamSearchCard(team)); });
      }
    }
    panel.appendChild(list);
    return panel;
  }

  function renderTeamStandingCard(summary) {
    var card = document.createElement('article');
    card.className = 'team-standing-card';
    var standing = getTeamStandingForSummary(summary);
    var meta = normalizeStandingDisplay(standing);
    var standingLogo = summary.badgeUrl || summary.logoUrl || '';
    var badge = standingLogo ? '<img src="' + escapeHtml(standingLogo) + '" alt="' + escapeHtml(summary.name) + '" width="54" height="54" loading="lazy">' : '<span>' + escapeHtml((summary.name || '?').charAt(0)) + '</span>';
    card.innerHTML = [
      '<div class="team-standing-top">',
      '<div class="team-standing-badge">' + badge + '</div>',
      '<div class="team-standing-title"><strong>' + escapeHtml(summary.name) + '</strong><span>' + escapeHtml([summary.sport, meta.league || summary.competition].filter(Boolean).join(' · ')) + '</span></div>',
      '<div class="standing-rank">' + escapeHtml(meta.position || '--') + '</div>',
      '</div>',
      '<p class="standing-summary">' + escapeHtml(meta.summary) + '</p>',
      '<div class="standing-stats">',
      renderStandingStatChips(meta),
      '</div>',
      meta.sourceUrl ? '<a class="standing-source" href="' + escapeHtml(meta.sourceUrl) + '" target="_blank" rel="noreferrer">' + escapeHtml(meta.sourceTitle || 'Source') + '</a>' : '<span class="standing-source muted">' + escapeHtml(meta.sourceTitle || 'OpenAI standings lookup') + '</span>'
    ].join('');

    var action = document.createElement('button');
    action.className = 'team-action-button remove';
    action.type = 'button';
    action.dataset.focusable = 'true';
    action.textContent = 'Remove';
    action.addEventListener('click', function () { toggleTeamFavouriteByKey(summary.key, summary.name); });
    card.appendChild(action);
    return card;
  }

  function renderStandingStatChips(meta) {
    var labels = meta.statLabels || {};
    return [
      renderStatChip(labels.points || 'Pts', meta.points),
      renderStatChip(labels.record || 'Record', meta.record),
      renderStatChip(labels.form || 'Form', meta.form)
    ].join('');
  }

  function renderStatChip(label, value) {
    return '<span><small>' + escapeHtml(label) + '</small><strong>' + escapeHtml(value || '--') + '</strong></span>';
  }

  function renderTeamSearchCard(summary) {
    var card = document.createElement('div');
    card.className = 'team-manager-card team-search-card';
    var followed = state.teamFavourites.indexOf(summary.key) >= 0;
    var searchLogo = summary.badgeUrl || summary.logoUrl || '';
    var badge = searchLogo ? '<img src="' + escapeHtml(searchLogo) + '" alt="" width="42" height="42" loading="lazy">' : '<span>' + escapeHtml((summary.name || '?').charAt(0)) + '</span>';
    card.innerHTML = [
      '<div class="team-search-badge">' + badge + '</div>',
      '<div class="team-manager-copy">',
      '<strong>' + escapeHtml(summary.name) + '</strong>',
      '<span>' + escapeHtml([summary.sport, summary.competition].filter(Boolean).join(' · ')) + '</span>',
      '</div>'
    ].join('');

    var action = document.createElement('button');
    action.className = 'team-action-button' + (followed ? ' remove' : ' add');
    action.type = 'button';
    action.dataset.focusable = 'true';
    action.textContent = followed ? 'Following' : 'Follow';
    action.addEventListener('click', function () { toggleTeamFavouriteByKey(summary.key, summary.name); });
    card.appendChild(action);
    return card;
  }

  function loadTeamHubBackfill() {
    if (!window.StreamedAPI || !window.StreamedAPI.getTeamHubTeams || !window.StreamedAPI.HAS_TEAM_HUB_PROVIDER) return;
    window.StreamedAPI.getTeamHubTeams().then(function (teams) {
      state.teamBackfill = teams || [];
      (teams || []).forEach(function (team) {
        if (team.key && team.standing) state.teamStandings[team.key] = normalizeStandingData(team.standing);
      });
      refreshTeamCatalog();
      if (state.teamHubOpen) {
        renderTeamHubView();
        collectFocusables(true);
      } else if (state.activeSport === 'for-you') {
        render(true);
      }
    }).catch(function () {});
  }

  function refreshTeamCatalog() {
    state.localTeamCatalog = buildTeamCatalog();
  }

  function getTeamCatalog() {
    if (!state.localTeamCatalog.length) refreshTeamCatalog();
    return state.localTeamCatalog;
  }

  function buildTeamCatalog() {
    var seen = {};
    var teams = [];
    function addTeam(summary) {
      if (!summary || !summary.name) return;
      var normalized = normalizeTeamSummary(summary);
      if (!normalized.key) return;
      if (seen[normalized.key]) {
        if (normalized.logoUrl && !seen[normalized.key].logoUrl) seen[normalized.key].logoUrl = normalized.logoUrl;
        if (normalized.badgeUrl && !seen[normalized.key].badgeUrl) seen[normalized.key].badgeUrl = normalized.badgeUrl;
        if (normalized.standing && !state.teamStandings[normalized.key]) state.teamStandings[normalized.key] = normalizeStandingData(normalized.standing);
        seen[normalized.key].aliases = mergeAliases(seen[normalized.key].aliases, normalized.aliases);
        seen[normalized.key].searchText = getTeamSearchText(seen[normalized.key]);
        return;
      }
      seen[normalized.key] = normalized;
      teams.push(normalized);
    }

    (window.STREAMED_TEAM_DB || []).forEach(addTeam);
    state.teamBackfill.forEach(addTeam);
    safeParseArray(TEAM_CATALOG_CACHE_KEY).forEach(addTeam);
    state.matches.forEach(function (match) {
      getMatchTeams(match).forEach(function (team) {
        addTeam(getTeamSummary(team, match));
      });
    });

    try {
      window.localStorage.setItem(TEAM_CATALOG_CACHE_KEY, JSON.stringify(teams.slice(0, 300)));
    } catch (error) {}

    return teams.sort(function (a, b) {
      var sportOrder = { cricket: 0, football: 1, basketball: 2, tennis: 3 };
      var aOrder = sportOrder[a.sport] == null ? 20 : sportOrder[a.sport];
      var bOrder = sportOrder[b.sport] == null ? 20 : sportOrder[b.sport];
      return aOrder - bOrder || a.name.localeCompare(b.name);
    });
  }

  function normalizeTeamSummary(summary) {
    var key = summary.key || normalizeTeamKey(summary.badge || summary.name);
    var aliases = mergeAliases(summary.aliases || [], [summary.nickname, summary.shortName]);
    var normalized = {
      key: normalizeTeamKey(key),
      name: summary.name || toTitleCase(key),
      sport: summary.sport || '',
      competition: summary.competition || '',
      country: summary.country || '',
      badge: summary.badge || '',
      badgeUrl: summary.badgeUrl || summary.logoUrl || '',
      logoUrl: summary.logoUrl || '',
      aliases: aliases
    };
    normalized.searchText = getTeamSearchText(normalized);
    return normalized;
  }

  function mergeTeamSummary(base, override) {
    var merged = {};
    [base || {}, override || {}].forEach(function (item) {
      Object.keys(item).forEach(function (key) {
        if (key === 'aliases') return;
        if (item[key]) merged[key] = item[key];
      });
    });
    merged.aliases = mergeAliases(base && base.aliases, override && override.aliases);
    merged.searchText = getTeamSearchText(merged);
    return merged;
  }

  function mergeAliases(first, second) {
    return (first || []).concat(second || []).filter(Boolean).filter(function (alias, index, list) {
      return list.indexOf(alias) === index;
    });
  }

  function getTeamSearchText(team) {
    if (!team) return '';
    if (team.searchText) return team.searchText;
    var catalogTeam = findCatalogTeamByName(team.name, team.sport);
    return normalizeSearchText([
      team.name,
      team.sport,
      team.competition,
      team.country,
      team.badge,
      (team.aliases || []).join(' '),
      catalogTeam ? (catalogTeam.aliases || []).join(' ') : ''
    ].join(' '));
  }

  function teamMatchesQuery(summary, query) {
    var terms = normalizeSearchText(query).trim().split(/\s+/).filter(Boolean);
    var haystack = getTeamSearchText(summary);
    return terms.every(function (term) { return haystack.indexOf(term) >= 0; });
  }

  function findCatalogTeamForTeam(team, match) {
    if (!team || !team.name) return null;
    return findCatalogTeamByName(team.name, match && match.sport);
  }

  function findCatalogTeamByName(name, sport) {
    var normalizedName = normalizeSearchText(name).trim();
    var normalizedKey = normalizeTeamKey(name);
    return (state.localTeamCatalog || []).concat(window.STREAMED_TEAM_DB || []).filter(function (team) {
      if (sport && team.sport && team.sport !== sport) return false;
      if (normalizeTeamKey(team.key || team.name) === normalizedKey) return true;
      var haystack = normalizeSearchText([team.name, (team.aliases || []).join(' ')].join(' '));
      return normalizedName && (haystack.indexOf(normalizedName) >= 0 || normalizedName.indexOf(haystack) >= 0);
    })[0] || null;
  }

  function getLocalTeamSnapshot(summary) {
    var related = state.matches.filter(function (match) { return matchIncludesTeam(match, summary); });
    var live = related.filter(function (match) { return match.status === 'live'; }).sort(rankMatches);
    var recent = related.filter(function (match) { return match.status === 'recent'; }).sort(sortNewestFirst);
    var scheduled = related.filter(function (match) { return match.status !== 'live' && match.status !== 'recent'; }).sort(sortOldestFirst);
    var lead = live[0] || scheduled[0] || recent[0];
    var summaryText = lead ? (live[0] ? 'Live now: ' : scheduled[0] ? 'Next: ' : 'Latest: ') + lead.title : 'Followed from local team database.';
    return {
      summary: summaryText,
      position: 'Hub',
      league: summary.competition || summary.sport || '',
      points: String(live.length),
      record: String(scheduled.length),
      form: String(recent.length),
      statLabels: { points: 'Live', record: 'Next', form: 'Recent' },
      sourceTitle: 'Local snapshot. Start the Node server with OPENAI_API_KEY for current rank and points.',
      sourceUrl: '',
      localOnly: true
    };
  }

  function matchIncludesTeam(match, summary) {
    return getMatchTeams(match).some(function (team) {
      return getTeamIdentityKeys(team, match).indexOf(summary.key) >= 0 || teamMatchesQuery(summary, team.name || '');
    });
  }

  function getFollowedTeamSummaries() {
    var seen = {};
    var summaries = [];
    state.matches.forEach(function (match) {
      getMatchTeams(match).forEach(function (team) {
        var summary = getTeamSummary(team, match);
        var favouriteKey = getTeamIdentityKeys(team, match).filter(function (key) { return state.teamFavourites.indexOf(key) >= 0; })[0];
        if (favouriteKey && !seen[favouriteKey]) {
          seen[favouriteKey] = true;
          summaries.push(mergeTeamSummary(summary, { key: favouriteKey }));
        }
      });
    });
    state.teamFavourites.forEach(function (key) {
      if (!seen[key]) {
        var catalogTeam = getTeamCatalog().filter(function (team) { return team.key === key; })[0];
        seen[key] = true;
        summaries.push(catalogTeam || { key: key, name: toTitleCase(key), sport: '', competition: '' });
      }
    });
    return summaries.sort(function (a, b) { return a.name.localeCompare(b.name); });
  }

  function getSearchBasedTeamSuggestions() {
    var seen = {};
    var suggestions = [];
    state.recentSearches.forEach(function (term) {
      getTeamCatalog().forEach(function (summary) {
        if (teamMatchesQuery(summary, term) && state.teamFavourites.indexOf(summary.key) < 0 && !seen[summary.key]) {
          seen[summary.key] = true;
          suggestions.push(summary);
        }
      });
    });
    if (!suggestions.length) {
      getTeamCatalog().forEach(function (summary) {
        if (state.teamFavourites.indexOf(summary.key) < 0 && !seen[summary.key]) {
          seen[summary.key] = true;
          suggestions.push(summary);
        }
      });
    }
    return suggestions.slice(0, 5);
  }

  function getTeamHubSearchResults() {
    var query = normalizeSearchText(state.teamHubSearchQuery).trim();
    var teams = getTeamCatalog().filter(function (summary) {
      return !query || teamMatchesQuery(summary, query);
    });
    teams.sort(function (a, b) {
      var aFollowed = state.teamFavourites.indexOf(a.key) >= 0 ? 1 : 0;
      var bFollowed = state.teamFavourites.indexOf(b.key) >= 0 ? 1 : 0;
      return bFollowed - aFollowed || (a.sport || '').localeCompare(b.sport || '') || a.name.localeCompare(b.name);
    });
    return teams.slice(0, 8);
  }

  function getTeamSummary(team, match) {
    var catalogTeam = findCatalogTeamForTeam(team, match);
    return mergeTeamSummary(catalogTeam, {
      key: catalogTeam ? catalogTeam.key : getTeamKey(team, match),
      name: team.name || (catalogTeam && catalogTeam.name) || 'Team',
      sport: match.sport || (catalogTeam && catalogTeam.sport) || '',
      competition: match.competition || (catalogTeam && catalogTeam.competition) || '',
      badge: team.badge || '',
      badgeUrl: team.badgeUrl || '',
      aliases: catalogTeam ? catalogTeam.aliases : []
    });
  }

  function getTeamStandingForSummary(summary) {
    if (state.teamStandings[summary.key]) return state.teamStandings[summary.key];
    if (!window.StreamedAPI || !window.StreamedAPI.HAS_TEAM_STANDINGS_PROVIDER) {
      return getLocalTeamSnapshot(summary);
    }
    if (state.teamStandingsLoading[summary.key]) return { summary: 'Updating standing...', status: 'loading' };
    loadTeamStanding(summary);
    return { summary: 'Updating standing...', status: 'loading' };
  }

  function loadTeamStanding(summary) {
    if (!window.StreamedAPI || !window.StreamedAPI.getTeamStanding || !window.StreamedAPI.HAS_TEAM_STANDINGS_PROVIDER) {
      state.teamStandings[summary.key] = getLocalTeamSnapshot(summary);
      return;
    }
    state.teamStandingsLoading[summary.key] = true;
    window.StreamedAPI.getTeamStanding(summary).then(function (payload) {
      state.teamStandings[summary.key] = normalizeStandingData(payload);
    }).catch(function () {
      state.teamStandings[summary.key] = { summary: 'Standing unavailable from backend.', status: 'error' };
    }).finally(function () {
      delete state.teamStandingsLoading[summary.key];
      if (state.teamHubOpen) {
        renderTeamHubView();
        collectFocusables(true);
      } else if (state.activeSport === 'for-you') {
        render(true);
      }
    });
  }

  function normalizeStandingData(payload) {
    var data = payload && (payload.data || payload.standing || payload.result || payload);
    if (typeof data === 'string') return { summary: data };
    if (!data) return { summary: 'Standing unavailable.' };
    return {
      summary: data.summary || data.text || 'Standing unavailable.',
      position: data.position || data.rank || data.place || '',
      league: data.league || data.competition || data.table || '',
      points: data.points || data.pts || '',
      record: data.record || data.wdl || data.winLoss || '',
      form: data.form || data.lastFive || '',
      sourceTitle: data.sourceTitle || data.source || '',
      sourceUrl: data.sourceUrl || data.url || '',
      updatedAt: data.updatedAt || '',
      statLabels: data.statLabels || null,
      localOnly: !!data.localOnly
    };
  }

  function normalizeStandingDisplay(value) {
    if (typeof value === 'string') return { summary: value };
    return value || { summary: 'Standing unavailable.' };
  }

  function refreshTeamStandings() {
    getFollowedTeamSummaries().forEach(function (team) {
      delete state.teamStandings[team.key];
      delete state.teamStandingsLoading[team.key];
      loadTeamStanding(team);
    });
    renderTeamHubView();
    collectFocusables(true);
  }

  function createEmptyState(message) {
    var empty = document.createElement('div');
    empty.className = 'empty-state';
    empty.textContent = message;
    return empty;
  }

  function createSmallEmptyState(message) {
    var empty = document.createElement('div');
    empty.className = 'team-manager-empty';
    empty.textContent = message;
    return empty;
  }

  function renderCricketRails(matches, target) {
    target = target || els.matchesGrid;
    var favourites = matches.filter(isFavourite).sort(rankMatches);
    if (favourites.length) {
      target.appendChild(createRail('Favourites', favourites));
    }

    groupByLeague(matches).forEach(function (group) {
      var expanded = isLeagueExpanded(group.name);
      target.appendChild(createRail(group.name, orderLeagueMatches(group.matches, expanded), {
        leagueName: group.name,
        totalCount: group.matches.length,
        favouriteLeague: isLeagueFavourite(group.name),
        expanded: expanded
      }));
    });
  }

  function groupByLeague(matches) {
    var groups = [];
    var byName = {};
    matches.forEach(function (match) {
      var name = match.competition || 'Other cricket';
      if (!byName[name]) {
        byName[name] = { name: name, matches: [] };
        groups.push(byName[name]);
      }
      byName[name].matches.push(match);
    });
    return groups.sort(function (a, b) {
      var aFav = isLeagueFavourite(a.name) ? 1 : 0;
      var bFav = isLeagueFavourite(b.name) ? 1 : 0;
      var aLive = a.matches.filter(function (match) { return match.status === 'live'; }).length;
      var bLive = b.matches.filter(function (match) { return match.status === 'live'; }).length;
      return bFav - aFav || bLive - aLive || a.name.localeCompare(b.name);
    });
  }

  function orderLeagueMatches(matches, expanded) {
    var live = matches.filter(function (match) { return match.status === 'live'; }).sort(rankMatches);
    var scheduled = matches.filter(function (match) { return match.status !== 'live' && match.status !== 'recent'; }).sort(sortOldestFirst);
    var recent = matches.filter(function (match) { return match.status === 'recent'; }).sort(sortNewestFirst);
    if (!expanded) recent = recent.slice(0, 4);
    return live.concat(scheduled, recent);
  }

  function orderLiveScheduledRecent(a, b) {
    var statusDelta = getStatusWeight(a) - getStatusWeight(b);
    if (statusDelta) return statusDelta;

    var teamDelta = getTeamFavouriteWeight(b) - getTeamFavouriteWeight(a);
    if (teamDelta) return teamDelta;

    if (a.status === 'live') return rankMatches(a, b);
    if (a.status === 'recent') return sortNewestFirst(a, b);
    return sortOldestFirst(a, b);
  }

  function getTeamFavouriteWeight(match) {
    return matchHasFavouriteTeam(match) ? 1 : 0;
  }

  function getStatusWeight(match) {
    if (match.status === 'live') return 0;
    if (match.status !== 'recent') return 1;
    return 2;
  }

  function sortNewestFirst(a, b) {
    return new Date(b.startTime).getTime() - new Date(a.startTime).getTime();
  }

  function sortOldestFirst(a, b) {
    return new Date(a.startTime).getTime() - new Date(b.startTime).getTime();
  }

  function createRail(title, matches, options) {
    options = options || {};
    var section = document.createElement('section');
    section.className = 'match-rail';
    var header = document.createElement('div');
    header.className = 'match-rail-heading';
    var heading = document.createElement('h2');
    heading.className = 'match-rail-title';
    heading.textContent = title;
    header.appendChild(heading);

    if (options.leagueName) {
      var favButton = document.createElement('button');
      favButton.className = 'league-fav-button' + (options.favouriteLeague ? ' active' : '');
      favButton.type = 'button';
      favButton.dataset.focusable = 'true';
      favButton.dataset.leagueName = options.leagueName;
      favButton.textContent = options.favouriteLeague ? '★' : '☆';
      favButton.setAttribute('aria-label', 'Favourite ' + options.leagueName);
      favButton.addEventListener('click', function () { toggleLeagueFavourite(options.leagueName); });
      header.appendChild(favButton);
    }

    var row = document.createElement('div');
    row.className = 'matches-row';
    var visibleMatches = options.expanded ? matches : matches.slice(0, 10);
    var fragment = document.createDocumentFragment();
    visibleMatches.forEach(function (match) {
      fragment.appendChild(createMatchCard(match, 'match-card'));
    });

    if (options.leagueName && !options.expanded && options.totalCount > visibleMatches.length) {
      fragment.appendChild(createViewAllCard(options.leagueName, options.totalCount - visibleMatches.length));
    }
    row.appendChild(fragment);

    section.appendChild(header);
    section.appendChild(row);
    return section;
  }

  function createViewAllCard(leagueName, remainingCount) {
    var button = document.createElement('button');
    button.className = 'match-card view-all-card';
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.leagueName = leagueName;
    button.innerHTML = '<div class="view-all-content"><strong>View all</strong><span>' + remainingCount + ' more</span></div>';
    button.addEventListener('click', function () { expandLeague(leagueName); });
    return button;
  }

  function renderHero(match) {
    els.heroFeature.innerHTML = '';
    if (!match) return;

    var button = document.createElement('div');
    button.className = 'hero-button';
    button.dataset.focusable = 'true';
    button.dataset.matchId = match.id;
    button.setAttribute('role', 'button');
    button.setAttribute('tabindex', '0');
    button.innerHTML = [
      '<div class="hero-art hero-art-' + escapeHtml(match.sport) + '">' + (match.posterUrl ? '<img class="hero-poster-img" src="' + escapeHtml(match.posterUrl) + '" alt="' + escapeHtml(match.title) + '" width="1280" height="720" loading="' + (IS_TV_RUNTIME ? 'lazy' : 'eager') + '" decoding="async" onerror="this.hidden=true">' : '') + '</div>',
      '<div class="hero-copy">',
      '<p class="hero-kicker">' + (match.status === 'live' ? 'Live now' : 'Featured match') + '</p>',
      '<h2 class="hero-title">' + escapeHtml(match.title) + '</h2>',
      '<div class="hero-meta">',
      '<span class="maturity">' + (match.status === 'live' ? 'LIVE' : escapeHtml(match.status)) + '</span>',
      '<span>' + escapeHtml(match.competition) + '</span>',
      '<span>' + formatStart(match.startTime) + '</span>',
      '<span>' + escapeHtml(match.scoreText || match.resultText || (match.streams.length + ' source' + (match.streams.length === 1 ? '' : 's'))) + '</span>',
      '</div>',
      '<p class="hero-selection-note">' + (match.status === 'live' ? 'Selected from the row below. Press OK to choose a stream.' : 'Selected from the row below.') + '</p>',
      renderHeroTeamActions(match),
      match.status === 'live' ? '<button class="play-chip hero-play-button" type="button" data-focusable="true" aria-label="View streams for ' + escapeHtml(match.title) + '"><span class="play-icon">▶</span> View streams</button>' : '',
      '</div>'
    ].join('');
    button.addEventListener('click', function () {
      state.userInteracted = true;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, true);
      playFullMatch(match);
    });
    button.addEventListener('focus', function () {
      state.refreshMatchId = match.id;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, !IS_TV_RUNTIME);
      if (!IS_TV_RUNTIME) preloadStreamsForMatch(match);
    });
    els.heroFeature.appendChild(button);
    bindHeroControls();
    bindHeroPlayControl(match);
    bindHeroTeamControls();
  }

  function bindHeroPlayControl(match) {
    var playButton = els.heroFeature.querySelector('.hero-play-button');
    if (!playButton) return;
    playButton.addEventListener('click', function (event) {
      event.stopPropagation();
      state.userInteracted = true;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, true);
      playFullMatch(match);
    });
  }

  function renderHeroTeamActions(match) {
    var teams = getMatchTeams(match);
    if (!teams.length) return '';
    return '<div class="hero-team-actions">' + teams.map(function (team) {
      var key = getTeamKey(team, match);
      var active = isTeamFavourite(team, match);
      return '<button class="team-fav-button' + (active ? ' active' : '') + '" type="button" data-focusable="true" data-team-key="' + escapeHtml(key) + '" data-team-name="' + escapeHtml(team.name || 'Team') + '">' + (active ? 'Following ' : 'Follow ') + escapeHtml(team.name || 'Team') + '</button>';
    }).join('') + '</div>';
  }

  function bindHeroTeamControls() {
    Array.prototype.slice.call(els.heroFeature.querySelectorAll('[data-team-key]')).forEach(function (button) {
      button.addEventListener('click', function (event) {
        event.stopPropagation();
        toggleTeamFavouriteByKey(button.dataset.teamKey, button.dataset.teamName || 'team');
      });
    });
  }

  function getMatchTeams(match) {
    return [match.teams && match.teams.home, match.teams && match.teams.away].filter(Boolean);
  }

  function createMatchCard(match, className) {
    var button = document.createElement('button');
    var streams = getStreamsForMatch(match);
    var streamCount = streams.length;
    var apiSourceCount = (match.streamSources || []).length;
    button.className = className;
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.matchId = match.id;
    button.disabled = streamCount === 0 && apiSourceCount === 0 && !match.scoreText && !match.resultText;

    if (className === 'match-card') {
      button.innerHTML = getMatchCardHtml(match);
    }

    button.addEventListener('click', function () {
      state.userInteracted = true;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, true);
      playFullMatch(match);
    });
    button.addEventListener('focus', function () {
      state.refreshMatchId = match.id;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, !IS_TV_RUNTIME);
      if (!IS_TV_RUNTIME) preloadStreamsForMatch(match);
    });
    button.addEventListener('mouseenter', function () {
      state.userInteracted = true;
      var index = state.focusables.indexOf(button);
      if (index >= 0) state.focusedIndex = index;
      focusNode(button);
      state.refreshMatchId = match.id;
      state.selectedMatchId = match.id;
      selectHeroMatch(match, true);
      if (!IS_TV_RUNTIME) preloadStreamsForMatch(match);
    });
    return button;
  }

  function updateMatchDom(match) {
    Array.prototype.slice.call(document.querySelectorAll('[data-match-id="' + cssEscape(match.id) + '"]')).forEach(function (button) {
      if (button.className.indexOf('match-card') >= 0) {
        button.innerHTML = getMatchCardHtml(match);
      }
    });
  }

  function getMatchCardHtml(match) {
    var streamCount = getStreamsForMatch(match).length;
    var apiSourceCount = (match.streamSources || []).length;
    return [
      '<div class="poster poster-' + escapeHtml(match.sport) + '">',
      match.posterUrl ? '<img class="poster-img" src="' + escapeHtml(match.posterUrl) + '" alt="' + escapeHtml(match.title) + '" width="320" height="180" loading="lazy" decoding="async" onerror="this.hidden=true">' : '',
      '<span class="poster-live">' + (match.status === 'live' ? 'LIVE' : escapeHtml(match.status)) + '</span>',
      renderTeamBadges(match),
      '<span class="poster-sport">' + escapeHtml(match.sport) + '</span>',
      '<span class="fav-badge">' + (matchHasFavouriteTeam(match) ? '★' : (isFavourite(match) ? '★' : '☆')) + '</span>',
      '<span class="score-badge">' + escapeHtml(match.scoreText || '') + '</span>',
      '</div>',
      '<div class="tile-copy">',
      '<strong>' + escapeHtml(match.title) + '</strong>',
      '<span class="competition">' + escapeHtml(match.competition) + '</span>',
      '<span class="score-line">' + escapeHtml(match.scoreText || match.resultText || match.venue || '') + '</span>',
      '<span class="source-row">' + (streamCount ? streamCount + ' stream' + (streamCount === 1 ? '' : 's') : apiSourceCount ? apiSourceCount + ' backend source' + (apiSourceCount === 1 ? '' : 's') : 'Score listing') + '</span>',
      '</div>'
    ].join('');
  }

  function renderTeamBadges(match) {
    var home = match.teams && match.teams.home;
    var away = match.teams && match.teams.away;
    if (!home && !away) return '';
    return '<span class="team-badges">' + [home, away].filter(Boolean).map(function (team) {
      var src = team.badgeUrl || team.badge || '';
      if (!src) return '';
      return '<img class="' + (isTeamFavourite(team, match) ? 'team-favourite' : '') + '" src="' + escapeHtml(src) + '" alt="' + escapeHtml(team.name || 'Team') + '" width="50" height="50" loading="lazy" decoding="async" onerror="this.hidden=true">';
    }).filter(Boolean).join('') + '</span>';
  }

  function findMatchById(matchId) {
    return state.matchesById[matchId] || null;
  }

  function preloadStreamsForMatch(match) {
    if (!match.streamSources || !match.streamSources.length || getStreamsForMatch(match).length || match.loadingStreams) return;
    match.loadingStreams = true;
    loadBackendStreamsForMatch(match).finally(function () { match.loadingStreams = false; });
  }

  function playFullMatch(match) {
    if (state.heroRenderTimer) {
      clearTimeout(state.heroRenderTimer);
      state.heroRenderTimer = null;
    }
    state.selectedMatchId = match.id;
    openStreamPicker(match);
  }

  function openStreamPicker(match) {
    if (!match) return;
    state.streamPickerMatchId = match.id;
    state.streamPickerSelectedStreamKey = '';
    state.streamPickerLoading = false;
    state.streamPickerOpen = true;
    els.streamPickerOverlay.removeAttribute('hidden');
    els.streamPickerOverlay.hidden = false;
    renderStreamPickerView(match);
    collectFocusables(true);
    focusStreamPickerPrimary();

    if (match.status !== 'live') {
      setStatus('Scheduled match selected.');
      return;
    }

    if (getStreamsForMatch(match).length) {
      setStatus('Choose a stream to start playback.');
      return;
    }

    if (match.streamSources && match.streamSources.length) {
      state.streamPickerLoading = true;
      renderStreamPickerView(match);
      collectFocusables(true);
      focusNode(els.streamPickerCloseButton);
      setStatus('Loading available streams...');
      loadBackendStreamsForMatch(match).then(function (updatedMatch) {
        if (!state.streamPickerOpen || state.streamPickerMatchId !== updatedMatch.id) return;
        state.streamPickerLoading = false;
        renderStreamPickerView(updatedMatch);
        collectFocusables(true);
        focusStreamPickerPrimary();
        setStatus(getOrderedPlayableStreams(getStreamsForMatch(updatedMatch)).length ? 'Choose a stream to start playback.' : 'No streams are available for this match.');
      }).catch(function () {
        if (!state.streamPickerOpen || state.streamPickerMatchId !== match.id) return;
        state.streamPickerLoading = false;
        renderStreamPickerView(match);
        collectFocusables(true);
        focusNode(els.streamPickerCloseButton);
        setStatus('Could not load streams for this match.');
      });
      return;
    }

    setStatus('No stream sources are available for this match.');
  }

  function closeStreamPicker() {
    var matchId = state.streamPickerMatchId || state.selectedMatchId || state.heroMatchId;
    state.streamPickerOpen = false;
    state.streamPickerLoading = false;
    state.streamPickerMatchId = '';
    state.streamPickerSelectedStreamKey = '';
    els.streamPickerOverlay.setAttribute('hidden', '');
    els.streamPickerOverlay.hidden = true;
    restoreListingFocus(matchId);
  }

  function restoreStreamPickerFromPlayer() {
    var match = findMatchById(state.streamPickerMatchId || state.playbackMatchId || state.selectedMatchId || state.heroMatchId);
    if (!match) {
      restoreListingFocus(state.selectedMatchId || state.heroMatchId || state.playbackMatchId);
      return;
    }
    state.streamPickerOpen = true;
    state.streamPickerLoading = false;
    state.streamPickerMatchId = match.id;
    els.streamPickerOverlay.removeAttribute('hidden');
    els.streamPickerOverlay.hidden = false;
    renderStreamPickerView(match);
    collectFocusables(true);
    focusStreamPickerPrimary();
    setStatus('Choose a stream to start playback.');
  }

  function restoreListingFocus(matchId) {
    render(true);
    state.focusedKey = matchId ? 'tile:' + matchId : state.focusedKey;
    collectFocusables(true);
    if (matchId) {
      focusMatchById(matchId);
      ensureFocusVisible(document.activeElement);
      return;
    }
    focusCurrent();
  }

  function renderStreamPickerView(match) {
    if (!els.streamPickerBody) return;
    els.streamPickerLeague.textContent = match ? match.competition : '';
    els.streamPickerTitle.textContent = match ? match.title : 'Available streams';
    els.streamPickerBody.innerHTML = '';
    if (!match) {
      els.streamPickerBody.innerHTML = '<div class="empty-state">Match details are unavailable.</div>';
      return;
    }

    var layout = document.createElement('div');
    layout.className = 'stream-picker-layout';
    layout.appendChild(createStreamPickerSummaryCard(match));

    var optionsPanel = document.createElement('section');
    optionsPanel.className = 'stream-options-panel';

    var streams = getOrderedPlayableStreams(getStreamsForMatch(match));
    var header = document.createElement('div');
    header.className = 'stream-options-header';
    header.innerHTML = [
      '<div>',
      '<h3>Available streams</h3>',
      '<p>' + escapeHtml(match.status === 'live' ? 'Pick the feed you want to open. Back returns to the match rows.' : 'Streams will appear here when this match goes live.') + '</p>',
      '</div>',
      '<span class="stream-options-count">' + (state.streamPickerLoading ? '...' : String(streams.length)) + '</span>'
    ].join('');
    optionsPanel.appendChild(header);

    if (state.streamPickerLoading) {
      appendStreamPickerEmptyState(optionsPanel, 'Loading available streams...');
      layout.appendChild(optionsPanel);
      els.streamPickerBody.appendChild(layout);
      return;
    }

    if (!streams.length) {
      appendStreamPickerEmptyState(optionsPanel, match.status === 'live' ? 'No playable streams are available for this match yet.' : 'No streams are available for this scheduled match yet.');
      layout.appendChild(optionsPanel);
      els.streamPickerBody.appendChild(layout);
      return;
    }

    var list = document.createElement('div');
    list.className = 'stream-option-list';
    streams.forEach(function (stream, index) {
      list.appendChild(createStreamOptionButton(match, stream, streams, index));
    });
    optionsPanel.appendChild(list);
    layout.appendChild(optionsPanel);
    els.streamPickerBody.appendChild(layout);
  }

  function createStreamPickerSummaryCard(match) {
    var card = document.createElement('section');
    var teamNames = getMatchTeams(match).map(function (team) { return team.name || 'Team'; }).join(' vs ');
    card.className = 'stream-match-card';
    card.innerHTML = [
      '<div class="stream-match-art">' + (match.posterUrl ? '<img class="stream-match-art-img" src="' + escapeHtml(match.posterUrl) + '" alt="' + escapeHtml(match.title) + '" width="640" height="360" loading="eager" decoding="async" onerror="this.hidden=true">' : '') + '<div class="stream-match-art-fallback">' + escapeHtml(toTitleCase(match.sport)) + '</div></div>',
      '<div class="stream-match-copy">',
      '<div class="stream-picker-status-row">',
      '<span class="maturity">' + escapeHtml(match.status === 'live' ? 'LIVE' : match.status) + '</span>',
      '<span>' + escapeHtml(formatStart(match.startTime)) + '</span>',
      '</div>',
      '<h3>' + escapeHtml(teamNames || match.title) + '</h3>',
      '<p class="stream-picker-copy">' + escapeHtml(match.scoreText || match.resultText || match.venue || (match.status === 'live' ? 'Open the stream that looks best for this match.' : 'Playback will appear here when the match goes live.')) + '</p>',
      '<div class="stream-match-meta">',
      '<span>' + escapeHtml(match.competition || toTitleCase(match.sport)) + '</span>',
      '<span>' + escapeHtml(match.sport) + '</span>',
      '</div>',
      '</div>'
    ].join('');
    return card;
  }

  function appendStreamPickerEmptyState(container, message) {
    var empty = document.createElement('div');
    empty.className = 'empty-state stream-picker-empty';
    empty.textContent = message;
    container.appendChild(empty);
  }

  function createStreamOptionButton(match, stream, streams, index) {
    var button = document.createElement('button');
    var badges = getStreamOptionBadges(stream);
    var streamKey = getStreamKey(stream);
    var selected = state.streamPickerSelectedStreamKey && state.streamPickerSelectedStreamKey === streamKey;
    button.className = 'stream-option-button' + (selected ? ' is-selected' : '') + (index === 0 ? ' is-recommended' : '');
    button.type = 'button';
    button.dataset.focusable = 'true';
    button.dataset.streamKey = streamKey;
    button.innerHTML = [
      '<span class="stream-option-index">' + (index + 1) + '</span>',
      '<div class="stream-option-copy">',
      '<div class="stream-option-topline"><strong>' + escapeHtml(getStreamOptionTitle(stream, index)) + '</strong>' + (index === 0 ? '<span class="stream-option-pill recommended">Recommended</span>' : '') + '</div>',
      '<div class="stream-option-badges">' + badges.map(function (badge) { return '<span class="stream-option-pill">' + escapeHtml(badge) + '</span>'; }).join('') + '</div>',
      '<span class="stream-option-summary">' + escapeHtml(getStreamOptionSummary(stream)) + '</span>',
      '</div>'
    ].join('');
    button.addEventListener('click', function () {
      state.userInteracted = true;
      setStatus('Opening ' + formatStreamLabel(stream) + '...');
      state.streamPickerSelectedStreamKey = streamKey;
      state.streamPickerOpen = false;
      els.streamPickerOverlay.setAttribute('hidden', '');
      els.streamPickerOverlay.hidden = true;
      playMatch(match, stream, streams, index);
    });
    return button;
  }

  function getStreamOptionTitle(stream, index) {
    var parts = [];
    if (stream.language) parts.push(stream.language);
    if (stream.hd) parts.push('HD');
    if (!parts.length) parts.push('Stream ' + (index + 1));
    return parts.join(' ') + (stream.streamNo ? ' #' + stream.streamNo : '');
  }

  function getStreamOptionBadges(stream) {
    var badges = [];
    if (stream.type === 'hls') badges.push('Best for TV');
    else if (stream.type === 'mp4') badges.push('Direct playback');
    else badges.push('Provider player');
    if (stream.source) badges.push(toTitleCase(stream.source));
    if (stream.language) badges.push(stream.language);
    if (stream.hd) badges.push('HD');
    return badges;
  }

  function getStreamOptionSummary(stream) {
    if (stream.type === 'hls' || stream.type === 'mp4') return 'Starts in the built-in player with direct playback.';
    return 'Opens the provider stream and may take an extra moment to start.';
  }

  function focusStreamPickerPrimary() {
    collectFocusables(true);
    var preferred = state.streamPickerSelectedStreamKey
      ? els.streamPickerOverlay.querySelector('[data-stream-key="' + cssEscape(state.streamPickerSelectedStreamKey) + '"]')
      : null;
    var target = preferred || els.streamPickerOverlay.querySelector('.stream-option-button') || els.streamPickerCloseButton;
    if (target) focusNode(target);
  }

  function playMatchFromTile(match) {
    if (match.status !== 'live') {
      stopHeroPreview();
      setStatus('Scheduled match selected. Preview will start when it is live.');
      return;
    }

    if (getStreamsForMatch(match).length) {
      playHeroPreview(match);
      return;
    }

    if (match.streamSources && match.streamSources.length) {
      setStatus('Loading live preview...');
      loadBackendStreamsForMatch(match).then(function (updatedMatch) {
        playHeroPreview(updatedMatch);
      }).catch(function () {
        setStatus('Could not load preview for this match.');
      });
      return;
    }

    stopHeroPreview();
    setStatus('No stream sources are available for this match.');
  }

  function loadBackendStreamsForMatch(match) {
    if (!window.StreamedAPI || !match.streamSources || !match.streamSources.length) return Promise.resolve(match);
    if (getStreamsForMatch(match).length) return Promise.resolve(match);

    return Promise.all(match.streamSources.map(function (item) {
      return window.StreamedAPI.getStreamsBySource(item.source, item.id).catch(function () { return []; });
    })).then(function (groups) {
      var streams = groups.reduce(function (all, group) { return all.concat(group); }, []).map(mapBackendStream).filter(function (stream) { return stream.url; });
      if (!streams.length) return match;
      return resolveStreamList(streams).then(function (resolvedStreams) {
        match.streams = resolvedStreams;
        updateMatchDom(match);
        return match;
      });
    });
  }

  function mapBackendStream(stream) {
    var embedUrl = stream.embedUrl || stream.embed || stream.iframe || '';
    var directCandidates = getDirectStreamCandidates(stream, embedUrl);
    var directUrl = directCandidates[0] || '';
    var url = directUrl || embedUrl;
    return {
      label: [stream.source, stream.language, stream.hd ? 'HD' : '', stream.streamNo ? '#' + stream.streamNo : ''].filter(Boolean).join(' '),
      type: getStreamType(url, !!embedUrl && !directUrl),
      url: url,
      embedUrl: embedUrl,
      fallbackUrl: directUrl && embedUrl ? embedUrl : '',
      directCandidates: directCandidates,
      resolvedDirectUrl: directUrl || '',
      source: stream.source,
      id: stream.id,
      language: stream.language || '',
      hd: !!stream.hd,
      streamNo: Number(stream.streamNo || stream.stream || stream.no || 0) || 0
    };
  }

  function getDirectStreamUrl(stream, embedUrl) {
    return getDirectStreamCandidates(stream, embedUrl)[0] || '';
  }

  function getDirectStreamCandidates(stream, embedUrl) {
    var candidates = [
      stream.playbackUrl,
      stream.directUrl,
      stream.hlsUrl,
      stream.hls,
      stream.m3u8,
      stream.mp4,
      stream.file,
      stream.src,
      stream.url
    ];
    if (embedUrl) candidates.push(embedUrl);
    if (stream.urls) {
      candidates = candidates.concat([stream.urls.hls, stream.urls.m3u8, stream.urls.mp4, stream.urls.direct, stream.urls.playback]);
    }
    if (Array.isArray(stream.sources)) {
      stream.sources.forEach(function (source) {
        candidates.push(source.url || source.src || source.file);
      });
    }
    return extractDirectVideoCandidates(candidates, embedUrl || stream.url || '');
  }

  function isDirectVideoUrl(url) {
    return /\.(m3u8|mp4)(\?|$)/i.test(String(url || ''));
  }

  function getStreamType(url, isEmbedOnly) {
    if (isEmbedOnly) return 'embed';
    if (/\.m3u8(\?|$)/i.test(url)) return 'hls';
    if (/\.mp4(\?|$)/i.test(url)) return 'mp4';
    return 'embed';
  }

  function extractDirectVideoCandidates(values, baseUrl) {
    var results = [];
    var seen = {};
    (Array.isArray(values) ? values : [values]).forEach(function (value) {
      appendDirectVideoCandidates(value, baseUrl, results, seen, 0);
    });
    return results;
  }

  function appendDirectVideoCandidates(value, baseUrl, results, seen, depth) {
    if (value == null || depth > 3) return;
    if (Array.isArray(value)) {
      value.forEach(function (item) {
        appendDirectVideoCandidates(item, baseUrl, results, seen, depth + 1);
      });
      return;
    }

    var text = cleanupCandidateText(value);
    if (!text) return;

    pushDirectVideoCandidate(text, baseUrl, results, seen);
    extractDirectVideoUrlsFromText(text, baseUrl).forEach(function (candidate) {
      pushDirectVideoCandidate(candidate, baseUrl, results, seen);
    });
    extractDirectVideoUrlsFromParams(text, baseUrl).forEach(function (candidate) {
      pushDirectVideoCandidate(candidate, baseUrl, results, seen);
    });

    var decoded = safeDecodeCandidate(text);
    if (decoded && decoded !== text) appendDirectVideoCandidates(decoded, baseUrl, results, seen, depth + 1);

    var decodedBase64 = safeDecodeBase64Candidate(text);
    if (decodedBase64 && decodedBase64 !== text) appendDirectVideoCandidates(decodedBase64, baseUrl, results, seen, depth + 1);
  }

  function cleanupCandidateText(value) {
    return String(value || '')
      .trim()
      .replace(/^["']|["']$/g, '')
      .replace(/&amp;/g, '&')
      .replace(/\\u0026/gi, '&')
      .replace(/\\u002f/gi, '/')
      .replace(/\\\//g, '/');
  }

  function pushDirectVideoCandidate(value, baseUrl, results, seen) {
    var normalized = normalizeDirectVideoUrl(value, baseUrl);
    if (!normalized || !isDirectVideoUrl(normalized) || seen[normalized]) return;
    seen[normalized] = true;
    results.push(normalized);
  }

  function normalizeDirectVideoUrl(value, baseUrl) {
    var text = cleanupCandidateText(value);
    if (!text) return '';
    if (text.indexOf('//') === 0) return window.location.protocol + text;
    if (/^https?:\/\//i.test(text)) return text;
    if (/^[/.]/.test(text) && baseUrl) {
      try {
        return new URL(text, baseUrl).toString();
      } catch (error) {
        return text;
      }
    }
    return text;
  }

  function extractDirectVideoUrlsFromText(text, baseUrl) {
    var results = [];
    var seen = {};
    var pattern = /((?:https?:)?\/\/[^"'\\\s<>]+?\.(?:m3u8|mp4)(?:[^"'\\\s<>]*)|(?:\/|\.\.?\/)[^"'\\\s<>]+?\.(?:m3u8|mp4)(?:[^"'\\\s<>]*)?)/ig;
    var match = pattern.exec(text);
    while (match) {
      pushDirectVideoCandidate(match[1], baseUrl, results, seen);
      match = pattern.exec(text);
    }
    return results;
  }

  function extractDirectVideoUrlsFromParams(text, baseUrl) {
    var values = [];
    var seen = {};
    function collect(params) {
      params.forEach(function (value) {
        if (!value || seen[value]) return;
        seen[value] = true;
        values.push(value);
      });
    }

    try {
      if (/^https?:\/\//i.test(text) || text.indexOf('//') === 0) {
        var url = new URL(text.indexOf('//') === 0 ? window.location.protocol + text : text);
        collect(Array.from(url.searchParams.values()));
        if (url.hash && url.hash.indexOf('=') >= 0) collect(Array.from(new URLSearchParams(url.hash.replace(/^#/, '')).values()));
      }
    } catch (error) {}

    if (text.indexOf('?') >= 0) {
      collect(Array.from(new URLSearchParams(text.split('?')[1].split('#')[0]).values()));
    }
    if (text.indexOf('#') >= 0 && text.split('#')[1].indexOf('=') >= 0) {
      collect(Array.from(new URLSearchParams(text.split('#')[1]).values()));
    }

    return extractDirectVideoCandidates(values, baseUrl);
  }

  function safeDecodeCandidate(text) {
    var value = cleanupCandidateText(text);
    if (!/%[0-9a-f]{2}/i.test(value)) return value;
    try {
      return decodeURIComponent(value);
    } catch (error) {
      return value;
    }
  }

  function safeDecodeBase64Candidate(text) {
    var value = cleanupCandidateText(text).replace(/-/g, '+').replace(/_/g, '/');
    if (!/^[A-Za-z0-9+/=]+$/.test(value) || value.length < 16 || value.length % 4 === 1) return '';
    try {
      return atob(value);
    } catch (error) {
      return '';
    }
  }

  function getStreamResolutionKey(stream) {
    stream = stream || {};
    return [stream.source || '', stream.id || '', stream.embedUrl || stream.url || '', stream.streamNo || 0].join('|');
  }

  function resolveStreamList(streams) {
    var resolvedStreams = (streams || []).map(function (stream) {
      return applyResolvedDirectUrl(stream, stream.directCandidates && stream.directCandidates[0], 'metadata');
    });
    var embedStreams = resolvedStreams.filter(function (stream) {
      return stream.type === 'embed' && (stream.embedUrl || stream.url);
    }).slice(0, 4);
    if (!embedStreams.length) return Promise.resolve(resolvedStreams);
    return Promise.all(embedStreams.map(resolveDirectPlaybackForStream)).then(function () {
      return resolvedStreams;
    });
  }

  function applyResolvedDirectUrl(stream, directUrl, source) {
    var normalized = normalizeDirectVideoUrl(directUrl, stream && (stream.embedUrl || stream.url || ''));
    if (!stream || !normalized || !isDirectVideoUrl(normalized)) return stream;
    if (!stream.fallbackUrl && stream.url && stream.type === 'embed') stream.fallbackUrl = stream.url;
    stream.resolvedDirectUrl = normalized;
    stream.resolutionSource = source || stream.resolutionSource || '';
    stream.url = normalized;
    stream.type = getStreamType(normalized, false);
    return stream;
  }

  function resolveDirectPlaybackForStream(stream) {
    if (!stream || stream.type !== 'embed' || !(stream.embedUrl || stream.url)) return Promise.resolve(stream);
    var cacheKey = getStreamResolutionKey(stream);
    var cached = state.streamResolutionCache[cacheKey];
    if (cached) return Promise.resolve(applyResolvedDirectUrl(stream, cached, 'cache'));
    if (state.streamResolutionPending[cacheKey]) return state.streamResolutionPending[cacheKey].then(function (directUrl) {
      return applyResolvedDirectUrl(stream, directUrl, 'cache');
    });

    var resolverPromise = fetchDirectUrlFromEmbed(stream.embedUrl || stream.url).then(function (directUrl) {
      if (directUrl) state.streamResolutionCache[cacheKey] = directUrl;
      return directUrl;
    }).catch(function () {
      return '';
    }).then(function (directUrl) {
      delete state.streamResolutionPending[cacheKey];
      return applyResolvedDirectUrl(stream, directUrl, directUrl ? 'embed-page' : '');
    });

    state.streamResolutionPending[cacheKey] = resolverPromise.then(function (resolvedStream) {
      return resolvedStream && resolvedStream.resolvedDirectUrl ? resolvedStream.resolvedDirectUrl : '';
    });
    return resolverPromise;
  }

  function fetchDirectUrlFromEmbed(url) {
    if (!url) return Promise.resolve('');
    return withTimeout(fetch(url, {
      credentials: 'omit',
      referrerPolicy: 'no-referrer'
    }).then(function (response) {
      if (!response.ok) throw new Error('Embed request failed.');
      return response.text();
    }).then(function (text) {
      var candidates = extractDirectVideoUrlsFromMarkup(text, url);
      return candidates[0] || '';
    }), 2400);
  }

  function extractDirectVideoUrlsFromMarkup(markup, baseUrl) {
    var results = extractDirectVideoCandidates(markup, baseUrl);
    var html = String(markup || '');
    if (!html) return results;
    try {
      var doc = new DOMParser().parseFromString(html, 'text/html');
      Array.prototype.slice.call(doc.querySelectorAll('video[src], source[src], a[href], iframe[src]')).forEach(function (node) {
        var value = node.getAttribute('src') || node.getAttribute('href') || '';
        extractDirectVideoCandidates(value, baseUrl).forEach(function (candidate) {
          if (results.indexOf(candidate) < 0) results.push(candidate);
        });
      });
    } catch (error) {}
    return results;
  }

  function withTimeout(promise, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var settled = false;
      var timer = setTimeout(function () {
        if (settled) return;
        settled = true;
        reject(new Error('Timed out.'));
      }, timeoutMs);
      promise.then(function (value) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(value);
      }, function (error) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        reject(error);
      });
    });
  }

  function renderSourcePreview(streams) {
    if (!streams.length) return '<div class="source-preview">No streams configured</div>';
    return '<div class="source-preview">' + streams.slice(0, 3).map(function (stream) {
      return '<span>' + escapeHtml(stream.label || 'Stream') + ' - ' + escapeHtml(stream.type || 'video') + '</span>';
    }).join('') + '</div>';
  }

  function renderError(message) {
    setStatus(message);
    els.heroFeature.innerHTML = '';
    els.matchesGrid.innerHTML = '<div class="empty-state">' + escapeHtml(message) + '</div>';
  }

  function playPrimaryStream(match) {
    var streams = getOrderedPlayableStreams(getStreamsForMatch(match));
    var stream = streams[0];
    if (!stream) {
      setStatus('No playable stream is configured for this match.');
      return;
    }

    playMatch(match, stream, streams, 0);
  }

  function configureEmbedFrame(frame) {
    if (!frame) return;
    frame.removeAttribute('sandbox');
    frame.setAttribute('allow', 'autoplay; fullscreen; encrypted-media; picture-in-picture');
    frame.setAttribute('referrerpolicy', 'no-referrer');
    frame.setAttribute('tabindex', '-1');
    frame.tabIndex = -1;
  }

  function playHeroPreview(match) {
    if (match.status !== 'live') {
      stopHeroPreview();
      return;
    }

    var stream = selectPlayableStream(getStreamsForMatch(match));
    if (!stream) {
      setStatus('No playable preview is configured for this match.');
      return;
    }

    var hero = els.heroFeature.querySelector('.hero-button');
    var preview = els.heroFeature.querySelector('.hero-preview');
    var video = els.heroFeature.querySelector('.hero-preview-video');
    var frame = els.heroFeature.querySelector('.hero-preview-frame');
    if (!preview || !video || !frame) return;

    if (IS_TV_RUNTIME && stream.type === 'embed') {
      stopHeroPreview();
      setStatus('TV blocked autoplay preview. Press Play to open stream.');
      return;
    }

    state.previewMatchId = match.id;
    preview.classList.add('is-playing');
    if (hero) hero.classList.add('preview-playing');
    els.heroFeature.classList.add('preview-playing');

    if (stream.type === 'embed') {
      video.pause();
      video.removeAttribute('src');
      video.hidden = true;
      frame.hidden = false;
      configureEmbedFrame(frame);
      frame.onload = function () { nudgeEmbeddedPlayback(frame); };
      frame.src = withAutoplayParams(stream.url, true);
      nudgeEmbeddedPlayback(frame);
      setStatus('Loading embedded live preview...');
      return;
    }

    frame.removeAttribute('src');
    frame.hidden = true;
    video.hidden = false;
    video.controls = false;
    preparePreviewVideo(video);
    loadVideoPreviewSource(video, stream);
    requestPreviewPlay(video, stream, frame);
  }

  function preparePreviewVideo(video) {
    state.previewMuted = true;
    video.pause();
    video.autoplay = true;
    video.defaultMuted = true;
    video.muted = true;
    video.volume = 0;
    video.playsInline = true;
    video.setAttribute('autoplay', '');
    video.setAttribute('muted', '');
    video.setAttribute('playsinline', '');
    video.setAttribute('webkit-playsinline', '');
  }

  function requestPreviewPlay(video, stream, frame) {
    var promise = video.play && video.play();
    if (!promise || !promise.then) {
      setStatus('Loading live preview.');
      return;
    }
    promise.then(function () {
      setStatus('Playing muted live preview.');
    }).catch(function (error) {
      if (IS_TV_RUNTIME) {
        stopHeroPreview();
        var tvReason = error && error.name ? ' (' + error.name + ')' : '';
        setStatus('TV cannot autoplay preview' + tvReason + '. Press Play to open stream.');
        focusCurrent();
        return;
      }
      if (stream && stream.embedUrl && frame) {
        video.pause();
        video.removeAttribute('src');
        video.hidden = true;
        frame.hidden = false;
        configureEmbedFrame(frame);
        frame.onload = function () { nudgeEmbeddedPlayback(frame); };
        frame.src = withAutoplayParams(stream.embedUrl, true);
        nudgeEmbeddedPlayback(frame);
        setStatus('Using embedded live preview.');
        return;
      }
      var reason = error && error.name ? ' (' + error.name + ')' : '';
      setStatus('TV cannot preview this stream' + reason + '. Press Play to open stream.');
    });
  }

  function loadVideoPreviewSource(video, stream) {
    destroyPreviewHls();
    if (stream.type === 'hls' && canPlayNativeHls(video)) {
      video.src = stream.url;
      video.load();
      return;
    }
    if (stream.type === 'hls' && window.Hls && window.Hls.isSupported()) {
      state.previewHls = new window.Hls({ autoStartLoad: true, startPosition: -1 });
      state.previewHls.loadSource(stream.url);
      state.previewHls.attachMedia(video);
      state.previewHls.on(window.Hls.Events.MANIFEST_PARSED, function () {
        preparePreviewVideo(video);
        requestPreviewPlay(video, stream, null);
      });
      return;
    }
    video.src = stream.url;
    video.load();
  }

  function canPlayNativeHls(video) {
    return !!(video.canPlayType('application/vnd.apple.mpegurl') || video.canPlayType('application/x-mpegURL'));
  }

  function destroyPreviewHls() {
    if (state.previewHls) {
      if (state.previewHls.stopLoad) state.previewHls.stopLoad();
      if (state.previewHls.detachMedia) state.previewHls.detachMedia();
      state.previewHls.destroy();
      state.previewHls = null;
    }
  }

  function destroyPlayerHls() {
    if (state.playerHls) {
      if (state.playerHls.stopLoad) state.playerHls.stopLoad();
      if (state.playerHls.detachMedia) state.playerHls.detachMedia();
      state.playerHls.destroy();
      state.playerHls = null;
    }
  }

  function clearEmbeddedPlaybackTimers() {
    if (!state.embeddedPlaybackTimers || !state.embeddedPlaybackTimers.length) return;
    state.embeddedPlaybackTimers.forEach(function (timer) {
      clearTimeout(timer);
    });
    state.embeddedPlaybackTimers = [];
  }

  function clearPlaybackStartAttemptTimers() {
    if (!state.playbackStartAttemptTimers || !state.playbackStartAttemptTimers.length) return;
    state.playbackStartAttemptTimers.forEach(function (timer) {
      clearTimeout(timer);
    });
    state.playbackStartAttemptTimers = [];
  }

  function resetVideoPlaybackSurface(video) {
    if (!video) return;
    try {
      video.pause();
    } catch (error) {}
    try {
      video.removeAttribute('src');
      video.srcObject = null;
    } catch (error) {}
    try {
      video.load();
    } catch (error) {}
  }

  function resetEmbedPlaybackSurface(frame) {
    if (!frame) return;
    clearEmbeddedPlaybackTimers();
    try {
      postEmbedPauseCommands(frame);
    } catch (error) {}
    try {
      frame.onload = null;
    } catch (error) {}
    try {
      if (frame.contentWindow && frame.contentWindow.stop) frame.contentWindow.stop();
    } catch (error) {}
    try {
      frame.src = 'about:blank';
    } catch (error) {}
  }

  function teardownActivePlayback() {
    clearPlaybackStartAttemptTimers();
    destroyPlayerHls();
    teardownAvplayPlayback();
    state.playerEngine = '';
    state.embedPlaybackPaused = false;
    resetVideoPlaybackSurface(els.videoPlayer);
    resetEmbedPlaybackSurface(els.embedPlayer);
  }

  function hasAvplaySupport() {
    return !!(IS_TV_RUNTIME && els.avplayPlayer && window.webapis && webapis.avplay);
  }

  function safeGetAvplayState() {
    if (!hasAvplaySupport()) return 'NONE';
    try {
      return webapis.avplay.getState();
    } catch (error) {
      return 'NONE';
    }
  }

  function setAvplaySurfaceVisible(visible) {
    if (!els.avplayPlayer) return;
    if (visible) {
      els.avplayPlayer.hidden = false;
      els.avplayPlayer.removeAttribute('hidden');
      return;
    }
    els.avplayPlayer.hidden = true;
    els.avplayPlayer.setAttribute('hidden', '');
  }

  function syncAvplayDisplayRect() {
    if (!hasAvplaySupport()) return;
    var rect = els.avplayPlayer && els.avplayPlayer.getBoundingClientRect ? els.avplayPlayer.getBoundingClientRect() : null;
    var viewportWidth = document.documentElement.clientWidth || window.innerWidth || 1920;
    var viewportHeight = document.documentElement.clientHeight || window.innerHeight || 1080;
    var ratio = 1920 / Math.max(viewportWidth, 1);
    var x = 0;
    var y = 0;
    var width = 1920;
    var height = Math.max(1, Math.round(viewportHeight * ratio));

    if (rect && rect.width > 0 && rect.height > 0) {
      x = Math.max(0, Math.round(rect.left * ratio));
      y = Math.max(0, Math.round(rect.top * ratio));
      width = Math.max(1, Math.round(rect.width * ratio));
      height = Math.max(1, Math.round(rect.height * ratio));
    }

    try {
      webapis.avplay.setDisplayRect(x, y, width, height);
    } catch (error) {}

    try {
      webapis.avplay.setDisplayMethod('PLAYER_DISPLAY_MODE_FULL_SCREEN');
    } catch (error) {}
  }

  function applyAvplayStreamingHints(stream) {
    if (!hasAvplaySupport()) return;
    try {
      webapis.avplay.setTimeoutForBuffering(8);
    } catch (error) {}
    try {
      webapis.avplay.setBufferingParam('PLAYER_BUFFER_FOR_PLAY', 'PLAYER_BUFFER_SIZE_IN_SECOND', stream && stream.type === 'hls' ? 4 : 3);
    } catch (error) {}
    try {
      webapis.avplay.setBufferingParam('PLAYER_BUFFER_FOR_RESUME', 'PLAYER_BUFFER_SIZE_IN_SECOND', stream && stream.type === 'hls' ? 7 : 5);
    } catch (error) {}
    try {
      if (stream && stream.type === 'hls') {
        webapis.avplay.setStreamingProperty('ADAPTIVE_INFO', 'STARTBITRATE=LOWEST|FIXED_MAX_RESOLUTION=1920X1080');
      }
    } catch (error) {}
  }

  function teardownAvplayPlayback() {
    state.avplayReady = false;
    state.avplayBuffering = false;
    state.avplaySession += 1;
    setAvplaySurfaceVisible(false);
    if (!hasAvplaySupport()) return;
    try {
      webapis.avplay.setListener({});
    } catch (error) {}
    try {
      webapis.avplay.stop();
    } catch (error) {}
    try {
      webapis.avplay.close();
    } catch (error) {}
  }

  function loadPlayerVideoSource(video, stream, onReady) {
    destroyPlayerHls();
    if (stream.type === 'hls' && canPlayNativeHls(video)) {
      video.src = stream.url;
      video.load();
      onReady();
      return;
    }
    if (stream.type === 'hls' && window.Hls && window.Hls.isSupported()) {
      var readyCalled = false;
      function signalReady() {
        if (readyCalled) return;
        readyCalled = true;
        onReady();
      }
      state.playerHls = new window.Hls({ autoStartLoad: true, startPosition: -1 });
      state.playerHls.attachMedia(video);
      state.playerHls.on(window.Hls.Events.MEDIA_ATTACHED, function () {
        signalReady();
        state.playerHls.loadSource(stream.url);
      });
      video.load();
      state.playerHls.on(window.Hls.Events.MANIFEST_PARSED, function () {
        signalReady();
      });
      state.playerHls.on(window.Hls.Events.ERROR, function (event, data) {
        if (data && data.fatal) handlePlaybackStalled({ type: 'error' });
      });
      return;
    }
    video.src = stream.url;
    video.load();
    onReady();
  }

  function bindHeroControls() {
    syncHeroVolumeControls();
  }

  function applyHeroVolume() {
    var video = els.heroFeature.querySelector('.hero-preview-video');
    var frame = els.heroFeature.querySelector('.hero-preview-frame');
    if (video) {
      video.volume = state.previewVolume;
      video.muted = state.previewMuted;
    }
    if (frame && !frame.hidden) {
      postEmbedVolumeCommands(frame);
      trySetAccessibleEmbedVolume(frame);
    }
    syncHeroVolumeControls();
  }

  function syncHeroVolumeControls() {
    var toggle = els.heroFeature.querySelector('[data-volume-toggle="true"]');
    if (toggle) toggle.textContent = state.previewMuted ? 'Muted' : 'Sound on';
  }

  function nudgeEmbeddedPlayback(frame) {
    clearEmbeddedPlaybackTimers();
    [250, 900, 1800].forEach(function (delay) {
      state.embeddedPlaybackTimers.push(setTimeout(function () {
        if (state.embedPlaybackPaused) return;
        postEmbedPlayCommands(frame);
        tryClickEmbeddedPlay(frame);
        keepPlayerControlsFocused(frame);
      }, delay));
    });
  }

  function postEmbedPlayCommands(frame) {
    postEmbedVolumeCommands(frame);
    try {
      var target = frame.contentWindow;
      if (!target) return;
      target.postMessage({ event: 'command', func: 'playVideo', args: [] }, '*');
      target.postMessage({ method: 'play' }, '*');
      target.postMessage('play', '*');
    } catch (error) {
      // Cross-origin embeds may ignore player commands.
    }
  }

  function postEmbedVolumeCommands(frame) {
    try {
      var target = frame.contentWindow;
      if (!target) return;
      var volume100 = Math.round(state.previewVolume * 100);
      var volume1 = state.previewMuted ? 0 : state.previewVolume;
      target.postMessage({ event: 'command', func: state.previewMuted ? 'mute' : 'unMute', args: [] }, '*');
      target.postMessage({ event: 'command', func: 'setVolume', args: [volume100] }, '*');
      target.postMessage({ method: 'setVolume', value: volume1 }, '*');
      target.postMessage({ method: 'volume', value: volume1 }, '*');
      target.postMessage({ type: 'setVolume', volume: volume1, muted: state.previewMuted }, '*');
    } catch (error) {
      // Cross-origin embeds may ignore player commands.
    }
  }

  function trySetAccessibleEmbedVolume(frame) {
    try {
      var doc = frame.contentDocument || frame.contentWindow.document;
      collectFromRoot(doc, 'video, audio').forEach(function (media) {
        media.volume = state.previewVolume;
        media.muted = state.previewMuted;
      });
    } catch (error) {}
  }

  function tryClickEmbeddedPlay(frame) {
    try {
      var doc = frame.contentDocument || frame.contentWindow.document;
      var playedNativeVideo = playAccessibleVideos(doc);
      var target = findEmbeddedPlayTarget(doc);
      if (target) {
        fireUserLikeClick(target);
        setStatus('Playing embedded live preview.');
        return;
      }
      if (playedNativeVideo) {
        setStatus('Playing embedded live preview.');
        return;
      }
      clickDocumentCenter(doc);
    } catch (error) {
      setStatus('Embedded preview loaded. Provider may require a manual play gesture.');
    }
  }

  function playAccessibleVideos(root) {
    var played = false;
    collectFromRoot(root, 'video').forEach(function (video) {
      try {
        video.muted = state.previewMuted;
        video.volume = state.previewVolume;
        video.autoplay = true;
        video.playsInline = true;
        var promise = video.play && video.play();
        if (promise && promise.catch) promise.catch(function () {});
        played = true;
      } catch (error) {}
    });
    return played;
  }

  function findEmbeddedPlayTarget(root) {
    var selector = [
      'button',
      '[role="button"]',
      '.play',
      '.play-button',
      '.vjs-big-play-button',
      '.jw-icon-playback',
      '.plyr__control',
      '[aria-label*="play" i]',
      '[title*="play" i]',
      '[data-testid*="play" i]'
    ].join(',');
    return findEmbeddedControlTarget(root, /play|▶|►/i, true);
  }

  function findEmbeddedControlTarget(root, pattern, allowFallback) {
    var selector = [
      'button',
      '[role="button"]',
      '.play',
      '.play-button',
      '.vjs-big-play-button',
      '.jw-icon-playback',
      '.plyr__control',
      '[aria-label*="play" i]',
      '[aria-label*="pause" i]',
      '[title*="play" i]',
      '[title*="pause" i]',
      '[data-testid*="play" i]',
      '[data-testid*="pause" i]'
    ].join(',');
    var candidates = collectFromRoot(root, selector).filter(function (node) {
      if (!isVisibleNode(node)) return false;
      return pattern.test(node.getAttribute('aria-label') || node.getAttribute('title') || node.getAttribute('data-testid') || node.textContent || node.className || '');
    });
    return candidates[0] || (allowFallback ? collectFromRoot(root, selector).filter(isVisibleNode)[0] : null) || null;
  }

  function collectFromRoot(root, selector) {
    var results = [];
    function visit(scope) {
      try {
        results = results.concat(Array.prototype.slice.call(scope.querySelectorAll(selector)));
        Array.prototype.slice.call(scope.querySelectorAll('*')).forEach(function (node) {
          if (node.shadowRoot) visit(node.shadowRoot);
        });
      } catch (error) {}
    }
    visit(root);
    return results;
  }

  function isVisibleNode(node) {
    var rect = node.getBoundingClientRect && node.getBoundingClientRect();
    if (!rect || rect.width < 2 || rect.height < 2) return false;
    var style = node.ownerDocument.defaultView.getComputedStyle(node);
    return style.visibility !== 'hidden' && style.display !== 'none' && Number(style.opacity || 1) > 0;
  }

  function fireUserLikeClick(node) {
    ['pointerdown', 'mousedown', 'mouseup', 'pointerup', 'click'].forEach(function (type) {
      node.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: node.ownerDocument.defaultView }));
    });
    if (node.click) node.click();
  }

  function clickDocumentCenter(doc) {
    var win = doc.defaultView;
    var x = Math.round((win.innerWidth || 0) / 2);
    var y = Math.round((win.innerHeight || 0) / 2);
    var node = doc.elementFromPoint(x, y) || doc.body;
    if (node) fireUserLikeClick(node);
  }

  function stopHeroPreview() {
    if (state.autoPlayTimer) clearTimeout(state.autoPlayTimer);
    state.previewMatchId = '';
    var preview = els.heroFeature.querySelector('.hero-preview');
    var video = els.heroFeature.querySelector('.hero-preview-video');
    var frame = els.heroFeature.querySelector('.hero-preview-frame');
    var hero = els.heroFeature.querySelector('.hero-button');
    if (hero) hero.classList.remove('preview-playing');
    els.heroFeature.classList.remove('preview-playing');
    if (preview) preview.classList.remove('is-playing');
    destroyPreviewHls();
    if (video) {
      resetVideoPlaybackSurface(video);
      video.hidden = true;
    }
    if (frame) {
      resetEmbedPlaybackSurface(frame);
      frame.hidden = true;
    }
  }

  function selectPlayableStream(streams) {
    return getOrderedPlayableStreams(streams)[0] || null;
  }

  function getOrderedPlayableStreams(streams) {
    return (streams || []).slice().filter(function (stream) { return stream && stream.url; }).sort(function (a, b) {
      return getStreamScore(b) - getStreamScore(a);
    });
  }

  function getStreamScore(stream) {
    var type = stream.type || getStreamType(stream.url, false);
    var text = [stream.label, stream.language, stream.source, stream.url].join(' ').toLowerCase();
    var hlsSupport = hasAvplaySupport() || canPlayNativeHls(els.videoPlayer) || (window.Hls && window.Hls.isSupported());
    var score = 0;
    if (type === 'hls') score += hlsSupport ? 90 : 35;
    else if (type === 'mp4') score += 82;
    else score += 38;
    if (stream.hd || /\bhd\b|1080|720/.test(text)) score += 22;
    if (/1080/.test(text)) score += 10;
    if (/720/.test(text)) score += 7;
    if (/english|\beng\b/.test(text)) score += 8;
    if (stream.streamNo) score += Math.max(0, 10 - stream.streamNo);
    if (type === 'embed') score -= IS_TV_RUNTIME ? 18 : 8;
    return score;
  }

  function getStreamKey(stream) {
    stream = stream || {};
    return [stream.source || '', stream.id || '', stream.url || '', stream.label || ''].join('|');
  }

  function schedulePlaybackStartKick(stream) {
    clearPlaybackStartAttemptTimers();
    var delays = state.playerEngine === 'embed' ? [80, 260, 700, 1400, 2600] : [0, 180, 700, 1500];
    delays.forEach(function (delay) {
      state.playbackStartAttemptTimers.push(setTimeout(function () {
        kickPlaybackStart(stream);
      }, delay));
    });
  }

  function kickPlaybackStart(stream) {
    if (els.playerView.hidden || state.playbackPausedByUser) return;
    if (state.playerEngine === 'avplay') {
      if (!hasAvplaySupport()) return;
      try {
        syncAvplayDisplayRect();
        if (safeGetAvplayState() !== 'PLAYING') webapis.avplay.play();
      } catch (error) {}
      return;
    }
    if (state.playerEngine === 'html5' && !els.videoPlayer.hidden) {
      try {
        els.videoPlayer.autoplay = true;
        els.videoPlayer.playsInline = true;
        var promise = els.videoPlayer.play && els.videoPlayer.play();
        if (promise && promise.catch) {
          promise.catch(function () {
            if (els.playerView.hidden || state.playbackPausedByUser) return;
            try {
              els.videoPlayer.muted = true;
              var retry = els.videoPlayer.play && els.videoPlayer.play();
              if (retry && retry.catch) retry.catch(function () {});
            } catch (retryError) {}
          });
        }
      } catch (error) {}
      return;
    }
    if (state.playerEngine === 'embed' && !els.embedPlayer.hidden) {
      postEmbedPlayCommands(els.embedPlayer);
      trySetAccessibleEmbedPlayback(els.embedPlayer, true);
      tryClickEmbeddedPlay(els.embedPlayer);
      keepPlayerControlsFocused(els.embedPlayer);
    }
  }

  function playDirectHtml5Stream(match, stream, streams) {
    var readyForPlayback = false;
    var playbackStarted = false;
    var mutedFallbackTried = false;
    var directPlaybackAttemptInFlight = false;

    state.playerEngine = 'html5';
    state.avplayReady = false;
    state.avplayBuffering = false;
    state.embedPlaybackPaused = false;
    setAvplaySurfaceVisible(false);
    els.embedPlayer.hidden = true;
    els.videoPlayer.hidden = false;
    els.videoPlayer.controls = !IS_TV_RUNTIME;
    els.videoPlayer.autoplay = true;
    els.videoPlayer.muted = false;
    els.videoPlayer.defaultMuted = false;
    els.videoPlayer.removeAttribute('muted');
    els.videoPlayer.preload = 'auto';
    els.videoPlayer.volume = 1;
    els.videoPlayer.playsInline = true;

    function completePlaybackStart(message) {
      if (playbackStarted) return;
      playbackStarted = true;
      setStatus(message);
    }

    function attemptDirectPlayback(isEarlyAttempt) {
      if (playbackStarted || directPlaybackAttemptInFlight) return;
      var promise;
      try {
        promise = els.videoPlayer.play && els.videoPlayer.play();
      } catch (error) {
        promise = Promise.reject(error);
      }
      if (!promise || !promise.then) {
        if (!isEarlyAttempt || readyForPlayback) completePlaybackStart('Playing ' + formatStreamLabel(stream) + (streams.length > 1 ? ' · auto fallback on' : '') + '.');
        return;
      }
      directPlaybackAttemptInFlight = true;
      promise.then(function () {
        directPlaybackAttemptInFlight = false;
        completePlaybackStart('Playing ' + formatStreamLabel(stream) + (streams.length > 1 ? ' · auto fallback on' : '') + '.');
      }).catch(function () {
        directPlaybackAttemptInFlight = false;
        if (isEarlyAttempt && !readyForPlayback) return;
        if (!mutedFallbackTried) {
          mutedFallbackTried = true;
          els.videoPlayer.muted = true;
          attemptDirectPlayback(false);
          return;
        }
        if (playEmbeddedFallback(match, stream)) return;
        setStatus('Playback blocked. Trying another stream...');
        scheduleStreamFallback('playback blocked', 500);
      });
    }

    loadPlayerVideoSource(els.videoPlayer, stream, function () {
      readyForPlayback = true;
      attemptDirectPlayback(false);
    });
    attemptDirectPlayback(true);
  }

  function playDirectAvplayStream(match, stream, streams) {
    if (!hasAvplaySupport()) {
      playDirectHtml5Stream(match, stream, streams);
      return;
    }

    state.avplaySession += 1;
    var sessionId = state.avplaySession;

    state.playerEngine = 'avplay';
    state.avplayReady = false;
    state.avplayBuffering = false;
    state.embedPlaybackPaused = false;
    els.videoPlayer.hidden = true;
    els.embedPlayer.hidden = true;
    setAvplaySurfaceVisible(true);

    function isCurrentSession() {
      return state.playerEngine === 'avplay' && state.avplaySession === sessionId && !els.playerView.hidden;
    }

    function fallbackToHtml5(reason) {
      if (!isCurrentSession()) return;
      teardownAvplayPlayback();
      state.playerEngine = '';
      setStatus(reason || 'TV native player failed. Trying browser player.');
      playDirectHtml5Stream(match, stream, streams);
    }

    try {
      webapis.avplay.open(stream.url);
      applyAvplayStreamingHints(stream);
      webapis.avplay.setListener({
        onbufferingstart: function () {
          if (!isCurrentSession()) return;
          state.avplayBuffering = true;
          setStatus('Buffering ' + formatStreamLabel(stream) + ' on TV...');
          scheduleStreamFallback('buffering', 9000);
          syncPlayerPlaybackButton();
        },
        onbufferingprogress: function (percent) {
          if (!isCurrentSession()) return;
          if (typeof percent === 'number' && percent >= 0 && percent < 100) {
            setStatus('Buffering ' + formatStreamLabel(stream) + ' on TV... ' + percent + '%');
          }
        },
        onbufferingcomplete: function () {
          if (!isCurrentSession()) return;
          state.avplayBuffering = false;
          state.avplayReady = true;
          handlePlaybackHealthy();
          setStatus('Playing ' + formatStreamLabel(stream) + (streams.length > 1 ? ' · auto fallback on' : '') + '.');
        },
        oncurrentplaytime: function () {
          if (!isCurrentSession()) return;
          state.avplayReady = true;
          clearPlaybackSwitchTimer();
          syncPlayerPlaybackButton();
        },
        onstreamcompleted: function () {
          if (!isCurrentSession()) return;
          setStatus('Stream ended. Trying another source...');
          scheduleStreamFallback('stream completed', 250);
        },
        onerror: function (eventType) {
          fallbackToHtml5('TV native player error: ' + String(eventType || 'unknown') + '. Trying browser player.');
        },
        onerrormsg: function (eventType, errorMsg) {
          var parts = [eventType, errorMsg].filter(Boolean);
          fallbackToHtml5('TV native player error: ' + parts.join(' ') + '. Trying browser player.');
        },
        onevent: function (eventType) {
          if (!isCurrentSession()) return;
          if (String(eventType || '') === 'PLAYER_MSG_HTTP_ERROR_CODE') {
            fallbackToHtml5('TV native player hit a stream network error. Trying browser player.');
          }
        }
      });
      syncAvplayDisplayRect();
      webapis.avplay.prepareAsync(function () {
        if (!isCurrentSession()) return;
        state.avplayReady = true;
        try {
          syncAvplayDisplayRect();
          if (!state.playbackPausedByUser) webapis.avplay.play();
          setStatus('Opening ' + formatStreamLabel(stream) + ' on TV...');
          syncPlayerPlaybackButton();
        } catch (error) {
          fallbackToHtml5('TV native player could not start playback. Trying browser player.');
        }
      }, function () {
        fallbackToHtml5('TV native player could not prepare this stream. Trying browser player.');
      });
    } catch (error) {
      fallbackToHtml5('TV native player could not open this stream. Trying browser player.');
    }
  }

  function playMatch(match, stream, orderedStreams, streamIndex) {
    state.playbackPausedByUser = false;
    var streams = orderedStreams && orderedStreams.length ? orderedStreams.slice() : getOrderedPlayableStreams(getStreamsForMatch(match));
    var index = typeof streamIndex === 'number' ? streamIndex : streams.map(getStreamKey).indexOf(getStreamKey(stream));
    if (index < 0) {
      streams.unshift(stream);
      index = 0;
    }
    clearPlaybackSwitchTimer();
    teardownActivePlayback();
    state.playbackMatchId = match.id;
    state.playbackStreams = streams;
    state.playbackStreamIndex = index;
    els.playerLeague.textContent = match.competition;
    els.playerTitle.textContent = match.title;
    els.playerView.removeAttribute('hidden');
    els.playerView.hidden = false;
    state.focusedKey = '';
    if (stream.type === 'embed') {
      state.playerEngine = 'embed';
      state.avplayReady = false;
      state.avplayBuffering = false;
      state.embedPlaybackPaused = false;
      state.previewMuted = false;
      setAvplaySurfaceVisible(false);
      els.videoPlayer.hidden = true;
      els.embedPlayer.hidden = false;
      configureEmbedFrame(els.embedPlayer);
      els.embedPlayer.onload = function () {
        nudgeEmbeddedPlayback(els.embedPlayer);
        keepPlayerControlsFocused(els.embedPlayer);
      };
      els.embedPlayer.src = withAutoplayParams(stream.url, false);
      nudgeEmbeddedPlayback(els.embedPlayer);
      setStatus('Opened ' + formatStreamLabel(stream) + '.');
    } else {
      if (hasAvplaySupport()) playDirectAvplayStream(match, stream, streams);
      else playDirectHtml5Stream(match, stream, streams);
    }
    schedulePlaybackStartKick(stream);
    updatePlayerControls(stream);
    collectFocusables(true);
    stopPlayerFocusGuard();
  }

  function formatStreamLabel(stream) {
    return stream.label || [stream.source, stream.language, stream.hd ? 'HD' : '', stream.streamNo ? '#' + stream.streamNo : ''].filter(Boolean).join(' ') || 'stream';
  }

  function updatePlayerControls(stream) {
    if (els.playerStreamLabel) {
      els.playerStreamLabel.textContent = formatStreamLabel(stream) + (state.playbackStreams.length > 1 ? ' · ' + (state.playbackStreamIndex + 1) + '/' + state.playbackStreams.length : '');
    }
    var hasAlternate = state.playbackStreams && state.playbackStreams.length > 1;
    if (els.playerPrevButton) els.playerPrevButton.setAttribute('aria-disabled', hasAlternate ? 'false' : 'true');
    if (els.playerNextButton) els.playerNextButton.setAttribute('aria-disabled', hasAlternate ? 'false' : 'true');
    syncPlayerPlaybackButton();
  }

  function syncPlayerPlaybackButton() {
    if (!els.playerPlayButton) return;
    if (state.playerEngine === 'avplay') {
      els.playerPlayButton.textContent = safeGetAvplayState() === 'PLAYING' ? 'Pause' : 'Play';
      return;
    }
    if (state.playerEngine === 'html5' && !els.videoPlayer.hidden) {
      els.playerPlayButton.textContent = els.videoPlayer.paused ? 'Play' : 'Pause';
      return;
    }
    els.playerPlayButton.textContent = state.embedPlaybackPaused ? 'Play' : 'Pause';
  }

  function togglePlayerPlayback(shouldPlay) {
    if (typeof shouldPlay !== 'boolean') shouldPlay = null;
    var currentlyPlaying = state.playerEngine === 'avplay' ? safeGetAvplayState() === 'PLAYING'
      : state.playerEngine === 'html5' ? !els.videoPlayer.paused : !state.embedPlaybackPaused;
    state.playbackPausedByUser = shouldPlay === null ? currentlyPlaying : !shouldPlay;
    if (state.playbackPausedByUser) {
      clearPlaybackStartAttemptTimers();
      clearPlaybackSwitchTimer();
    }
    if (state.playerEngine === 'avplay') {
      try {
        var avplayState = safeGetAvplayState();
        var playAvplay = shouldPlay === null ? avplayState !== 'PLAYING' : shouldPlay;
        if (playAvplay) webapis.avplay.play();
        else webapis.avplay.pause();
      } catch (error) {
        setStatus('TV player rejected that command.');
      }
      syncPlayerPlaybackButton();
      return;
    }
    if (state.playerEngine === 'html5' && !els.videoPlayer.hidden) {
      var playVideo = shouldPlay === null ? els.videoPlayer.paused : shouldPlay;
      if (playVideo) {
        els.videoPlayer.play().catch(function () { scheduleStreamFallback('playback blocked', 500); });
      } else {
        els.videoPlayer.pause();
      }
      syncPlayerPlaybackButton();
      return;
    }
    if (!els.embedPlayer.hidden) {
      state.embedPlaybackPaused = shouldPlay === null ? !state.embedPlaybackPaused : !shouldPlay;
      if (state.embedPlaybackPaused) {
        postEmbedPauseCommands(els.embedPlayer);
        trySetAccessibleEmbedPlayback(els.embedPlayer, false);
        setStatus('Sent pause command to embedded stream.');
      } else {
        postEmbedPlayCommands(els.embedPlayer);
        trySetAccessibleEmbedPlayback(els.embedPlayer, true);
        setStatus('Sent play command to embedded stream.');
      }
      syncPlayerPlaybackButton();
    }
  }

  function postEmbedPauseCommands(frame) {
    try {
      var target = frame.contentWindow;
      if (!target) return;
      target.postMessage({ event: 'command', func: 'pauseVideo', args: [] }, '*');
      target.postMessage({ event: 'command', func: 'pause', args: [] }, '*');
      target.postMessage({ method: 'pause' }, '*');
      target.postMessage({ type: 'pause' }, '*');
      target.postMessage('pause', '*');
    } catch (error) {}
  }

  function trySetAccessibleEmbedPlayback(frame, shouldPlay) {
    try {
      var doc = frame.contentDocument || frame.contentWindow.document;
      collectFromRoot(doc, 'video, audio').forEach(function (media) {
        if (shouldPlay) {
          var promise = media.play && media.play();
          if (promise && promise.catch) promise.catch(function () {});
        } else if (media.pause) {
          media.pause();
        }
      });
      var target = findEmbeddedControlTarget(doc, shouldPlay ? /play|▶|►/i : /pause|❚❚|Ⅱ|\|\|/i, false);
      if (target) fireUserLikeClick(target);
    } catch (error) {}
  }

  function keepPlayerControlsFocused(frame) {
    if (els.playerView.hidden || !isFocusableVisible(els.backButton)) return;
    var active = document.activeElement;
    var isControl = active && active.dataset && active.dataset.focusable === 'true' && els.playerView.contains(active);
    if (!isControl || active === document.body || active === frame || active === els.videoPlayer || active === els.playerView) {
      focusNode(els.backButton);
    }
  }

  function startPlayerFocusGuard() {
    stopPlayerFocusGuard();
    state.playerFocusTimer = setInterval(function () {
      if (els.playerView.hidden) {
        stopPlayerFocusGuard();
        return;
      }
      keepPlayerControlsFocused(els.embedPlayer);
    }, 450);
  }

  function stopPlayerFocusGuard() {
    if (state.playerFocusTimer) clearInterval(state.playerFocusTimer);
    state.playerFocusTimer = null;
  }


  function clearPlaybackSwitchTimer() {
    if (state.playbackSwitchTimer) clearTimeout(state.playbackSwitchTimer);
    state.playbackSwitchTimer = null;
  }

  function playEmbeddedFallback(match, stream) {
    if (!match || !stream || !stream.fallbackUrl || stream.fallbackUrl === stream.url) return false;
    setStatus('Direct stream failed. Falling back to embedded player.');
    playMatch(match, Object.assign({}, stream, {
      url: stream.fallbackUrl,
      type: 'embed'
    }), state.playbackStreams, state.playbackStreamIndex);
    return true;
  }

  function scheduleStreamFallback(reason, delay) {
    if (els.playerView.hidden || !state.playbackStreams || state.playbackStreams.length < 2) return;
    clearPlaybackSwitchTimer();
    state.playbackSwitchTimer = setTimeout(function () {
      switchToNextStream(reason || 'buffering');
    }, typeof delay === 'number' ? delay : 6500);
  }

  function switchToNextStream(reason) {
    switchStreamByOffset(1, reason || 'manual switch');
  }

  function switchToPreviousStream(reason) {
    switchStreamByOffset(-1, reason || 'manual switch');
  }

  function switchStreamByOffset(step, reason) {
    clearPlaybackSwitchTimer();
    var match = findMatchById(state.playbackMatchId);
    var streams = state.playbackStreams || [];
    if (!match || streams.length < 2) {
      setStatus('No alternate stream is available.');
      return;
    }
    var current = streams[state.playbackStreamIndex];
    for (var offset = 1; offset < streams.length; offset += 1) {
      var nextIndex = (state.playbackStreamIndex + offset * step + streams.length) % streams.length;
      var next = streams[nextIndex];
      if (next && getStreamKey(next) !== getStreamKey(current)) {
        setStatus('Switching stream: ' + reason + '.');
        playMatch(match, next, streams, nextIndex);
        return;
      }
    }
  }

  function handlePlaybackHealthy() {
    clearPlaybackSwitchTimer();
    syncPlayerPlaybackButton();
  }

  function handlePlaybackStalled(event) {
    if (els.playerView.hidden) return;
    var reason = event && event.type === 'error' ? 'stream error' : 'buffering';
    setStatus('Stream is ' + reason + '. Watching for recovery...');
    scheduleStreamFallback(reason, event && event.type === 'error' ? 700 : 6500);
  }


  function withAutoplayParams(url, muted) {
    if (!url) return '';
    var hash = '';
    var base = url;
    var hashIndex = base.indexOf('#');
    if (hashIndex >= 0) {
      hash = base.slice(hashIndex);
      base = base.slice(0, hashIndex);
    }
    var separator = base.indexOf('?') >= 0 ? '&' : '?';
    var muteValue = muted ? '1' : '0';
    return base + separator + [
      'autoplay=1',
      'autoPlay=1',
      'autostart=1',
      'playsinline=1',
      'muted=' + muteValue,
      'mute=' + muteValue,
      'volume=' + (muted ? '0' : Math.round(state.previewVolume * 100)),
      'start=1',
      'controls=0',
      'enablejsapi=1',
      'preload=auto',
      'origin=' + encodeURIComponent(window.location.origin)
    ].join('&') + hash;
  }

  function closePlayer() {
    teardownActivePlayback();
    setAvplaySurfaceVisible(false);
    els.videoPlayer.hidden = false;
    els.embedPlayer.hidden = true;
    els.playerView.setAttribute('hidden', '');
    els.playerView.hidden = true;
    clearPlaybackSwitchTimer();
    stopPlayerFocusGuard();
    state.playbackMatchId = '';
    state.playbackStreams = [];
    state.playbackStreamIndex = 0;
    state.playerEngine = '';
    state.avplayReady = false;
    state.avplayBuffering = false;
    if (els.playerStreamLabel) els.playerStreamLabel.textContent = '';
    if (state.streamPickerMatchId) {
      restoreStreamPickerFromPlayer();
      return;
    }
    restoreListingFocus(state.selectedMatchId || state.heroMatchId);
  }

  function collectFocusables(skipFocus) {
    var scope = document;
    if (state.profileOpen && !els.profileView.hidden) scope = els.profileView;
    else if (state.teamHubOpen && !els.teamHubOverlay.hidden) scope = els.teamHubOverlay;
    else if (state.searchOpen && !els.searchOverlay.hidden) scope = els.searchOverlay;
    else if (state.streamPickerOpen && !els.streamPickerOverlay.hidden) scope = els.streamPickerOverlay;
    else if (!els.playerView.hidden) scope = els.playerView;
    state.focusables = Array.prototype.slice.call(scope.querySelectorAll('[data-focusable="true"]:not([disabled])')).filter(isFocusableVisible);
    var activeIndex = state.focusables.indexOf(document.activeElement);
    var keyIndex = -1;
    if (state.focusedKey) {
      for (var i = 0; i < state.focusables.length; i += 1) {
        if (getFocusKey(state.focusables[i]) === state.focusedKey) {
          keyIndex = i;
          break;
        }
      }
    }
    if (activeIndex >= 0) state.focusedIndex = activeIndex;
    else if (keyIndex >= 0) state.focusedIndex = keyIndex;
    else state.focusedIndex = Math.min(state.focusedIndex, Math.max(state.focusables.length - 1, 0));
    if (!skipFocus) focusCurrent();
  }

  function focusNode(node) {
    if (!node) return;
    try {
      node.focus({ preventScroll: true });
    } catch (error) {
      node.focus();
    }
    rememberFocusedNode(node);
  }

  function rememberFocusedNode(node) {
    if (!node) return;
    var index = state.focusables.indexOf(node);
    if (index >= 0) state.focusedIndex = index;
    var key = getFocusKey(node);
    if (key) state.focusedKey = key;
  }

  function getFocusKey(node) {
    if (!node) return '';
    if (node.dataset) {
      if (node.dataset.navMoreToggle) return 'nav-more-toggle';
      if (node.dataset.sportId) return 'sport:' + node.dataset.sportId;
      if (node.dataset.matchId) return (node.className && String(node.className).indexOf('match-card') >= 0 ? 'tile:' : 'hero:') + node.dataset.matchId;
      if (node.dataset.profileId) return 'profile:' + node.dataset.profileId;
      if (node.dataset.leagueName) return 'league:' + node.dataset.leagueName;
      if (node.dataset.teamKey) return 'team:' + node.dataset.teamKey;
    }
    return node.id ? 'id:' + node.id : '';
  }

  function focusCurrent() {
    var node = state.focusables[state.focusedIndex];
    focusNode(node);
    scheduleFocusVisible(node);
  }

  function scheduleFocusVisible(node) {
    state.focusRevealNode = node || null;
    if (state.focusRevealFrame) cancelAnimationFrame(state.focusRevealFrame);
    state.focusRevealFrame = requestAnimationFrame(function () {
      state.focusRevealFrame = null;
      ensureFocusVisible(state.focusRevealNode);
    });
  }

  function ensureFocusVisible(node) {
    if (!node || !els.content || state.profileOpen || state.searchOpen || state.teamHubOpen || !els.playerView.hidden) return;
    var rect = node.getBoundingClientRect();
    var row = node.closest && node.closest('.matches-row');
    if (row) {
      var rowRect = row.getBoundingClientRect();
      var sideSlack = 42;
      if (rect.left < rowRect.left + sideSlack) {
        row.scrollLeft += rect.left - rowRect.left - sideSlack;
      } else if (rect.right > rowRect.right - sideSlack) {
        row.scrollLeft += rect.right - rowRect.right + sideSlack;
      }
    }
    var containerRect = els.content.getBoundingClientRect();
    var topSlack = 112;
    var bottomSlack = 60;
    if (rect.top < containerRect.top + topSlack) {
      els.content.scrollTop += rect.top - containerRect.top - topSlack;
    } else if (rect.bottom > containerRect.bottom - bottomSlack) {
      els.content.scrollTop += rect.bottom - containerRect.bottom + bottomSlack;
    }
  }

  function isFocusableVisible(node) {
    return !!(node.offsetWidth || node.offsetHeight || node.getClientRects().length);
  }

  function focusMatchById(matchId) {
    var target = getMatchTileById(matchId) || document.querySelector('[data-match-id="' + cssEscape(matchId) + '"]');
    if (!target) return;
    var index = state.focusables.indexOf(target);
    if (index >= 0) state.focusedIndex = index;
    focusNode(target);
  }

  function getMatchTileById(matchId) {
    if (!matchId || !els.matchesGrid) return null;
    return els.matchesGrid.querySelector('.match-card[data-match-id="' + cssEscape(matchId) + '"]');
  }

  function focusActiveSportSelector() {
    collectFocusables(true);
    var activeTabs = Array.prototype.slice.call(els.sportsList.querySelectorAll('[data-sport-id="' + cssEscape(state.activeSport) + '"]')).filter(isFocusableVisible);
    var target = activeTabs[0] || els.sportsList.querySelector('.more-button.active') || els.sportsList.querySelector('[data-focusable="true"]');
    if (!target) return;
    var index = state.focusables.indexOf(target);
    if (index >= 0) state.focusedIndex = index;
    focusNode(target);
    setStatus('Choose a sport.');
  }

  function moveFocus(direction) {
    var active = document.activeElement;
    var index = state.focusables.indexOf(active);
    if (index >= 0) state.focusedIndex = index;

    var override = getDirectionalFocusOverride(active, direction);
    if (override) {
      focusNode(override);
      scheduleFocusVisible(override);
      return;
    }

    var localTarget = getLocalDirectionalFocus(active, direction);
    if (localTarget) {
      focusNode(localTarget);
      scheduleFocusVisible(localTarget);
      return;
    }

    var nextIndex = getSpatialFocusIndex(active, direction);
    if (nextIndex == null) {
      if (direction === 'left' || direction === 'up') nextIndex = state.focusedIndex - 1;
      if (direction === 'right' || direction === 'down') nextIndex = state.focusedIndex + 1;
    }

    state.focusedIndex = Math.max(0, Math.min(state.focusables.length - 1, nextIndex));
    focusCurrent();
  }

  function getLocalDirectionalFocus(active, direction) {
    if (!active || !active.closest) return null;
    if ((direction === 'left' || direction === 'right') && active.closest('.matches-row')) {
      return getSiblingFocusableInScope(active.closest('.matches-row'), active, direction === 'left' ? -1 : 1);
    }
    if ((direction === 'left' || direction === 'right') && active.closest('.sports-list')) {
      return getSiblingFocusableInScope(els.sportsList, active, direction === 'left' ? -1 : 1);
    }
    if ((direction === 'up' || direction === 'down') && active.closest('.nav-more-menu')) {
      return getSiblingFocusableInScope(active.closest('.nav-more-menu'), active, direction === 'up' ? -1 : 1);
    }
    if ((direction === 'up' || direction === 'down') && active.closest('.stream-option-list')) {
      return getSiblingFocusableInScope(active.closest('.stream-option-list'), active, direction === 'up' ? -1 : 1);
    }
    if ((direction === 'left' || direction === 'right') && active.closest('.hero-team-actions')) {
      return getSiblingFocusableInScope(active.closest('.hero-team-actions'), active, direction === 'left' ? -1 : 1);
    }
    if ((direction === 'left' || direction === 'right') && active.closest('.keyboard-row')) {
      return getSiblingFocusableInScope(active.closest('.keyboard-row'), active, direction === 'left' ? -1 : 1);
    }
    return null;
  }

  function getSiblingFocusableInScope(scope, active, step) {
    if (!scope || !active) return null;
    var nodes = Array.prototype.slice.call(scope.querySelectorAll('[data-focusable="true"]:not([disabled])')).filter(isFocusableVisible);
    var index = nodes.indexOf(active);
    if (index < 0) return null;
    var target = nodes[index + step];
    return target || null;
  }

  function getDirectionalFocusOverride(active, direction) {
    if (!active || !active.closest || !els.playerView.hidden || state.profileOpen || state.searchOpen || state.teamHubOpen) return null;
    if (direction === 'down' && active.dataset && active.dataset.navMoreToggle && state.navMoreOpen) return els.sportsList.querySelector('.nav-more-menu [data-sport-id]');
    if (direction === 'up' && active.closest('.nav-more-menu')) return els.sportsList.querySelector('.more-button');
    if (direction === 'up' && active.closest('.match-card')) return getHeroFocusTarget();
    if (direction === 'up' && active.closest('.hero-feature')) return getActiveSportFocusTarget();
    if (direction === 'down' && active.dataset && active.dataset.sportId && !active.closest('.nav-more-menu')) return getHeroFocusTarget();
    if (direction === 'down' && active.closest('.hero-feature')) return getMatchTileById(state.selectedMatchId || state.heroMatchId);
    return null;
  }

  function getHeroFocusTarget() {
    return els.heroFeature.querySelector('.hero-play-button') || els.heroFeature.querySelector('[data-team-key]') || els.heroFeature.querySelector('.hero-button');
  }

  function getActiveSportFocusTarget() {
    var activeTabs = Array.prototype.slice.call(els.sportsList.querySelectorAll('[data-sport-id="' + cssEscape(state.activeSport) + '"]')).filter(isFocusableVisible);
    return activeTabs[0] || els.sportsList.querySelector('.more-button.active') || els.sportsList.querySelector('[data-focusable="true"]');
  }

  function getSpatialFocusIndex(active, direction) {
    if (!active || !active.getBoundingClientRect) return null;
    var activeRect = active.getBoundingClientRect();
    var ax = activeRect.left + activeRect.width / 2;
    var ay = activeRect.top + activeRect.height / 2;
    var activeRow = active.closest ? active.closest('.matches-row') : null;
    var bestNode = null;
    var bestScore = Infinity;
    state.focusables.forEach(function (node) {
      if (node === active || !isFocusableVisible(node)) return;
      var rect = node.getBoundingClientRect();
      var cx = rect.left + rect.width / 2;
      var cy = rect.top + rect.height / 2;
      var dx = cx - ax;
      var dy = cy - ay;
      var primary = direction === 'left' ? -dx : direction === 'right' ? dx : direction === 'up' ? -dy : dy;
      if (primary <= 8) return;
      var secondary = direction === 'left' || direction === 'right' ? Math.abs(dy) : Math.abs(dx);
      var score = primary + secondary * 1.7;
      if ((direction === 'left' || direction === 'right') && activeRow && node.closest && node.closest('.matches-row') === activeRow) {
        score -= 180;
      }
      if (score < bestScore) {
        bestScore = score;
        bestNode = node;
      }
    });
    if (!bestNode) return null;
    var nextFocusIndex = state.focusables.indexOf(bestNode);
    return nextFocusIndex >= 0 ? nextFocusIndex : null;
  }

  function handleKeydown(event) {
    var key = window.StreamedCompat.getKey(event);
    state.userInteracted = true;
    if (state.profileOpen && !els.profileView.hidden) {
      event.preventDefault();
      if (key === 'Escape' && IS_WEBOS_RUNTIME) returnToWebos();
      if (key === 'ArrowLeft' || key === 37) moveFocus('left');
      if (key === 'ArrowRight' || key === 39) moveFocus('right');
      if (key === 'ArrowUp' || key === 38) moveFocus('up');
      if (key === 'ArrowDown' || key === 40) moveFocus('down');
      if (key === 'Enter' || key === 13) document.activeElement.click();
      return;
    }
    var isTeamHubTyping = state.teamHubOpen && !els.teamHubOverlay.hidden && document.activeElement && document.activeElement.matches && document.activeElement.matches('.team-hub-search input');
    if (isTeamHubTyping) {
      if (key === 'Escape' || key === 10009) {
        event.preventDefault();
        closeTeamHub();
      }
      return;
    }
    if (state.teamHubOpen && !els.teamHubOverlay.hidden) {
      if (key === 'Backspace' || key === 'Escape' || key === 10009) {
        event.preventDefault();
        closeTeamHub();
        return;
      }
      event.preventDefault();
      if (key === 'ArrowLeft' || key === 37) moveFocus('left');
      if (key === 'ArrowRight' || key === 39) moveFocus('right');
      if (key === 'ArrowUp' || key === 38) moveFocus('up');
      if (key === 'ArrowDown' || key === 40) moveFocus('down');
      if (key === 'Enter' || key === 13) document.activeElement.click();
      return;
    }
    var isSearchTyping = !els.searchOverlay.hidden && document.activeElement === els.searchInput;
    if (isSearchTyping) {
      if (key === 'Escape' || key === 10009) {
        event.preventDefault();
        closeSearch();
      }
      return;
    }

    if (state.streamPickerOpen && !els.streamPickerOverlay.hidden) {
      if (key === 'Backspace' || key === 'Escape' || key === 10009) {
        event.preventDefault();
        closeStreamPicker();
        return;
      }
      event.preventDefault();
      if (key === 'ArrowLeft' || key === 37) moveFocus('left');
      if (key === 'ArrowRight' || key === 39) moveFocus('right');
      if (key === 'ArrowUp' || key === 38) moveFocus('up');
      if (key === 'ArrowDown' || key === 40) moveFocus('down');
      if (key === 'Enter' || key === 13) document.activeElement.click();
      return;
    }

    if (!els.playerView.hidden) {
      event.preventDefault();
      if (key === 'Backspace' || key === 'Escape' || key === 10009) closePlayer();
      else if (key === 'ArrowLeft' || key === 37) switchToPreviousStream('manual switch');
      else if (key === 'ArrowRight' || key === 39) switchToNextStream('manual switch');
      else if (key === 'Enter' || key === 13) togglePlayerPlayback();
      else if (key === 'MediaPlay' || key === 415) togglePlayerPlayback(true);
      else if (key === 'MediaPause' || key === 19) togglePlayerPlayback(false);
      else if (key === 'MediaPlayPause' || key === 10252) togglePlayerPlayback();
      else if (key === 'MediaStop' || key === 413) closePlayer();
      else if (key === 'MediaTrackNext' || key === 417) switchToNextStream('manual switch');
      else if (key === 'MediaTrackPrevious' || key === 412) switchToPreviousStream('manual switch');
      return;
    }

    if (!els.searchOverlay.hidden) {
      if (key === 'Backspace' || key === 'Escape' || key === 10009) {
        event.preventDefault();
        closeSearch();
        return;
      }
      event.preventDefault();
      if (key === 'ArrowLeft' || key === 37) moveFocus('left');
      if (key === 'ArrowRight' || key === 39) moveFocus('right');
      if (key === 'ArrowUp' || key === 38) moveFocus('up');
      if (key === 'ArrowDown' || key === 40) moveFocus('down');
      if (key === 'Enter' || key === 13) {
        rememberSearch(state.searchQuery);
        document.activeElement.click();
      }
      return;
    }

    if (key === 'Backspace' || key === 'Escape' || key === 10009) {
      event.preventDefault();
      if (state.navMoreOpen) {
        toggleNavMore(false, false);
        return;
      }
      if (IS_WEBOS_RUNTIME) { returnToWebos(); return; }
      focusActiveSportSelector();
      return;
    }

    if (key === '/' || key === 191 || key === 403) {
      event.preventDefault();
      openSearch();
      return;
    }

    if (key === 'f' || key === 'F' || key === 405) {
      event.preventDefault();
      toggleFocusedFavourite();
      return;
    }

    if (key === 'l' || key === 'L' || key === 406) {
      event.preventDefault();
      toggleFocusedLeagueFavourite();
      return;
    }

    if (key === 'ArrowLeft' || key === 37) {
      event.preventDefault();
      moveFocus('left');
    }
    if (key === 'ArrowRight' || key === 39) {
      event.preventDefault();
      moveFocus('right');
    }
    if (key === 'ArrowUp' || key === 38) {
      event.preventDefault();
      moveFocus('up');
    }
    if (key === 'ArrowDown' || key === 40) {
      event.preventDefault();
      moveFocus('down');
    }
    if (key === 'Enter' || key === 13) {
      event.preventDefault();
      document.activeElement.click();
    }
  }

  function handleHardwareBack(event) {
    state.userInteracted = true;
    if (event && event.preventDefault) event.preventDefault();

    if (state.profileOpen && !els.profileView.hidden) {
      if (IS_WEBOS_RUNTIME) returnToWebos();
      return;
    }

    if (!els.playerView.hidden) {
      closePlayer();
      return;
    }

    if (state.streamPickerOpen && !els.streamPickerOverlay.hidden) {
      closeStreamPicker();
      return;
    }

    if (state.teamHubOpen && !els.teamHubOverlay.hidden) {
      closeTeamHub();
      return;
    }

    if (state.searchOpen && !els.searchOverlay.hidden) {
      closeSearch();
      return;
    }

    if (IS_WEBOS_RUNTIME) returnToWebos();
    else focusActiveSportSelector();
  }

  function returnToWebos() {
    stopHeroPreview();
    if (window.webOS && typeof window.webOS.platformBack === 'function') window.webOS.platformBack();
    else if (window.PalmSystem && typeof window.PalmSystem.platformBack === 'function') window.PalmSystem.platformBack();
  }

  function handleTizenHardwareKey(event) {
    var keyName = String(event && event.keyName || '').toLowerCase();
    if (keyName === 'back' || keyName === 'return' || keyName === 'exit') {
      handleHardwareBack(event);
    }
  }

  function registerTizenKeys() {
    if (!window.tizen || !tizen.tvinputdevice || !tizen.tvinputdevice.registerKey) return;
    ['MediaPlayPause', 'MediaPlay', 'MediaPause', 'MediaStop', 'MediaTrackPrevious', 'MediaTrackNext'].forEach(function (name) {
      try {
        tizen.tvinputdevice.registerKey(name);
      } catch (error) {}
    });
  }

  function updateClock() {
    els.clock.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function formatStart(value) {
    return new Date(value).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  function cssEscape(value) {
    if (window.CSS && window.CSS.escape) return window.CSS.escape(value);
    return String(value).replace(/[^a-zA-Z0-9_-]/g, '\\$&');
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }

  window.open = function () {
    setStatus('Blocked popup from stream provider.');
    return null;
  };

  els.backButton.dataset.focusable = 'true';
  if (els.playerPlayButton) els.playerPlayButton.addEventListener('click', togglePlayerPlayback);
  if (els.playerPrevButton) els.playerPrevButton.addEventListener('click', function () { switchToPreviousStream('manual switch'); });
  if (els.playerNextButton) els.playerNextButton.addEventListener('click', function () { switchToNextStream('manual switch'); });
  ['playing', 'play', 'pause', 'volumechange', 'canplay', 'canplaythrough', 'timeupdate', 'progress'].forEach(function (type) {
    els.videoPlayer.addEventListener(type, handlePlaybackHealthy);
  });
  ['waiting', 'stalled', 'error'].forEach(function (type) {
    els.videoPlayer.addEventListener(type, handlePlaybackStalled);
  });
  els.videoPlayer.setAttribute('tabindex', '-1');
  els.embedPlayer.setAttribute('tabindex', '-1');
  els.backButton.addEventListener('click', closePlayer);
  els.profileManageButton.addEventListener('click', function () { toggleProfileManageMode(true); });
  els.profileDoneButton.addEventListener('click', function () { toggleProfileManageMode(false); });
  els.profileButton.addEventListener('click', openProfiles);
  els.teamHubCloseButton.addEventListener('click', closeTeamHub);
  els.streamPickerCloseButton.addEventListener('click', closeStreamPicker);
  els.searchButton.addEventListener('click', openSearch);
  els.searchCloseButton.addEventListener('click', closeSearch);
  els.searchInput.addEventListener('input', function () {
    state.searchQuery = els.searchInput.value;
    renderSearchResults();
    collectFocusables(true);
    focusNode(els.searchInput);
  });
  if (els.content) {
    els.content.addEventListener('scroll', function () {
      if (els.content.scrollTop > 32) state.userScrolledContent = true;
    }, { passive: true });
  }
  document.addEventListener('keydown', handleKeydown);
  window.addEventListener('tizenhwkey', handleTizenHardwareKey);
  window.addEventListener('popstate', handleHardwareBack);
  document.addEventListener('visibilitychange', function () {
    if (!IS_WEBOS_RUNTIME || !document.hidden) return;
    if (!els.playerView.hidden) closePlayer();
    stopHeroPreview();
  });
  document.addEventListener('webOSRelaunch', function () {
    if (IS_WEBOS_RUNTIME) collectFocusables(true);
  });
  window.addEventListener('resize', syncAvplayDisplayRect);
  registerTizenKeys();
  updateClock();
  setInterval(updateClock, 30000);
  renderProfiles();
  collectFocusables();
})();
