# Deliverable 3 — Load Test Results

**Repo:** https://github.com/aledanee/final-exam-ITMSdevOps
**Test:** k6 · 500 users × 3 minutes · script: `load/load-test.js` · target: API on Minikube

## Result

| Metric | Value |
|---|---|
| Total requests | 21 696 |
| Throughput | 117.1 req/s |
| Latency avg / median | 3.80 s / 2.73 s |
| Latency p90 / p95 / max | 7.11 s / **11.57 s** / 41.32 s |
| Error rate | **22.38 %** (4 857 failed · target < 1 % ❌) |

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
