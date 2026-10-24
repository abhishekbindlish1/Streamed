#!/usr/bin/env node
'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const { URL } = require('url');

const ROOT = __dirname;
function loadLocalEnv() {
  const envPath = path.join(ROOT, '.env.local');
  if (!fs.existsSync(envPath)) return;
  fs.readFileSync(envPath, 'utf8').split(/\r?\n/).forEach((line) => {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) return;
    const index = trimmed.indexOf('=');
    if (index < 0) return;
    const key = trimmed.slice(0, index).trim();
    const value = trimmed.slice(index + 1).trim().replace(/^['"]|['"]$/g, '');
    if (key && !process.env[key]) process.env[key] = value;
  });
}

loadLocalEnv();
const DATA_DIR = path.join(ROOT, 'data');
const TEAM_DB_PATH = path.join(DATA_DIR, 'team-db.js');
const TEAM_BACKFILL_PATH = path.join(DATA_DIR, 'team-backfill.json');
const STANDINGS_CACHE_PATH = path.join(DATA_DIR, 'team-standings-cache.json');
const PORT = Number(process.env.PORT || 3000);
const OPENAI_MODEL = process.env.OPENAI_MODEL || 'gpt-4.1-mini';
const CACHE_MS = 60 * 60 * 1000;
const standingsCache = new Map();

const MIME_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.xml': 'application/xml; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.webp': 'image/webp',
  '.png': 'image/png'
};

const TEAM_WIKI_TITLES = {
  'royal-challengers-bengaluru': 'Royal_Challengers_Bengaluru',
  'lucknow-super-giants': 'Lucknow_Super_Giants',
  'mumbai-indians': 'Mumbai_Indians',
  'chennai-super-kings': 'Chennai_Super_Kings',
  'kolkata-knight-riders': 'Kolkata_Knight_Riders',
  'sunrisers-hyderabad': 'Sunrisers_Hyderabad',
  'delhi-capitals': 'Delhi_Capitals',
  'punjab-kings': 'Punjab_Kings',
  'rajasthan-royals': 'Rajasthan_Royals',
  'gujarat-titans': 'Gujarat_Titans',
  'manchester-united': 'Manchester_United_F.C.',
  'manchester-city': 'Manchester_City_F.C.',
  'liverpool': 'Liverpool_F.C.',
  'arsenal': 'Arsenal_F.C.',
  'chelsea': 'Chelsea_F.C.',
  'tottenham-hotspur': 'Tottenham_Hotspur_F.C.',
  'newcastle-united': 'Newcastle_United_F.C.',
  'aston-villa': 'Aston_Villa_F.C.',
  'real-madrid': 'Real_Madrid_CF',
  'barcelona': 'FC_Barcelona',
  'atletico-madrid': 'Atlético_Madrid',
  'bayern-munich': 'FC_Bayern_Munich',
  'borussia-dortmund': 'Borussia_Dortmund',
  'paris-saint-germain': 'Paris_Saint-Germain_F.C.',
  'juventus': 'Juventus_FC',
  'inter-milan': 'Inter_Milan',
  'ac-milan': 'AC_Milan',
  'napoli': 'SSC_Napoli'
};

function sendJson(res, status, payload) {
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Access-Control-Allow-Origin': '*',
    'Cache-Control': 'no-store'
  });
  res.end(JSON.stringify(payload));
}

function readJson(filePath, fallback) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (error) {
    return fallback;
  }
}

function writeJson(filePath, payload) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(payload, null, 2) + '\n');
}

function normalizeTeamKey(value) {
  return String(value || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
}

function loadTeamDb() {
  const source = fs.readFileSync(TEAM_DB_PATH, 'utf8');
  const sandbox = { window: {} };
  vm.runInNewContext(source, sandbox, { filename: TEAM_DB_PATH });
  return Array.isArray(sandbox.window.STREAMED_TEAM_DB) ? sandbox.window.STREAMED_TEAM_DB : [];
}

function loadTeamBackfill() {
  const payload = readJson(TEAM_BACKFILL_PATH, { teams: [], updatedAt: '' });
  return Array.isArray(payload.teams) ? payload : { teams: [], updatedAt: '' };
}

function loadStandingsCache() {
  const payload = readJson(STANDINGS_CACHE_PATH, { standings: {}, updatedAt: '' });
  return payload && payload.standings ? payload : { standings: {}, updatedAt: '' };
}

function saveStanding(teamKey, payload) {
  const cache = loadStandingsCache();
  cache.standings[teamKey] = Object.assign({}, payload, { cachedAt: new Date().toISOString() });
  cache.updatedAt = new Date().toISOString();
  writeJson(STANDINGS_CACHE_PATH, cache);
}

function getCachedStanding(teamKey) {
  const persistent = loadStandingsCache().standings[teamKey];
  if (persistent) return persistent;
  const memory = standingsCache.get(teamKey);
  if (memory && Date.now() - memory.createdAt < CACHE_MS) return memory.payload;
  return null;
}

function mergeTeams() {
  const teams = loadTeamDb();
  const backfill = loadTeamBackfill();
  const byKey = new Map();

  teams.forEach((team) => {
    byKey.set(team.key, Object.assign({}, team));
  });

  backfill.teams.forEach((team) => {
    const key = team.key || normalizeTeamKey(team.name);
    byKey.set(key, Object.assign({}, byKey.get(key) || {}, team, { key }));
  });

  const standings = loadStandingsCache().standings;
  Object.keys(standings).forEach((key) => {
    if (byKey.has(key)) byKey.set(key, Object.assign({}, byKey.get(key), { standing: standings[key] }));
  });

  return Array.from(byKey.values());
}

function sendStatic(req, res) {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const safePath = path.normalize(decodeURIComponent(url.pathname)).replace(/^(\.\.[/\\])+/, '');
  const filePath = path.join(ROOT, safePath === '/' ? 'index.html' : safePath);

  if (!filePath.startsWith(ROOT)) {
    res.writeHead(403);
    res.end('Forbidden');
    return;
  }

  fs.readFile(filePath, (error, data) => {
    if (error) {
      res.writeHead(404);
      res.end('Not found');
      return;
    }
    res.writeHead(200, {
      'Content-Type': MIME_TYPES[path.extname(filePath)] || 'application/octet-stream',
      'Cache-Control': 'no-store'
    });
    res.end(data);
  });
}

function getOutputText(payload) {
  if (payload.output_text) return payload.output_text;
  const messages = Array.isArray(payload.output) ? payload.output : [];
  return messages.flatMap((item) => item.content || [])
    .map((content) => content.text || '')
    .filter(Boolean)
    .join('\n');
}

function parseJsonObject(text) {
  try {
    return JSON.parse(text);
  } catch (error) {
    const match = String(text || '').match(/\{[\s\S]*\}/);
    if (!match) throw error;
    return JSON.parse(match[0]);
  }
}

function buildStandingPrompt(params) {
  const team = params.get('team') || 'Unknown team';
  const sport = params.get('sport') || 'sport';
  const competition = params.get('competition') || 'current competition';
  return [
    `Find the latest current-season league/table standing for ${team}.`,
    `Sport/category: ${sport}. Competition hint: ${competition}.`,
    'Use web search. Prefer official league tables, ESPN/Cricbuzz for IPL/cricket, or major sports sites.',
    'Return only compact JSON with these fields:',
    '{ "summary": string, "position": string, "league": string, "points": string, "record": string, "form": string, "played": string, "sourceTitle": string, "sourceUrl": string, "updatedAt": string }',
    'For football, record can be W-D-L. For IPL/cricket, record can be P-W-L and include NRR in form if available.',
    'If the exact current league standing is unclear, set summary to a brief unavailable explanation and include the best source you found.'
  ].join('\n');
}

async function fetchOpenAIStanding(params) {
  if (!process.env.OPENAI_API_KEY) {
    const error = new Error('OPENAI_API_KEY is not set.');
    error.status = 503;
    throw error;
  }

  const response = await fetch('https://api.openai.com/v1/responses', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${process.env.OPENAI_API_KEY}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      model: OPENAI_MODEL,
      tools: [{ type: 'web_search' }],
      tool_choice: 'auto',
      include: ['web_search_call.action.sources'],
      max_output_tokens: 700,
      instructions: 'You are a sports standings lookup service. Answer with JSON only. Keep the summary TV-friendly and under 90 characters. Include current position, points, played/record, and recent form/NRR when available.',
      input: buildStandingPrompt(params)
    })
  });

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(payload.error && payload.error.message ? payload.error.message : 'OpenAI request failed.');
    error.status = response.status;
    throw error;
  }

  const parsed = parseJsonObject(getOutputText(payload));
  return {
    summary: parsed.summary || 'Standing unavailable.',
    position: parsed.position || '',
    league: parsed.league || '',
    points: parsed.points || '',
    record: parsed.record || parsed.played || '',
    form: parsed.form || '',
    sourceTitle: parsed.sourceTitle || '',
    sourceUrl: parsed.sourceUrl || '',
    updatedAt: parsed.updatedAt || new Date().toISOString()
  };
}

async function handleTeamStanding(req, res) {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const teamKey = normalizeTeamKey(url.searchParams.get('teamKey') || url.searchParams.get('team') || '');
  const cached = teamKey ? getCachedStanding(teamKey) : null;

  if (cached) {
    sendJson(res, 200, cached);
    return;
  }

  try {
    const payload = await fetchOpenAIStanding(url.searchParams);
    standingsCache.set(teamKey || url.searchParams.toString(), { createdAt: Date.now(), payload });
    if (teamKey) saveStanding(teamKey, payload);
    sendJson(res, 200, payload);
  } catch (error) {
    sendJson(res, error.status || 500, {
      summary: error.message || 'Standing unavailable.',
      position: '',
      league: '',
      points: '',
      record: '',
      form: '',
      sourceTitle: '',
      sourceUrl: '',
      updatedAt: new Date().toISOString()
    });
  }
}

async function fetchWikiLogo(team) {
  const wikiTitle = team.wikiTitle || TEAM_WIKI_TITLES[team.key] || '';
  if (!wikiTitle) return {};

  const response = await fetch(`https://en.wikipedia.org/api/rest_v1/page/summary/${encodeURIComponent(wikiTitle)}`, {
    headers: { 'User-Agent': 'streamed-tv-local-backfill/1.0' }
  });
  if (!response.ok) return {};
  const payload = await response.json();
  return {
    wikiTitle,
    logoUrl: payload.thumbnail && payload.thumbnail.source ? payload.thumbnail.source : '',
    logoSourceUrl: payload.content_urls && payload.content_urls.desktop ? payload.content_urls.desktop.page : ''
  };
}

async function handleTeamBackfill(req, res) {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const includeStandings = url.searchParams.get('standings') === '1';
  const baseTeams = loadTeamDb().filter((team) => TEAM_WIKI_TITLES[team.key]);
  const existing = loadTeamBackfill();
  const existingByKey = new Map(existing.teams.map((team) => [team.key, team]));
  const teams = [];

  for (const team of baseTeams) {
    const cached = existingByKey.get(team.key) || {};
    const logo = cached.logoUrl ? cached : await fetchWikiLogo(team).catch(() => ({}));
    const enriched = Object.assign({}, team, cached, logo, { wikiTitle: logo.wikiTitle || cached.wikiTitle || TEAM_WIKI_TITLES[team.key] });

    if (includeStandings && process.env.OPENAI_API_KEY) {
      const params = new URLSearchParams({
        team: team.name,
        teamKey: team.key,
        sport: team.sport || '',
        competition: team.competition || '',
        season: 'current'
      });
      enriched.standing = await fetchOpenAIStanding(params).catch(() => cached.standing || null);
      if (enriched.standing) saveStanding(team.key, enriched.standing);
    }

    teams.push(enriched);
  }

  const payload = { updatedAt: new Date().toISOString(), teams };
  writeJson(TEAM_BACKFILL_PATH, payload);
  sendJson(res, 200, payload);
}

const server = http.createServer((req, res) => {
  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type'
    });
    res.end();
    return;
  }

  if (req.method === 'GET' && req.url.startsWith('/api/ai/team-standings')) {
    handleTeamStanding(req, res);
    return;
  }

  if (req.method === 'GET' && req.url.startsWith('/api/team-hub/teams')) {
    sendJson(res, 200, { teams: mergeTeams(), updatedAt: new Date().toISOString() });
    return;
  }

  if ((req.method === 'GET' || req.method === 'POST') && req.url.startsWith('/api/team-hub/backfill')) {
    handleTeamBackfill(req, res);
    return;
  }

  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.writeHead(405);
    res.end('Method not allowed');
    return;
  }

  sendStatic(req, res);
});

server.listen(PORT, () => {
  console.log(`Streamed app: http://localhost:${PORT}`);
  console.log('Team Hub backend: /api/team-hub/teams');
  console.log('One-time logo backfill: open /api/team-hub/backfill');
  console.log('Logo + standings backfill: OPENAI_API_KEY=... node server.js, then open /api/team-hub/backfill?standings=1');
});
