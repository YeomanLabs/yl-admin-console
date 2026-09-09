# PERF — measured numbers

Targets come from `docs/01-VISION.md`. Every number here was measured, not estimated. Re-measure after any change to
the cache schema, the search ranking, or startup.

- **Machine:** Windows 11 Pro 10.0.26200, Debug build, no profiler attached.
- **Date:** 2026-09-09
- **Tenant:** YeomanLabs Consulting dev tenant — 11 users, 4 Entra devices, 2 Intune devices, 13 groups, 4 SKUs.

The dev tenant is far below the scale doc 01 names, so the palette was measured twice: once against the real cache,
and once against a synthetic one built at exactly the documented size. Only the second number means anything.

## Results

| What | Target | Measured | Verdict |
|---|---|---|---|
| Cold start to interactive palette (warm cache) | < 3 s | median 1.26 s, max 1.40 s | met |
| Window visible | — | median 571 ms | — |
| Palette query → results, 5,000 users / 2,000 devices / 1,000 groups | < 100 ms | median 1.1 ms, p95 3.3 ms, max 3.8 ms | met, with room to spare |
| Palette query → results, real dev tenant | < 100 ms | median 0.2 ms, p95 0.3 ms | met |
| User 360 from cache | < 500 ms | 0.2–2.9 ms | met |
| User 360 live sections | < 2 s | 307–511 ms | met |
| Create user from template | < 30 s | 3.4 s, 3.5 s, 3.8 s | met (M7 gate) |
| Offboard with default steps | < 60 s | not measured — see below | open |

## Palette at the documented scale

Synthetic cache: 5,000 users, 2,000 devices, 1,000 groups, 4.1 MB on disk, seeded in 0.4 s. Twenty samples per query
after one warm-up, taken through `SearchService` exactly as the palette calls it.

| Query | Median | p95 | Max |
|---|---|---|---|
| `a` (worst case, one letter) | 3.4 ms | 3.7 ms | 3.8 ms |
| `sm` | 1.1 ms | 1.5 ms | 1.6 ms |
| `smith` | 1.2 ms | 1.3 ms | 1.3 ms |
| `user 1234` | 1.0 ms | 1.1 ms | 1.2 ms |
| `LT-0500` (device name) | 0.3 ms | 0.4 ms | 0.4 ms |
| `SN-01234` (serial) | 0.4 ms | 0.4 ms | 0.5 ms |
| `team 42` | 0.5 ms | 0.7 ms | 1.0 ms |
| `u:smith` | 0.5 ms | 0.6 ms | 0.9 ms |
| `d:LT-` | 1.2 ms | 1.3 ms | 1.4 ms |
| `g:team` | 0.9 ms | 1.0 ms | 1.1 ms |

The single-letter query is the slowest, which is what an FTS5 index over three tables should do: it matches nearly
everything and the client-side re-rank has the most to sort. At 3.8 ms worst case there is nothing here to fix.

## User 360

| User | Cache read | Live `$batch` |
|---|---|---|
| Andrew Yeoman | 2.9 ms | 504 ms |
| Bre (test account) | 0.4 ms | 365 ms |
| Break Glass | 0.2 ms | 307 ms |
| Callie Yeoman | 0.3 ms | 511 ms |
| John Wentz | 0.2 ms | 493 ms |

The first cache read is the slowest because SQLite is still opening the file. The live number is one `$batch` of seven
inner requests, so it is a round trip to Graph and back, and it is what the 2 s target is about.

## Not measured

- **Offboarding wall clock.** The only account authorised for live writes is the admin's own, and a full default run
  would sign this session out and destroy its own MFA registrations. The M6 gate timed the steps that were safe to run;
  the whole-run number needs a disposable test user.
- **UI-thread stalls during sync** (doc 01: none over 100 ms). Judged by eye during the M2 and M3 gates — the window
  stays responsive through a full initial walk — but not instrumented. A dispatcher-latency counter is the honest way
  to hold this, and it is worth adding before the app is used on a large tenant.

## How to re-measure

The measurement code lives in the scratchpad harness, outside the repo, so it does not ship:

- `perfbig` — synthetic 5,000/2,000/1,000 cache, palette timings, then deletes the file.
- `perf` — the real cache: palette timings and User 360 cache-versus-live.
- `Measure-ColdStart.ps1` — five app launches, timing the window and the first usable palette.
