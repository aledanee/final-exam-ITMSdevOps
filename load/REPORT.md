# Deliverable 3 — Functional Validation and Load Testing (Report)

**Repository:** https://github.com/aledanee/final-exam-ITMSdevOps

## 1. Scope, tools and system under test

| Item | Value |
|---|---|
| System under test (SUT) | Task Manager API deployed on **Minikube** (Kubernetes v1.37.0), Service `NodePort 30080` |
| Stack | API `aledanee/task-manager-api:1.0` (Kestrel, 1→6 pods via HPA) + MySQL 8.0 (1 pod, PVC) |
| Load tool | **k6 (Grafana)** — one of the recommended tools |
| Functional tool | Bash + `curl` assertions (`tests/functional-test.sh`) |
| Load profile | **500 virtual users × 60 s**, 85 % `GET /api/tasks`, 10 % `GET /api/tasks/{id}`, 5 % `POST /api/tasks`, 100 ms think time |
| Scripts (committed) | `load/load-test.js` (k6), `tests/functional-test.sh` (functional assertions) |

Reproduce:

```bash
# functional validation (works against Kubernetes or Docker Compose)
./tests/functional-test.sh http://192.168.49.2:30080
./tests/functional-test.sh http://localhost:8080

# load test with raw samples for the time-window table
docker run --rm --network host -v "$PWD/load:/scripts" -v /tmp:/out \
  grafana/k6 run --out json=/out/k6-samples.json /scripts/load-test.js
```

## 2. Functional validation — 14/14 passed

```
=== Functional validation: http://192.168.49.2:30080 ===
PASS  GET /health returns 200                              -> 200
PASS  health body contains "status":"Healthy"
PASS  POST /api/tasks valid body -> 201                    -> 201
PASS  created task response contains id
PASS  GET /api/tasks -> 200 array                          -> 200
PASS  list contains the created task
PASS  GET /api/tasks/1114 -> 200                           -> 200
PASS  GET /api/tasks/999999 -> 404                         -> 404
PASS  PUT /api/tasks/1114 -> 200                           -> 200
PASS  update persisted isCompleted=true
PASS  POST /api/tasks missing title -> 400                 -> 400
PASS  DELETE /api/tasks/1114 -> 204                        -> 204
PASS  GET deleted task -> 404                              -> 404
=== Result: 14 passed, 0 failed ===   (exit code 0)
```

| Behaviour checked | Expected | Result |
|---|---|---|
| Health endpoint + body | 200, `{"status":"Healthy"}` | ✅ |
| Create returns created entity with `id` | 201 | ✅ |
| List contains the created task | 200 | ✅ |
| Read single task / missing task | 200 / 404 | ✅ |
| Update persists (`isCompleted=true`) | 200 | ✅ |
| Validation rejects empty title | 400 | ✅ |
| Delete then read | 204 then 404 | ✅ |

The API behaves exactly as specified **at zero load**.

## 3. Load test results (k6, 500 VU × 60 s)

```
http_req_duration..............: avg=1.02s  min=68.52ms  med=939.49ms  max=7.3s   p(90)=1.53s  p(95)=2.02s
http_req_failed................: 48.84%  13100 out of 26818
http_reqs......................: 26818  439.85/s
threshold http_req_failed rate<0.01 → ✗ (crossed — see root cause in §5)
```

| Metric | Value |
|---|---|
| Total requests | **26 818** in 60 s |
| Throughput | **439.8 req/s** |
| Latency avg / median | 1.02 s / 939 ms |
| Latency p90 / p95 / max | 1.53 s / **2.02 s** / 7.30 s |
| Error rate | **48.84 %** (13 100 × HTTP 500) |

## 4. Latency percentiles and error rate over time (per 10 s window)

| Window (s) | Requests | Req/s | Avg | p50 | p90 | p95 | Max | Errors | Error % |
|---|---|---|---|---|---|---|---|---|---|
| 0–10 | 3 634 | 363 | 1 153 ms | 1 059 ms | 1 815 ms | 2 108 ms | 5 866 ms | 2 025 | 55.72 % |
| 10–20 | 4 473 | 447 | 1 015 ms | 920 ms | 1 537 ms | 1 992 ms | 6 259 ms | 2 142 | 47.89 % |
| 20–30 | 4 510 | 451 | 1 008 ms | 934 ms | 1 515 ms | 2 026 ms | 5 696 ms | 2 112 | 46.83 % |
| 30–40 | 4 414 | 441 | 1 030 ms | 942 ms | 1 667 ms | 2 175 ms | 5 764 ms | 2 065 | 46.78 % |
| 40–50 | 4 318 | 432 | 1 050 ms | 925 ms | 1 638 ms | 2 264 ms | 7 300 ms | 2 148 | 49.75 % |
| 50–60 | 4 886 | 489 | 947 ms | 898 ms | 1 229 ms | 1 522 ms | 5 897 ms | 2 310 | 47.28 % |
| 60–70 (drain) | 583 | 58 | 930 ms | 885 ms | 1 234 ms | 1 307 ms | 2 178 ms | 298 | 51.11 % |

Error distribution by operation:

| Operation | 200/201 | 404 (expected) | 500 (failures) |
|---|---|---|---|
| `list` | 11 690 | — | 11 116 |
| `get` | 1 343 | 20 | 1 274 |
| `create` | 685 | — | 690 |

**Reading of the table:** latency was *stable* across the whole test (p95 ≈ 1.5–2.3 s, no
ramp-up) while the error rate sat flat at ~47 % from the first window. A steadily high error
rate with flat latency is the signature of a **hard capacity limit being hit immediately**
(some requests fail fast, the accepted ones queue normally) — not of a gradually saturating
resource. That is exactly what §5 proves.

## 5. Root cause of the errors — MySQL connection exhaustion

Every failure was `HTTP 500` on all three operations, and immediately after the test MySQL
reported it was **at/over its connection ceiling**:

```
$ kubectl exec deploy/mysql -- mysql -utaskuser -ptaskpass taskdb -e "SELECT 1;"
ERROR 1040 (08004): Too many connections

max_connections        151
Threads_connected      152
Max_used_connections   152
```

Chain of events:

1. 6 API pods × EF/MySqlConnector connection pool (default up to 100 per pod) ⇒ far more
   connection demand than MySQL's `max_connections = 151`.
2. Any request needing a **new** pooled connection gets `Too many connections` → unhandled by
   EF → the app's error middleware returns **500** (≈ 13 000 requests).
3. Newly scheduled pods could not reach the database **at boot**, burned through the startup
   retries and exited, so kubelet restarted them — the container restart counter climbed:
   `RESTARTS 0 → 1 → 2 → 3` on three pods (events: `Liveness probe failed … will be restarted`,
   `Readiness probe failed … connection refused`):

```
warn: Program[0] Database not ready (attempt 8/12): An exception has been raised that is
      likely due to a transient failure. … Retrying in 5s...
fail: Microsoft.EntityFrameworkCore.Database.Connection[20004]
      An error occurred using the connection to database '' on server 'mysql'.
```

**Recovery** (load stopped, pools recycled — no manual intervention):

```
Threads_connected 152 → 55
GET /api/tasks -> 200
api pods: 6 × 1/1 Running (RESTARTS 3/3/2 — counters keep their history)
```

## 6. CPU-bound or memory-bound?

Samples taken every 5 s during the test (`kubectl top`), against a node with **5 CPU / ~7.5 Gi**
and container limits from `k8s/*.yaml` (API 500m / 512Mi, MySQL 1 000m / 512Mi):

| Layer | CPU observed | CPU ceiling | Memory observed | Memory ceiling |
|---|---|---|---|---|
| Node (peak) | 1 258 m = **25 %** | 5 000 m | 1 727 Mi = **23 %** | 7 404 Mi |
| API pods (peak sampled) | 269–336 m = **54–67 % of the 500m limit** | 500 m | 94–119 Mi = **18–23 % of the 512Mi limit** | 512 Mi |
| MySQL | 94 m = 9 % | 1 000 m | 445 Mi vs **442 Mi at idle** (flat) | 512 Mi |

**Verdict: the system was neither memory-bound nor classically CPU-bound — it was
*connection-bound* (a software/service limit).**

* **Not memory-bound:** memory sat at ~18–23 % of every ceiling and MySQL's footprint was
  flat (445 Mi under load vs 442 Mi idle) — no growth, no pressure, no OOM kills
  (`RESTARTS` were probe/startup related, not OOM).
* **Not CPU-bound at the node level:** peak node CPU was 25 % of 5 cores; nothing queued for
  CPU scheduling at the infrastructure layer.
* **If forced to pick one of the two:** the resource *closest* to its ceiling was **CPU** —
  API pods ran at 54–67 % of their 500 m limit while memory never exceeded 23 % of its limit,
  i.e. CPU had roughly 3× less headroom than memory. So the deployment leans CPU-bound of the
  two, but CPU was not what failed.
* **What actually failed** was `max_connections = 151`, a configuration ceiling hit
  immediately (§5) — which is why the error rate is flat from window 1 rather than climbing.

## 7. Conclusions and recommendations

| # | Finding | Recommendation |
|---|---|---|
| 1 | 48.8 % errors, all HTTP 500, flat from the first window | Raise MySQL `max_connections` (e.g. 500) **and** cap each pod's pool (`Maximum Pool Size` in the connection string, e.g. 25 ⇒ 6 × 25 = 150 < ceiling) |
| 2 | New pods crash at boot while the DB is saturated (restarts 0→3) | Add `EnableRetryOnFailure()` to `UseMySql` (EF itself suggests it in the logs) and a `startupProbe` so liveness never kills a booting pod |
| 3 | Latency stable (p95 ≈ 2 s) while errors stay flat | Queueing behaviour is healthy; once (1) is fixed the same profile should show falling error rates — rerun this script to confirm |
| 4 | Throughput 439 req/s at 500 VU | HPA reacted (1→6 pods) but 2 pods stayed unready due to (2); fixing both raises usable capacity |
| 5 | Functional behaviour perfect at zero load | 14/14 assertions remain the regression gate — run `tests/functional-test.sh` after every change |

**Overall:** correctness ✅ (14/14 functional checks), load handling ⚠ — throughput and
latency are acceptable for the profile, but the deployment fails its 1 % error-rate
requirement until the database connection ceiling is addressed. The report does not adjust
the numbers: 48.84 % is what the system measured.
