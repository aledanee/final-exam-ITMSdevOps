// Deliverable 3 — Load test: 500 virtual users × 60 s against the Task Manager API (Kubernetes).
// Tool: k6 (Grafana). Mix: 85 % list reads, 10 % single-item reads, 5 % creates, 100 ms think time.
//
// Run (from repo root):
//   docker run --rm --network host \
//     -v "$PWD/load:/scripts" grafana/k6 run \
//     --out json=/tmp/k6-samples.json /scripts/load-test.js
//
// The JSON output contains every sample, which is aggregated per 10 s window in
// load/REPORT.md (latency percentiles + error rate over time).
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://192.168.49.2:30080'; // Minikube Service (NodePort 30080)

export const options = {
  vus: 500,
  duration: '60s',
  thresholds: {
    // course criterion: error rate must stay below 1 %
    http_req_failed: ['rate<0.01'],
  },
};

let nextId = 1;

export default function () {
  const r = Math.random();
  let res;

  if (r < 0.85) {
    // read: list all tasks
    res = http.get(`${BASE}/api/tasks`, { tags: { op: 'list' } });
    check(res, { 'list is 200': (x) => x.status === 200 });
  } else if (r < 0.95) {
    // read: fetch a single task
    const id = Math.floor(Math.random() * 50) + 1;
    res = http.get(`${BASE}/api/tasks/${id}`, { tags: { op: 'get' } });
    check(res, { 'get is 200 or 404': (x) => x.status === 200 || x.status === 404 });
  } else {
    // write: create a task
    res = http.post(
      `${BASE}/api/tasks`,
      JSON.stringify({ title: `vu${__VU}-${nextId++}`, description: 'deliverable 3 load test' }),
      { headers: { 'Content-Type': 'application/json' }, tags: { op: 'create' } },
    );
    check(res, { 'create is 201': (x) => x.status === 201 });
  }

  sleep(0.1); // think time
}
