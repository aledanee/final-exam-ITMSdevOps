// Load test — 500 virtual users for 60 seconds against the Task Manager API.
// Required metrics: latency (avg/p95/max), error rate, throughput.
// Run:
//   docker run --rm --network host -v "$PWD/load:/scripts" grafana/k6 run /scripts/load-test.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://192.168.49.2:30080';
const errors = new Rate('check_failed'); // any non-2xx/expected outcome

export const options = {
  vus: 500,
  duration: '60s',
  thresholds: {
    http_req_failed: ['rate<0.01'],          // error rate < 1%
    http_req_duration: ['p(95)<500'],        // 95% of requests < 500ms
    check_failed: ['rate<0.01'],
  },
};

let nextId = 1;

export default function () {
  const r = Math.random();
  let res;

  if (r < 0.85) {
    // read: list all tasks
    res = http.get(`${BASE}/api/tasks`, { tags: { op: 'list' } });
    check(res, { 'list is 200': (x) => x.status === 200 }) || errors.add(1);
  } else if (r < 0.95) {
    // read: fetch one task
    const id = Math.floor(Math.random() * 50) + 1;
    res = http.get(`${BASE}/api/tasks/${id}`, { tags: { op: 'get' } });
    check(res, { 'get is 200 or 404': (x) => x.status === 200 || x.status === 404 }) || errors.add(1);
  } else {
    // write: create a task
    res = http.post(
      `${BASE}/api/tasks`,
      JSON.stringify({ title: `vu${__VU}-${nextId++}`, description: 'load test' }),
      { headers: { 'Content-Type': 'application/json' }, tags: { op: 'create' } },
    );
    check(res, { 'create is 201': (x) => x.status === 201 }) || errors.add(1);
  }

  sleep(0.1); // small think time so 500 VUs don't hammer unbounded
}
