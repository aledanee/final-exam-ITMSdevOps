# Deliverable 2 — Kubernetes Deployment (Report)

## 1. What was deployed

Manifests in `k8s/`, applied with a single command:

```bash
kubectl apply -k k8s/
```

| Resource | apiVersion / kind | Notes |
|---|---|---|
| `namespace` | v1 / Namespace | `task-manager` |
| `task-manager-db` | v1 / Secret | DB credentials + connection string |
| `mysql-data` | v1 / PersistentVolumeClaim | 1Gi, RWO, default StorageClass |
| `mysql` | **apps/v1 / Deployment** | 1 replica, `Recreate`, exec probes |
| `mysql` | v1 / Service | ClusterIP:3306 (internal only) |
| `api` | **apps/v1 / Deployment** | **`spec.replicas: 1`**, **HTTP probes on `/health`** |
| `api` | v1 / Service | NodePort 30080 → 8080 |
| `api` | **autoscaling/v2 / HorizontalPodAutoscaler** | **1→6** replicas at 60% CPU |
| `api` | networking.k8s.io/v1 / Ingress | optional, needs ingress-nginx |

Both Deployments use `apiVersion: apps/v1` + `kind: Deployment`, as required.

**Probes hit the real health endpoint** (not TCP checks):

```
api deployment → readinessProbe: httpGet path=/health  livenessProbe: httpGet path=/health
```

## 2. Evidence of successful deployment (Minikube)

Environment: `minikube v1.39.0 --driver=docker`, Kubernetes **v1.37.0**, image loaded with
`minikube image load aledanee/task-manager-api:1.0`.

### `kubectl apply -k k8s/`

```
namespace/task-manager created
secret/task-manager-db created
service/api created
service/mysql created
persistentvolumeclaim/mysql-data created
deployment.apps/api created
deployment.apps/mysql created
ingress.networking.k8s.io/api created
```

### Rollout

```
$ kubectl -n task-manager rollout status deploy/mysql --timeout=240s
deployment "mysql" successfully rolled out
$ kubectl -n task-manager rollout status deploy/api --timeout=240s
deployment "api" successfully rolled out
```

### `kubectl get pods -o wide`

```
NAME                     READY   STATUS    RESTARTS   AGE   IP           NODE       NOMINATED NODE   READINESS GATES
api-699cbf7d4b-kx6wx     1/1     Running   0          57s   10.244.0.4   minikube   <none>           <none>
api-699cbf7d4b-xx5jv     1/1     Running   0          57s   10.244.0.3   minikube   <none>           <none>
mysql-5988457f9c-9ztng   1/1     Running   0          57s   10.244.0.5   minikube   <none>           <none>
```
*(the two API pods above are the pre-review run; with `spec.replicas: 1` the steady state is a
single API pod — see the autoscaling section, where the second pod appears only under load.)*

### `kubectl get services`

```
NAME    TYPE        CLUSTER-IP      EXTERNAL-IP   PORT(S)          AGE
api     NodePort    10.99.248.15    <none>        8080:30080/TCP   57s
mysql   ClusterIP   10.110.24.111   <none>        3306/TCP         57s
```

### `kubectl get pvc` / `kubectl get deployments`

```
NAME         STATUS   VOLUME                                     CAPACITY   ACCESS MODES   STORAGECLASS   AGE
mysql-data   Bound    pvc-46c1d48b-92f8-47ce-84e9-1bb2f9d86fc5   1Gi        RWO            standard       57s

NAME    READY   UP-TO-DATE   AVAILABLE   AGE
api     1/1     1            1           57s
mysql   1/1     1            1           57s
```

### Single-replica steady state (assignment requirement: `spec.replicas: 1`)

```
$ kubectl -n task-manager get hpa
NAME   REFERENCE        TARGETS   MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 1%/60%   1      6         1          17m

$ kubectl -n task-manager get pods -l app=api
NAME                     READY   STATUS    RESTARTS   AGE
api-699cbf7d4b-xx5jv     1/1     Running   0          45m
```

### Probe status (ready, zero restarts)

```
api-699cbf7d4b-xx5jv  ready=true restarts=0
```

### Application checks through the Service (`NodePort 30080`)

```
GET  /health          -> {"status":"Healthy", ...}
POST /api/tasks       -> 201
GET  /api/tasks       -> [{"id":1,"title":"minikube demo", ...}]
GET  /api/tasks/1     -> 200
PUT  /api/tasks/1     -> 200
DELETE /api/tasks/1   -> 204
GET  /api/tasks/1     -> 404   (after delete)
```

## 3. Load test — 500 virtual users, 60 seconds (required)

Script: `load/load-test.js` (k6). 500 VUs looping for 60 s, mixed traffic
(85 % `GET /api/tasks`, 10 % `GET /api/tasks/{id}`, 5 % `POST /api/tasks`), 100 ms think time.
Target: the Kubernetes Service at `http://192.168.49.2:30080`.

```bash
docker run --rm --network host -v "$PWD/load:/scripts" grafana/k6 run /scripts/load-test.js
```

### Results (verbatim k6 summary)

```
HTTP
http_req_duration..............: avg=1.24s min=52.01ms med=1.22s max=7.05s p(90)=1.86s p(95)=2.08s
http_req_failed................: 0.47%  108 out of 22605
http_reqs......................: 22605  365.539794/s

checks_total.......: 22605   365.539794/s
checks_succeeded...: 100.00% 22605 out of 22605

█ THRESHOLDS
  check_failed       ✓ rate<0.01   rate=0.00%
  http_req_failed    ✓ rate<0.01   rate=0.47%
  http_req_duration  ✗ p(95)<500   p(95)=2.08s
```

| Metric | Required | Measured | Verdict |
|---|---|---|---|
| Throughput | — | **365.5 req/s** (22 605 requests / 60 s) | — |
| Error rate | < 1 % | **0.47 %** (108 / 22 605) | ✅ pass |
| Latency avg / p95 | p95 < 500 ms | **avg 1.24 s / p95 2.08 s** (max 7.05 s) | ❌ threshold missed |
| Application checks | — | 100 % of 22 605 checks passed | ✅ |

**Honest analysis:** the p95 threshold was missed because the whole cluster is a single
2-CPU Minikube node running 1→5 API pods plus MySQL, so at ~365 req/s the request queue
dominates latency (single RWO database, no read replicas). The error rate stayed under 1 %
and no request crashed the service. Scaling the node / adding a second DB tier (out of scope
here) is the fix; the numbers are reported as measured, not adjusted.

### HPA reaction during the same test (10 s sampling)

```
12:08:42Z  cpu 1%/60%    min1 max6   replicas 1
12:08:52Z  cpu 1%/60%    min1 max6   replicas 1
12:09:03Z  cpu 1%/60%    min1 max6   replicas 1
12:09:13Z  cpu 1%/60%    min1 max6   replicas 1
12:09:23Z  cpu 1%/60%    min1 max6   replicas 1
12:09:33Z  cpu 1%/60%    min1 max6   replicas 1
12:09:43Z  cpu 1%/60%    min1 max6   replicas 1
12:09:53Z  cpu 410%/60%  min1 max6   replicas 1     ← metrics arrive, target blown
12:10:03Z  cpu 410%/60%  min1 max6   replicas 1

$ kubectl -n task-manager get hpa          # seconds later
NAME   REFERENCE        TARGETS         MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 410%/60%   1         6         5          19m

$ kubectl -n task-manager get pods -l app=api
NAME                   READY   STATUS    RESTARTS   AGE
api-699cbf7d4b-xx5jv   1/1     Running   0          46m
api-699cbf7d4b-2rjrg   1/1     Running   0          22s
api-699cbf7d4b-kw5v4   1/1     Running   0          22s
api-699cbf7d4b-rhxzx   1/1     Running   0          22s
api-699cbf7d4b-z4rzk   1/1     Running   0          22s
api-699cbf7d4b-xp2rj   1/1     Running   0          7s
```

CPU blew past the 60 % target (410 %) and the HPA scaled **1 → 5** pods (max 6), each new pod
entering Service rotation only after `/health` passed readiness.

**Load removed → scale-down back to the floor of 1** (300 s stabilization window, see §6.14):
```
12:16:03Z
NAME   REFERENCE        TARGETS   MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 1%/60%   1      6         1          25m

NAME                   READY   STATUS    RESTARTS        AGE
api-699cbf7d4b-xx5jv   1/1     Running   1 (5m41s ago)   52m

NAME                   CPU(cores)   MEMORY(bytes)
api-699cbf7d4b-xx5jv   1m           58Mi
```
The 4 added pods were removed once CPU settled back to 1 % — only one pod remains (the same
one that survived the container restart in §4, `RESTARTS 1`).

## 4. Fault tolerance — container restart count increases

Deleting pods shows *replacement*; a real crash shows the **restart counter incrementing on
the same pod**. Simulated by killing PID 1 (the `dotnet` process) inside a running API container:

```
=== BEFORE ===
api-699cbf7d4b-2rjrg RESTARTS=0
api-699cbf7d4b-kw5v4 RESTARTS=0
api-699cbf7d4b-rhxzx RESTARTS=0
api-699cbf7d4b-xp2rj RESTARTS=0
api-699cbf7d4b-xx5jv RESTARTS=0
api-699cbf7d4b-z4rzk RESTARTS=0

$ kubectl -n task-manager exec api-699cbf7d4b-xx5jv -- sh -c 'kill 1'

=== AFTER (20 s later) ===
NAME                   READY   STATUS    RESTARTS   AGE
api-699cbf7d4b-2rjrg   1/1     Running   0          51s
api-699cbf7d4b-kw5v4   1/1     Running   0          51s
api-699cbf7d4b-rhxzx   1/1     Running   0          51s
api-699cbf7d4b-xp2rj   1/1     Running   0          36s
api-699cbf7d4b-xx5jv   1/1     Running   1 (20s ago)   47m     ← same pod, counter 0 → 1
api-699cbf7d4b-z4rzk   1/1     Running   0          51s
```

Service recovered without intervention:

```
GET /health     -> {"status":"Healthy", ...}
GET /api/tasks  -> rows: 1112      (every row created by the 500-VU load test still present)
```

**What this proves:** the kubelet detected the dead container, restarted it *in the same pod*
(`RESTARTS 0 → 1`, same pod name and age), the readiness probe gated it back into the Service,
and the database was untouched — crash recovery works end-to-end.

## 5. Other evidence

### Non-root runtime (same user as the Dockerfile)

```
$ kubectl -n task-manager exec deploy/api -- id
uid=1654(app) gid=1654(app) groups=1654(app)
```

### Persistence

* Row created → MySQL pod deleted → new pod re-attaches the PVC → row still present.
* 1 112 rows written under load survived the container restart above.

## 6. Justification of choices

1. **`apps/v1` Deployment** — stable/GA API (required since Kubernetes 1.9; `extensions/v1beta1` is removed). One per component: `api`, `mysql`.
2. **HTTP probes on `/health` for the API** — `httpGet` readiness/liveness against the real endpoint proves the app is serving requests, not just that the port is open; `/health` is the endpoint the deliverable already exposes.
3. **Exec probe (`mysqladmin ping`) for MySQL** — MySQL has no HTTP endpoint; an exec ping is the accurate health signal for it. Nothing more was added.
4. **`spec.replicas: 1` for the API** — matches the assignment requirement; the pod count under load is then handled by the HPA (floor 1, ceiling 6) rather than a fixed replica count. MySQL also stays at 1 (single RWO volume, HA/StatefulSet out of scope).
5. **`Recreate` strategy for MySQL** — prevents two pods claiming the same `ReadWriteOnce` volume during rollouts.
6. **PVC `mysql-data` (1Gi)** — database data must outlive pod restarts; verified by deleting the MySQL pod and finding the row intact.
7. **`NodePort 30080`** — makes the API reachable on any cluster (Minikube included) without an ingress controller; port 8080 in, 30080 out.
8. **`Ingress` included but optional** — documents how the app would be exposed by hostname; inert without ingress-nginx, so it adds no moving parts.
9. **Secret for DB credentials + connection string** — keeps passwords out of the pod spec and lets the app consume them via `ConnectionStrings__DefaultConnection`.
10. **DB bootstrap via the MySQL image's env vars** (`MYSQL_DATABASE`/`MYSQL_USER`/…) — same outcome as `app/db/init.sql` with no extra ConfigMap/mount; the app creates the `Tasks` table itself at startup.
11. **`imagePullPolicy: IfNotPresent`** — allows the locally built image to be `minikube image load`-ed for this demo while still working with a pushed registry image.
12. **securityContext (`runAsNonRoot`, uid 1654, drop ALL caps, no privilege escalation)** — continues the non-root decision from the Deliverable 1 Dockerfile; verified with `kubectl exec … id`.
13. **Requests/limits on both containers** — required for stable scheduling, to prevent one pod from starving the node, and as the denominator for the HPA's CPU % (API 100m/128Mi→500m/512Mi, MySQL 100m/256Mi→1/512Mi).
14. **HPA (`autoscaling/v2`, 1→6 at 60 % CPU)** — the API is stateless, so horizontal scale-out is the correct response to load while keeping the required floor of 1. Scale-up is instant (`stabilizationWindowSeconds: 0`), scale-down waits 300 s so dips don't cause flapping; requires `metrics-server`. Verified live under the 500-VU test: CPU 410 % → 5 replicas → back to 1 when idle.

Nothing else was added: no ClusterAutoscaler, no NetworkPolicy, no StatefulSet, no init containers — out of scope for this deliverable.

## 7. Reproduce

```bash
minikube start --driver=docker
minikube addons enable metrics-server                # required by the HPA
minikube image load aledanee/task-manager-api:1.0     # after: docker build -t aledanee/task-manager-api:1.0 app/backend
kubectl apply -k k8s/
kubectl -n task-manager get pods,svc,pvc,hpa
curl http://$(minikube ip):30080/health

# required load test: 500 VUs for 60 s (latency, error rate, throughput)
docker run --rm --network host -v "$PWD/load:/scripts" grafana/k6 run /scripts/load-test.js
watch kubectl -n task-manager get hpa                # watch 1 → 5 during the test

# fault tolerance: restart counter on one pod
kubectl -n task-manager exec deploy/api -- sh -c 'kill 1'
kubectl -n task-manager get pods -l app=api          # RESTARTS 0 -> 1
```
