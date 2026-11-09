# Deliverable 3 — Functional Validation and Load Testing

**Repo:** https://github.com/aledanee/final-exam-ITMSdevOps

## 1. What was tested

| | |
|---|---|
| System | Task Manager API on Minikube (K8s v1.37.0), 1→6 API pods + MySQL |
| Tools | **k6** (load), `tests/functional-test.sh` (functional, curl) |
| Load profile | **500 users × 3 minutes**, 85 % list / 10 % get / 5 % create, 100 ms think time |
| Scripts | `load/load-test.js`, `tests/functional-test.sh` (both committed) |

## 2. Functional test — 14/14 passed

```bash
./tests/functional-test.sh http://192.168.49.2:30080
```

| Check | Result |
|---|---|
| Health → 200, body says `Healthy` | ✅ |
| Create → 201 with `id` | ✅ |
| List → 200, contains new task | ✅ |
| Get one → 200 / missing → 404 | ✅ |
| Update → 200, change saved | ✅ |
| Empty title → 400 | ✅ |
| Delete → 204, then get → 404 | ✅ |

**The API is correct when there is no load.**

## 3. Load test results (k6, 500 users × 3 min)

| Metric | Value |
|---|---|
| Total requests | 21 696 |
| Throughput | 117.1 req/s |
| Latency (avg / median) | 3.80 s / 2.73 s |
| Latency (p90 / p95 / max) | 7.11 s / **11.57 s** / 41.32 s |
| Error rate | **22.38 %** (peak 42 %), target was < 1 % ❌ |

## 4. Latency and errors over time

| Window | Req/s | p50 | p90 | p95 | Error % |
|---|---|---|---|---|---|
| 0–30 s | 86 | 5.00 s | 6.69 s | 7.11 s | 0.08 % |
| 30–60 s | 39 | 6.18 s | 11.70 s | 13.39 s | 3.01 % |
| 60–90 s | 35 | 11.82 s | 20.82 s | 21.54 s | 0.48 % |
| 90–120 s | 190 | 2.09 s | 3.97 s | 8.25 s | **42.08 %** |
| 120–150 s | 179 | 2.51 s | 4.25 s | 4.88 s | 27.44 % |
| 150–180 s | 178 | 2.32 s | 4.42 s | 5.20 s | 16.09 % |

What happened, in three steps:

1. **First 90 s:** almost no errors, but latency explodes (p95 21 s) — requests queue waiting for a database connection.
2. **At 90 s:** pods start crashing and restarting — latency drops, but errors spike to **42 %** (HTTP 500).
3. **After 120 s:** slow partial recovery (27 % → 16 %) as connections free up.

Failures: **4 446 × HTTP 500**, 384 timeouts, 27 expected 404s.

## 5. Root cause — MySQL out of connections

```
ERROR 1040 (08004): Too many connections
max_connections      151
Max_used_connections 152   ← limit hit
```

* 6 API pods × connection pool of up to 100 each ≫ 151 allowed by MySQL.
* Requests that need a new connection fail instantly → HTTP 500.
* Pods that can't reach the DB at boot give up and restart → **RESTARTS climbed 2 → 9 during the test**.
* After the load stopped everything recovered by itself: all pods `1/1`, API → 200, all 3 077 rows intact.

## 6. CPU-bound or memory-bound?

| | Peak usage | Limit |
|---|---|---|
| Node CPU | 36 % | 5 cores |
| Node memory | 24 % | 7 404 Mi |
| API pod CPU | up to 98 % of limit | 500 m |
| API pod memory | 28 % of limit | 512 Mi |
| MySQL memory | 93 % of limit (no OOM) | 512 Mi |

**Answer: neither — it was *connection-bound*.**

* Not memory-bound: memory stayed low everywhere, **zero OOM kills**.
* Not CPU-bound at cluster level: node CPU peaked at 36 %.
* If forced to choose one: **CPU** was tighter (pods touched 98 % of their limit) while memory had ≥ 72 % headroom.
* What actually failed: the MySQL connection limit — a config ceiling, hit instantly.

## 7. Recommendations

1. Raise MySQL `max_connections` (e.g. 500) **and** cap each pod's pool (e.g. 25 → 6×25 = 150).
2. Add `EnableRetryOnFailure()` and a `startupProbe` so booting pods aren't killed.
3. Re-run this same script afterwards to confirm errors drop below 1 %.
4. Keep `tests/functional-test.sh` as the quick correctness check after every change.

## 8. Commands to reproduce

```bash
# 1. functional test
./tests/functional-test.sh http://192.168.49.2:30080

# 2. load test (500 users, 3 minutes)
docker run --rm --network host -v "$PWD/load:/scripts" grafana/k6 run /scripts/load-test.js

# 3. watch the cluster in a second terminal
watch -n 2 'kubectl top pods -n task-manager; kubectl -n task-manager get pods; kubectl -n task-manager get hpa'
```
