/**
 * BetterTrumpet vote collector — Cloudflare Worker.
 *
 * Deployment:
 *   1. npx wrangler login
 *   2. wrangler kv namespace create VOTES  → paste id in wrangler.toml
 *   3. npx wrangler deploy
 *
 * Then put the deployed URL in announcements.json:
 *   "voteEndpoint": "https://votes.bettertrumpet.com/vote",
 *   "resultsUrl":   "https://votes.bettertrumpet.com/results"
 *
 * Endpoints
 *   POST /vote    — body { app, version, announcementId, voterId, answers, votedAt }
 *                   answers = { questionId: optionKey } for polls/surveys/A-B
 *                   answers = { text: "..." } for free-text items (feature requests…)
 *   GET  /results — live counts {updatedAt, results} + free-text answers {texts}
 *   GET  /feed    — the what's-new feed (stored via PUT /feed)
 *   PUT  /feed    — guarded by x-feed-key
 *   GET  /health  — ok
 *
 * KV budget notes (free tier: 1000 list/day, 100k read/day, 1k write/day… but
 * writes here are low-traffic):
 *   - NO list() anywhere. Announcement ids are tracked in a `manifest` key
 *     (array of ids), so /results does 1 manifest read + 1 read per known id —
 *     all cheap reads instead of quota-killing lists.
 *   - /results responses are cached in KV for 5 minutes (cache:results), so the
 *     per-install startup checks collapse into ~288 reads/day max.
 *   - /vote checks the dedupe key FIRST; a duplicate costs one read and zero
 *     writes.
 */

const RATE_LIMIT_WINDOW = 10 * 60;   // seconds
const RATE_LIMIT_MAX = 60;           // votes per IP per window
const TEXT_CAP = 300;                // keep the latest N free-text answers
const RESULTS_CACHE_TTL = 5 * 60;    // seconds — /results snapshot lifetime
const MANIFEST_KEY = "manifest";
const CACHE_KEY = "cache:results";

async function prepare(env) {
  if (typeof env.VOTES !== "object" || !env.VOTES.get) {
    throw new Error("Missing KV binding 'VOTES'. Add it to wrangler.toml.");
  }
}

function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  });
}

/** Register announcement ids touched by a vote so /results knows what to read. */
async function ensureInManifest(env, announcementId) {
  const manifest = (await env.VOTES.get(MANIFEST_KEY, "json")) || [];
  if (!manifest.includes(announcementId)) {
    manifest.push(announcementId);
    await env.VOTES.put(MANIFEST_KEY, JSON.stringify(manifest));
  }
}

/** Normalize a question map to { questionId: { optionKey: count } }. */
function tallyByQuestion(answers, existing) {
  const out = existing ? { ...existing } : {};
  for (const [questionId, optionKey] of Object.entries(answers || {})) {
    const opt = String(optionKey ?? "");
    if (!opt) continue;
    out[questionId] = { ...(out[questionId] || {}), [opt]: (out[questionId]?.[opt] || 0) + 1 };
  }
  return out;
}

/** Read-modify-write for option counts. */
async function mergeCounts(env, announcementId, answers) {
  const key = `counts:${announcementId}`;
  const existing = await env.VOTES.get(key, "json");
  const next = tallyByQuestion(answers, existing);
  await env.VOTES.put(key, JSON.stringify(next));
}

/** Append one free-text answer, keeping the latest TEXT_CAP entries. */
async function appendText(env, announcementId, entry) {
  const key = `texts:${announcementId}`;
  let list = (await env.VOTES.get(key, "json")) || [];
  list.push(entry);
  if (list.length > TEXT_CAP) list = list.slice(list.length - TEXT_CAP);
  await env.VOTES.put(key, JSON.stringify(list));
}

async function handleGetFeed(env) {
  await prepare(env);
  const feed = await env.VOTES.get("feed:json", "json");
  if (!feed) return json({ error: "feed not set yet — PUT /feed with x-feed-key" }, 404);
  return new Response(JSON.stringify(feed), {
    status: 200,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  });
}

async function handlePutFeed(request, env) {
  const key = request.headers.get("x-feed-key") || "";
  if (!env.FEED_KEY || key !== env.FEED_KEY) return json({ error: "forbidden" }, 403);
  let body;
  try { body = await request.json(); } catch { return json({ error: "invalid json" }, 400); }
  if (!body || !Array.isArray(body.announcements)) return json({ error: "announcements array required" }, 400);
  await env.VOTES.put("feed:json", JSON.stringify(body));
  // The feed defines the canonical id set — keep the manifest in sync and
  // invalidate the results cache so new ids show up immediately.
  const ids = body.announcements.map((a) => a.id).filter(Boolean);
  const manifest = (await env.VOTES.get(MANIFEST_KEY, "json")) || [];
  for (const id of ids) {
    if (!manifest.includes(id)) manifest.push(id);
  }
  await env.VOTES.put(MANIFEST_KEY, JSON.stringify(manifest));
  await env.VOTES.delete(CACHE_KEY);
  return json({ ok: true });
}

async function handleVote(request, env) {
  await prepare(env);
  let body;
  try {
    body = await request.json();
  } catch {
    return json({ error: "invalid json" }, 400);
  }

  const { announcementId, voterId, answers } = body || {};
  if (!announcementId || !voterId || !answers || typeof answers !== "object") {
    return json({ error: "announcementId, voterId and answers are required" }, 400);
  }

  // One vote per (announcementId, voterId) — first one wins. Checked before
  // the rate limiter so honest duplicate clients never consume quota.
  const dedupeKey = `vote:${announcementId}:${voterId}`;
  const existingVote = await env.VOTES.get(dedupeKey);
  if (existingVote) {
    return json({ ok: true, duplicate: true });
  }

  // Rate limit per IP (works behind Cloudflare: cf-connecting-ip).
  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  const windowKey = Math.floor(Date.now() / 1000 / RATE_LIMIT_WINDOW);
  const rateKey = `rate:${windowKey}:${ip}`;
  const current = Number((await env.VOTES.get(rateKey)) || 0);
  if (current >= RATE_LIMIT_MAX) {
    return json({ error: "rate limited" }, 429);
  }
  await env.VOTES.put(rateKey, String(current + 1), { expirationTtl: RATE_LIMIT_WINDOW });

  await ensureInManifest(env, announcementId);

  const isText = Object.keys(answers).some((k) => k === "text" && typeof answers[k] === "string");
  if (isText) {
    await appendText(env, announcementId, {
      voter: voterId.slice(0, 8),
      text: String(answers.text).slice(0, 1000),
      at: body.votedAt || new Date().toISOString(),
    });
  } else {
    await mergeCounts(env, announcementId, answers);
  }

  await env.VOTES.put(dedupeKey, JSON.stringify({ votedAt: body.votedAt || null, version: body.version || null }));

  await env.VOTES.put("meta:updatedAt", new Date().toISOString());
  await env.VOTES.delete(CACHE_KEY); // invalidate the snapshot so /results is fresh
  return json({ ok: true });
}

/** Build the results snapshot with zero list() calls. */
async function buildResults(env) {
  const cached = await env.VOTES.get(CACHE_KEY, "json");
  if (cached) return cached;

  const manifest = (await env.VOTES.get(MANIFEST_KEY, "json")) || [];
  const results = {};
  const texts = {};
  let updatedAt = await env.VOTES.get("meta:updatedAt");

  for (const id of manifest) {
    const [counts, textsForId] = await Promise.all([
      env.VOTES.get(`counts:${id}`, "json"),
      env.VOTES.get(`texts:${id}`, "json"),
    ]);
    if (counts) results[id] = counts;
    if (textsForId) texts[id] = textsForId;
  }

  const payload = { updatedAt: updatedAt || null, results, texts };
  await env.VOTES.put(CACHE_KEY, JSON.stringify(payload), { expirationTtl: RESULTS_CACHE_TTL });
  return payload;
}

async function handleResults(env) {
  await prepare(env);
  const payload = await buildResults(env);
  return json(payload);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    try {
      if (request.method === "POST" && url.pathname === "/vote") return await handleVote(request, env);
      if (request.method === "GET" && url.pathname === "/feed") return await handleGetFeed(env);
      if (request.method === "PUT" && url.pathname === "/feed") return await handlePutFeed(request, env);
      if (request.method === "GET" && url.pathname === "/results") return await handleResults(env);
      if (request.method === "GET" && url.pathname === "/health") return json({ ok: true });
      return json({ error: "not found" }, 404);
    } catch (err) {
      return json({ error: String(err?.message || err) }, 500);
    }
  },
};