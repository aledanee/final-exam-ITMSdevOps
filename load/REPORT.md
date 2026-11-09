# Deliverable 3 — Results

**Repo:** https://github.com/aledanee/final-exam-ITMSdevOps
**Test:** k6, 500 users × 3 minutes, against the API on Minikube · scripts: `load/load-test.js`, `tests/functional-test.sh`

## Functional validation: 14 / 14 passed ✅

Health 200 · create 201 · list 200 · get 200/404 · update 200 · empty title 400 · delete 204 → 404

## Load test result

| Metric | Value |
|---|---|
| Total requests | 21 696 |
| Throughput | 117.1 req/s |
| Latency avg / median | 3.80 s / 2.73 s |
| Latency p90 / p95 / max | 7.11 s / **11.57 s** / 41.32 s |
| Error rate | **22.38 %** (target < 1 % ❌) |

## Latency and errors over time

| Window | Req/s | p50 | p90 | p95 | Error % |
|---|---|---|---|---|---|
| 0–30 s | 86 | 5.00 s | 6.69 s | 7.11 s | 0.08 % |
| 30–60 s | 39 | 6.18 s | 11.70 s | 13.39 s | 3.01 % |
| 60–90 s | 35 | 11.82 s | 20.82 s | 21.54 s | 0.48 % |
| 90–120 s | 190 | 2.09 s | 3.97 s | 8.25 s | **42.08 %** |
| 120–150 s | 179 | 2.51 s | 4.25 s | 4.88 s | 27.44 % |
| 150–180 s | 178 | 2.32 s | 4.42 s | 5.20 s | 16.09 % |

Failures: 4 446 × HTTP 500 · 384 timeouts · 27 expected 404s.

## Why it failed

```
ERROR 1040 (08004): Too many connections
max_connections 151  |  Max_used_connections 152
```
MySQL ran out of connections (6 pods × pools of 100 ≫ 151) → HTTP 500s, pods crash-looping
(**RESTARTS 2 → 9 during the test**). Everything recovered by itself afterwards: all pods
`1/1`, API 200, all 3 077 rows intact.

## CPU-bound or memory-bound?

| | Peak | Limit |
|---|---|---|
| Node CPU | 36 % | 5 cores |
| Node memory | 24 % | 7 404 Mi |
| API pod CPU | up to 98 % | 500 m |
| API pod memory | 28 % | 512 Mi |
| MySQL memory | 93 % (no OOM) | 512 Mi |

**Neither — it was connection-bound.** No OOM kills and node CPU at 36 %, so memory and
cluster CPU were not the problem. If forced to choose one: **CPU** was the tighter resource
(pods hit 98 % of their limit) while memory kept ≥ 72 % headroom. The failure was the MySQL
connection limit — a config ceiling hit instantly.

## Recommendation

Raise MySQL `max_connections` (e.g. 500) and cap each pod's pool (e.g. 25 → 6×25 = 150);
add `EnableRetryOnFailure()` + a `startupProbe`; re-run the same script to confirm errors
drop below 1 %.
