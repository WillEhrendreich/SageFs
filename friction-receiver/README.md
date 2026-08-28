# SageFs Friction Receiver

A small Cloudflare Worker that receives sanitized SageFs friction reports
and stores them in R2, with an optional Discord notification.

## Cost

Free tier covers everything you'd realistically use:

- **Workers**: 100,000 requests/day free
- **R2**: 10 GB storage, 10M Class A ops/month, 10M Class B ops/month free
- **No Durable Objects** — keeps it free, the Worker is stateless

A typical user submitting a report = 1 POST + 1 R2 PUT = well within free
tier even with thousands of users.

## Setup

### 1. Create the R2 bucket

```sh
wrangler r2 bucket create sagefs-friction
wrangler r2 bucket create sagefs-friction-preview  # for `wrangler dev`
```

### 2. (Optional) Set secrets

```sh
# If set, submissions must include this token in the X-SageFs-Token header.
# Prevents random internet noise from filling your bucket.
wrangler secret put INGEST_TOKEN
# paste a long random string, e.g. `openssl rand -hex 32`

# If set, a Discord notification is posted on each submission.
# Right-click your Discord channel → Integrations → Webhooks → New Webhook → Copy URL
wrangler secret put DISCORD_WEBHOOK_URL
# paste the webhook URL
```

### 3. Deploy

```sh
npm install
npm run deploy
```

Note the Worker URL — it will be `https://sagefs-friction-receiver.<your-subdomain>.workers.dev`
or whatever custom domain you configure.

### 4. (Optional) Custom domain

In the Cloudflare dashboard, add a route for the Worker on a subdomain
of sagetech.dev (e.g. `friction.sagetech.dev`). Workers → your worker →
Triggers → Custom Domains.

### 5. Configure SageFs

In the SageFs dashboard, open the Friction panel (right-side drawer).
Enter your endpoint URL and (if set) the ingest token. The endpoint
URL and token are stored locally — never sent anywhere automatically.

## What the Worker accepts

```
POST / HTTP/1.1
Content-Type: application/json
X-SageFs-Token: <optional, required if INGEST_TOKEN is set>

{
  "schemaVersion": 1,
  "sageFsVersion": "0.6.315",
  "submittedAtUtc": "2026-01-01T00:00:00.000Z",
  "totalEvents": 16,
  "totalFeedbackItems": 4,
  "toolsWithFriction": [...],
  "topBlockers": [...],
  "frequentTransitions": [...],
  "recentFeedback": [...],
  "recommendedWorkItems": [...]
}
```

The schema is `1`. The server rejects anything else with a 400.

## What the Worker does

1. **Token check** — if `INGEST_TOKEN` is set, requests without a matching
   `X-SageFs-Token` header are rejected with 401.
2. **Size check** — requests over `MAX_PAYLOAD_BYTES` (default 64KB) are
   rejected with 413.
3. **Schema validation** — the body must be a JSON object with the
   expected fields and types. Otherwise 400.
4. **Defense-in-depth sanitization** — the server re-applies the same
   path/IP/email/session-id redaction as the client. Even if a buggy
   daemon sends a raw path, what's stored in R2 is safe.
5. **Store in R2** — one object per submission, keyed by
   `YYYY/MM/DD/<timestamp>-<random>.json`. The R2 prefix is a real date
   so you can list by month in the Cloudflare dashboard or with
   `wrangler r2 object list --prefix 2026/01/`.
6. **Discord notification** — if `DISCORD_WEBHOOK_URL` is set, a short
   summary is posted to Discord. The full report is in R2; the Discord
   message is just a heads-up.

The Worker returns `{ "reportId": "...", "key": "..." }` on success.

## Browsing submissions

In the Cloudflare dashboard, R2 → sagefs-fucket → object list.
Files are organized by date prefix (`2026/01/15/...`).

Or from the CLI:
```sh
wrangler r2 object list sagefs-friction --prefix 2026/01/
wrangler r2 object get sagefs-friction 2026/01/15/lwxyz-abc12345.json
```

The full JSON is what SageFs sent after server-side sanitization. If
anything sensitive ever lands here, it's a bug — report it.

## What SageFs sends

The SageFs dashboard's Friction panel lets you:

1. Generate a friction report from local telemetry
2. See both the **raw** report (with your free-text `reason` fields) and
   the **sanitized** preview side-by-side
3. Edit the free-text fields before sending
4. Click "Send" to POST to this Worker
5. See the response (success / error) and the reportId for reference

Nothing is sent automatically. The button is opt-in per report. The
endpoint URL is empty by default.

## What the SageFs daemon never sends

- File paths (Windows or Unix)
- IP addresses (private or public)
- Email addresses
- Session IDs (8+ char hex blobs)
- Raw eval code
- Anything beyond the structured schema

Free-text `reason` fields ARE sent, but the user reviews and edits them
in the dashboard before clicking Send. The server re-sanitizes them
as defense-in-depth.

## Development

```sh
npm install
npm run dev          # local dev server on http://localhost:8787
npm run test         # sanitization unit tests
npm run typecheck    # TypeScript type check
```
