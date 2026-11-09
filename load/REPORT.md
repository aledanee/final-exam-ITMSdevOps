# Deliverable 3 — Functional Validation and Load Testing (Report)

**Repository:** https://github.com/aledanee/final-exam-ITMSdevOps

## 1. Scope, tools and system under test

| Item | Value |
|---|---|
| System under test (SUT) | Task Manager API deployed on **Minikube** (Kubernetes v1.37.0), Service `NodePort 30080` |
| Stack | API `aledanee/task-manager-api:1.0` (Kestrel, 1→6 pods via HPA) + MySQL 8.0 (1 pod, PVC) |
| Load tool | **k6 (Grafana)** — one of the recommended tools |
| Functional tool | Bash + `curl` assertions (`tests/functional-test.sh`) |
| Load profile | **500 virtual users × 3 minutes**, 85 % `GET /api/tasks`, 10 % `GET /api/tasks/{id}`, 5 % `POST /api/tasks`, 100 ms think time |
| Scripts (committed) | `load/load-test.js` (k6), `tests/functional-test.sh` (functional assertions) |

Reproduce:

```bash
# functional validation (works against Kubernetes or Docker Compose)
./tests/functional-test.sh http://192.168.49.2:30080
./tests/functional-test.sh http://localhost:8080

# load test (500 VUs for 3 minutes) with raw samples for the time-window table
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

## 3. Load test results (k6, 500 VU × 3 minutes)

```
http_req_duration..............: avg=3.8s  min=0s  med=2.73s  max=41.32s  p(90)=7.11s  p(95)=11.57s
http_req_failed................: 22.38%  4857 out of 21696
http_reqs......................: 21696  117.12/s
threshold http_req_failed rate<0.01 → ✗ (crossed — see root cause in §5)
```

| Metric | Value |
|---|---|
| Duration | **3 minutes** (180 s + drain) |
| Total requests | **21 696** |
| Throughput | **117.1 req/s** |
| Latency avg / median | 3.80 s / 2.73 s |
| Latency p90 / p95 / max | 7.11 s / **11.57 s** / 41.32 s |
| Error rate | **22.38 %** (4 857 failed) |

Failure composition: **4 446 × HTTP 500**, **384 × network errors (status 0, client gave up)**
and 27 × expected 404s (counted by k6 as non-2xx).

Response codes across the whole run:

| 200 | 201 | 404 (expected) | 500 | 0 (timeout) |
|---|---|---|---|---|
| 16 005 | 834 | 27 | 4 446 | 384 |

## 4. Latency percentiles and error rate over time (per 30 s window)

| Window (s) | Requests | Req/s | Avg | p50 | p90 | p95 | Max | Errors | Error % |
|---|---|---|---|---|---|---|---|---|---|
| 0–30 | 2 593 | 86 | 5.09 s | 4.999 s | 6.69 s | 7.11 s | 15.7 s | 2 | 0.08 % |
| 30–60 | 1 162 | 39 | 7.13 s | 6.18 s | 11.70 s | 13.39 s | 20.5 s | 35 | 3.01 % |
| 60–90 | 1 049 | 35 | 13.99 s | 11.82 s | 20.82 s | 21.54 s | 33.0 s | 5 | 0.48 % |
| 90–120 | 5 691 | 190 | 2.81 s | 2.09 s | 3.97 s | 8.25 s | 41.3 s | 2 395 | 42.08 % |
| 120–150 | 5 361 | 179 | 2.64 s | 2.51 s | 4.25 s | 4.88 s | 11.8 s | 1 471 | 27.44 % |
| 150–180 | 5 340 | 178 | 2.57 s | 2.32 s | 4.42 s | 5.20 s | 11.4 s | 859 | 16.09 % |
| 180–210 (drain) | 500 | 17 | 5.08 s | 4.91 s | 8.30 s | 8.72 s | 17.8 s | 90 | 18.00 % |

Error distribution by operation (whole run):

| Operation | Success | 404 (expected) | HTTP 500 | Timeout (0) |
|---|---|---|---|---|
| `list` | 14 372 | — | 3 764 | 297 |
| `get` | 1 633 | 27 | 471 | 22 |
| `create` | 834 | — | 211 | 65 |

**Reading of the table — three distinct phases:**

1. **Seconds 0–90 — saturation:** almost no errors (0.08 % → 3 %) but latency explodes
   (median 5 s → 11.8 s, p95 21.5 s) and throughput collapses (86 → 35 req/s). Requests
   queue for scarce database connections; the connection pool is being exhausted.
2. **Seconds 90–120 — the breaking point:** pods begin crash-looping (see §5), queues are
   flushed so latency *drops* to ~2.8 s and throughput jumps back to 190 req/s — but now
   nearly half of requests fail fast with 500: **error rate spikes to 42 %**.
3. **Seconds 120–210 — partial recovery:** error rate falls (27 % → 16 %) as pools recycle
   and pods stabilise, but it never reaches the 1 % target while connections remain scarce.

This pattern — latency ramp first, then an error spike exactly when containers restart — is
the signature of a hard capacity limit (database connections), not of a gradually saturating
CPU or memory resource.

## 5. Root cause — MySQL connection exhaustion

Evidence collected during/after the run:

```
$ kubectl exec deploy/mysql -- mysql -utaskuser -ptaskpass taskdb -e "SELECT 1;"
ERROR 1040 (08004): Too many connections

max_connections        151
Max_used_connections   152      ← ceiling hit again during this run
Threads_connected      100      (draining after the load stopped)
```

Chain of events (same failure mode as the 60 s pilot run, now observed over 3 minutes):

1. 6 API pods × EF/MySqlConnector pools (up to 100 per pod) ⇒ demand far beyond
   `max_connections = 151`.
2. Requests needing a **new** connection get `Too many connections` → EF throws → the app's
   error middleware returns **500** (4 446 requests), or the client gives up first (384 timeouts).
3. Pods that could not reach the database at boot burned through the startup retries
   (`Database not ready (attempt 8/12) …`) and exited; kubelet restarted them.

**Container restart count rising during the test** (sum of `RESTARTS` over all API pods,
sampled every 5 s):

```
14:12:25Z restarts_sum=2        ← test starts
14:13:02Z restarts_sum=3
14:14:04Z restarts_sum=4
14:14:46Z restarts_sum=6        ← error spike window (§4 phase 2)
14:15:02Z restarts_sum=7
14:15:17Z restarts_sum=8
14:15:33Z restarts_sum=9        ← test ends
```

Final state — every pod recovered **without manual intervention**:

```
14:16:13Z
NAME                   READY   STATUS    RESTARTS      AGE
api-699cbf7d4b-c66r9   1/1     Running   1 (88s ago)   2m31s
api-699cbf7d4b-kcvz8   1/1     Running   1 (58s ago)   2m31s
api-699cbf7d4b-l7wvf   1/1     Running   1 (42s ago)   2m16s
api-699cbf7d4b-m55kz   1/1     Running   0             2m31s
api-699cbf7d4b-p4nbs   1/1     Running   1 (89s ago)   2m31s
api-699cbf7d4b-xx5jv   1/1     Running   5 (75s ago)   172m
hpa: cpu 17%/60%  replicas 6 (max)
GET /api/tasks -> 200
```

Data intact: **3 077 tasks** in MySQL (rows written under load all survived).

## 6. CPU-bound or memory-bound?

Samples every 5 s during the 3-minute run (`kubectl top`), node capacity **5 CPU / 7 404 Mi**,
container limits from `k8s/*.yaml` (API 500m / 512Mi, MySQL 1 000m / 512Mi):

| Layer | CPU observed (peak) | CPU ceiling | Memory observed (peak) | Memory ceiling |
|---|---|---|---|---|
| Node | 1 828 m = **36 %** | 5 000 m | 1 839 Mi = **24 %** | 7 404 Mi |
| API pods | up to **489 m ≈ 98 % of the 500 m limit** (others 13–359 m) | 500 m | 145 Mi = **28 % of the 512 Mi limit** | 512 Mi |
| MySQL | 133 m = 13 % | 1 000 m | 477 Mi = **93 % of its 512 Mi limit** (442 Mi at idle) | 512 Mi |

**Verdict: the system was *connection-bound* — neither classically CPU-bound nor
memory-bound.**

* **Not memory-bound:** node memory never exceeded 24 %, API pods stayed at ≤ 28 % of their
  limit, and although MySQL sat highest of all (93 % of its limit) it never crossed it —
  **zero OOM kills** occurred (`RESTARTS` were startup/liveness events, not OOM).
* **Not CPU-bound at the infrastructure level:** node CPU peaked at 36 % of 5 cores, so the
  cluster as a whole had CPU headroom throughout.
* **If forced to choose one of the two:** **CPU** was the more constrained resource —
  individual API pods briefly hit **98 % of their 500 m CPU limit** (inviting throttling),
  while memory everywhere retained ≥ 72 % headroom. So the deployment leans CPU-bound of the
  two, but CPU is not what produced the failures.
* **What actually failed** was `max_connections = 151` (`Max_used_connections 152`): a
  configuration ceiling hit instantly, which is why errors concentrate at the moment pods
  could no longer obtain connections rather than growing smoothly with resource usage.

## 7. Conclusions and recommendations

| # | Finding | Recommendation |
|---|---|---|
| 1 | 22.38 % errors overall (42 % peak), all server-side: 500s + timeouts | Raise MySQL `max_connections` (e.g. 500) **and** cap each pod's pool (`Maximum Pool Size`, e.g. 25 ⇒ 6 × 25 = 150 < ceiling) |
| 2 | Restart count climbed 2 → 9 during the run; new pods died at boot | Add `EnableRetryOnFailure()` to `UseMySql` (EF suggests it in the logs) and a `startupProbe` so liveness never kills a booting pod |
| 3 | Latency ramped to 21 s p95 *before* errors spiked | Pool starvation shows up as queueing first — the table in §4 gives an early warning ~90 s before containers started failing |
| 4 | Throughput fell 440 → 117 req/s as restarts accumulated | Fixes (1)+(2) keep all 6 HPA pods ready, restoring throughput at the same load |
| 5 | Functional behaviour perfect at zero load | 14/14 assertions remain the regression gate — run `tests/functional-test.sh` after every change |
| 6 | System recovered fully by itself (pods 1/1, API 200, data intact) | Liveness/readiness/self-healing work as designed — reliability of the *orchestration* is proven even though the *database config* is not |

**Overall:** correctness ✅ (14/14 functional checks) and self-healing ✅ (restarts 2 → 9, all
pods back to `1/1`, no data loss across 3 077 rows). Load handling ⚠ — during a sustained
3-minute 500-VU test the deployment cannot hold the 1 % error target until the database
connection ceiling is raised. The numbers above are reported exactly as measured.
