(function () {
  'use strict';

  var config = window.STREAMED_CONFIG || {};
  var API_BASE_URL = config.INTERNAL_API_BASE_URL || 'https://streamed.pk';
  var TEAM_STANDINGS_API_BASE_URL = config.TEAM_STANDINGS_API_BASE_URL || '';
  var TEAM_HUB_API_BASE_URL = config.TEAM_HUB_API_BASE_URL || TEAM_STANDINGS_API_BASE_URL;
  var HAS_INTERNAL_API_BASE = !!config.INTERNAL_API_BASE_URL;
  var HAS_TEAM_STANDINGS_PROVIDER = !!TEAM_STANDINGS_API_BASE_URL;

  var API_ENDPOINTS = {
    sports: '/api/sports',
    matchesBySport: function (sport) { return '/api/matches/' + encodeURIComponent(sport); },
    popularMatchesBySport: function (sport) { return '/api/matches/' + encodeURIComponent(sport) + '/popular'; },
    allMatches: '/api/matches/all',
    allPopularMatches: '/api/matches/all/popular',
    todaysMatches: '/api/matches/all-today',
    todaysPopularMatches: '/api/matches/all-today/popular',
    liveMatches: '/api/matches/live',
    livePopularMatches: '/api/matches/live/popular',
    teamStanding: '/api/ai/team-standings',
    teamHubTeams: '/api/team-hub/teams',
    teamBadge: function (id) { return '/api/images/badge/' + cleanImageId(id) + '.webp'; },
    matchPoster: function (homeBadge, awayBadge) { return '/api/images/poster/' + cleanImageId(homeBadge) + '/' + cleanImageId(awayBadge) + '.webp'; },
    proxiedImage: function (poster) { return '/api/images/proxy/' + cleanProxyImageId(poster) + '.webp'; },
    streamsBySource: function (source, id) { return '/api/stream/' + encodeURIComponent(source) + '/' + encodeURIComponent(id); }
  };

  /**
   * @typedef {Object} APIMatch
   * @property {string} id Unique identifier for the match.
   * @property {string} title Match title, for example "Team A vs Team B".
   * @property {string} category Sport category, for example "football" or "basketball".
   * @property {number} date Unix timestamp in milliseconds.
   * @property {string=} poster URL path to match poster image.
   * @property {boolean} popular Whether the match is marked as popular.
   * @property {{home?: {name: string, badge: string}, away?: {name: string, badge: string}}=} teams Team metadata.
   * @property {{source: string, id: string}[]} sources Stream source identifiers.
   */

  function cleanImageId(value) {
    return String(value || '')
      .replace(/^https?:\/\/[^/]+/i, '')
      .replace(/^\/+/, '')
      .replace(/^api\/images\/(badge|poster)\//i, '')
      .replace(/\.webp$/i, '')
      .split('/')
      .map(encodeURIComponent)
      .join('/');
  }

  function cleanProxyImageId(value) {
    return encodeURIComponent(String(value || '').replace(/^\/+/, '').replace(/^api\/images\/proxy\//i, '').replace(/\.webp$/i, ''));
  }

  function unwrapArray(payload) {
    if (Array.isArray(payload)) return payload;
    if (Array.isArray(payload && payload.data)) return payload.data;
    if (Array.isArray(payload && payload.matches)) return payload.matches;
    if (Array.isArray(payload && payload.streams)) return payload.streams;
    if (Array.isArray(payload && payload.results)) return payload.results;
    return [];
  }

  function buildUrl(path) {
    if (/^https?:\/\//i.test(String(path || ''))) return path;
    if (!API_BASE_URL) return path;
    return API_BASE_URL.replace(/\/$/, '') + path;
  }

  function requestJson(path, options) {
    return fetch(buildUrl(path), options || {}).then(function (response) {
      if (!response.ok) {
        throw new Error('API request failed: ' + response.status + ' ' + response.statusText);
      }
      return response.json();
    });
  }

  function requestBaseJson(baseUrl, path, label) {
    var base = String(baseUrl || '').replace(/\/$/, '');
    return fetch(base + path).then(function (response) {
      if (!response.ok) {
        throw new Error(label + ' request failed: ' + response.status + ' ' + response.statusText);
      }
      return response.json();
    });
  }

  function requestTeamStandingJson(path) {
    return requestBaseJson(TEAM_STANDINGS_API_BASE_URL, path, 'Team standing');
  }

  function requestTeamHubJson(path) {
    return requestBaseJson(TEAM_HUB_API_BASE_URL, path, 'Team Hub');
  }

  /**
   * @typedef {Object} Stream
   * @property {string} id Unique identifier for the stream.
   * @property {number} streamNo Stream number/index.
   * @property {string} language Stream language, for example "English".
   * @property {boolean} hd Whether the stream is HD quality.
   * @property {string} embedUrl URL that can be used to embed the stream.
   * @property {string} source Source identifier, for example "alpha" or "bravo".
   */

  /**
   * @typedef {Object} Sport
   * @property {string} id Sport identifier used in Matches API endpoints.
   * @property {string} name Display name of the sport.
   */

  /** @returns {Promise<Sport[]>} */
  function getSports() {
    return requestJson(API_ENDPOINTS.sports).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getMatchesBySport(sport) {
    return requestJson(API_ENDPOINTS.matchesBySport(sport)).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getPopularMatchesBySport(sport) {
    return requestJson(API_ENDPOINTS.popularMatchesBySport(sport)).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getAllMatches() {
    return requestJson(API_ENDPOINTS.allMatches).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getAllPopularMatches() {
    return requestJson(API_ENDPOINTS.allPopularMatches).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getTodaysMatches() {
    return requestJson(API_ENDPOINTS.todaysMatches).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getTodaysPopularMatches() {
    return requestJson(API_ENDPOINTS.todaysPopularMatches).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getLiveMatches() {
    return requestJson(API_ENDPOINTS.liveMatches).then(unwrapArray);
  }

  /** @returns {Promise<APIMatch[]>} */
  function getLivePopularMatches() {
    return requestJson(API_ENDPOINTS.livePopularMatches).then(unwrapArray);
  }

  /** @returns {Promise<Stream[]>} */
  function getStreamsBySource(source, id) {
    return requestJson(API_ENDPOINTS.streamsBySource(source, id)).then(unwrapArray);
  }

  function getTeamHubTeams() {
    if (!TEAM_HUB_API_BASE_URL) return Promise.reject(new Error('Team Hub backend is not configured.'));
    return requestTeamHubJson(API_ENDPOINTS.teamHubTeams).then(function (payload) { return unwrapArray(payload.teams ? payload.teams : payload); });
  }

  function getTeamStanding(team) {
    if (!HAS_TEAM_STANDINGS_PROVIDER) return Promise.reject(new Error('Team standings provider is not configured.'));
    var query = [
      'team=' + encodeURIComponent(team.name || ''),
      'sport=' + encodeURIComponent(team.sport || ''),
      'teamKey=' + encodeURIComponent(team.key || ''),
      'competition=' + encodeURIComponent(team.competition || ''),
      'season=current'
    ].join('&');
    return requestTeamStandingJson(API_ENDPOINTS.teamStanding + '?' + query);
  }

  function getAlphaStreams(id) { return getStreamsBySource('alpha', id); }
  function getBravoStreams(id) { return getStreamsBySource('bravo', id); }
  function getCharlieStreams(id) { return getStreamsBySource('charlie', id); }
  function getDeltaStreams(id) { return getStreamsBySource('delta', id); }
  function getEchoStreams(id) { return getStreamsBySource('echo', id); }
  function getFoxtrotStreams(id) { return getStreamsBySource('foxtrot', id); }
  function getGolfStreams(id) { return getStreamsBySource('golf', id); }
  function getHotelStreams(id) { return getStreamsBySource('hotel', id); }
  function getIntelStreams(id) { return getStreamsBySource('intel', id); }

  function getTeamBadgeUrl(id) {
    return buildUrl(API_ENDPOINTS.teamBadge(id));
  }

  function getMatchPosterUrl(homeBadge, awayBadge) {
    return buildUrl(API_ENDPOINTS.matchPoster(homeBadge, awayBadge));
  }

  function getProxiedImageUrl(poster) {
    return buildUrl(API_ENDPOINTS.proxiedImage(poster));
  }

  window.StreamedAPI = {
    API_BASE_URL: API_BASE_URL,
    API_ENDPOINTS: API_ENDPOINTS,
    HAS_INTERNAL_API_BASE: HAS_INTERNAL_API_BASE,
    HAS_TEAM_STANDINGS_PROVIDER: HAS_TEAM_STANDINGS_PROVIDER,
    HAS_TEAM_HUB_PROVIDER: !!TEAM_HUB_API_BASE_URL,
    buildUrl: buildUrl,
    unwrapArray: unwrapArray,
    getTeamBadgeUrl: getTeamBadgeUrl,
    getMatchPosterUrl: getMatchPosterUrl,
    getProxiedImageUrl: getProxiedImageUrl,
    requestJson: requestJson,
    getSports: getSports,
    getMatchesBySport: getMatchesBySport,
    getPopularMatchesBySport: getPopularMatchesBySport,
    getAllMatches: getAllMatches,
    getAllPopularMatches: getAllPopularMatches,
    getTodaysMatches: getTodaysMatches,
    getTodaysPopularMatches: getTodaysPopularMatches,
    getLiveMatches: getLiveMatches,
    getLivePopularMatches: getLivePopularMatches,
    getStreamsBySource: getStreamsBySource,
    getTeamHubTeams: getTeamHubTeams,
    getTeamStanding: getTeamStanding,
    getAlphaStreams: getAlphaStreams,
    getBravoStreams: getBravoStreams,
    getCharlieStreams: getCharlieStreams,
    getDeltaStreams: getDeltaStreams,
    getEchoStreams: getEchoStreams,
    getFoxtrotStreams: getFoxtrotStreams,
    getGolfStreams: getGolfStreams,
    getHotelStreams: getHotelStreams,
    getIntelStreams: getIntelStreams
  };
})();
