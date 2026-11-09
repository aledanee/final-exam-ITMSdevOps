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
| `api` | **apps/v1 / Deployment** | 2 replicas, **HTTP probes on `/health`** |
| `api` | v1 / Service | NodePort 30080 → 8080 |
| `api` | **autoscaling/v2 / HorizontalPodAutoscaler** | 2→6 replicas at 60% CPU |
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
api     2/2     2            2           57s
mysql   1/1     1            1           57s
```

### Probe status (ready, zero restarts)

```
api-699cbf7d4b-kx6wx  ready=true restarts=0
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

### Horizontal autoscaling under load (`kubectl get hpa`)

Enabled with `minikube addons enable metrics-server`, then a `wget` loop was pointed at the
Service (`kubectl run load --image=busybox ... while true; do wget http://api:8080/api/tasks; done`):

**Before load — steady at the floor:**
```
NAME   REFERENCE        TARGETS       MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 1%/60%   2         6         2          30s
```

**Under load — CPU blows past the 60% target, HPA adds pods (2 → 6):**
```
NAME   REFERENCE        TARGETS         MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 333%/60%   2         6         6          2m3s

NAME                   READY   STATUS    RESTARTS   AGE
api-699cbf7d4b-kx6wx   1/1     Running   0          29m
api-699cbf7d4b-q7glg   1/1     Running   0          7s
api-699cbf7d4b-vgd2q   1/1     Running   0          7s
api-699cbf7d4b-vw5zx   1/1     Running   0          7s
api-699cbf7d4b-wdtmh   1/1     Running   0          7s
api-699cbf7d4b-xx5jv   1/1     Running   0          29m
```
Rollout finished with `6 of 6 updated replicas are available`, load then spread across the
pods (`kubectl top pods`: 325m / 341m per pod, well under the 500m limit).

**Load removed → scale-down back to the floor** (300s stabilization window, see §3.14):
```
11:59:52Z
NAME   REFERENCE        TARGETS       MINPODS   MAXPODS   REPLICAS   AGE
api    Deployment/api   cpu: 1%/60%   2         6         2          9m1s

NAME                   READY   STATUS    RESTARTS   AGE
api-699cbf7d4b-kx6wx   1/1     Running   0          36m
api-699cbf7d4b-xx5jv   1/1     Running   0          36m

NAME                   CPU(cores)   MEMORY(bytes)
api-699cbf7d4b-kx6wx   1m           81Mi
api-699cbf7d4b-xx5jv   1m           90Mi
```
The 4 added pods were removed once CPU settled back to 1%/60% — only the original 2 remain,
still at `RESTARTS 0`.

### Non-root runtime (same user as the Dockerfile)

```
$ kubectl -n task-manager exec deploy/api -- id
uid=1654(app) gid=1654(app) groups=1654(app)
```

### Persistence / resilience (also verified on a kind cluster earlier)

* Row created → MySQL pod deleted → new pod re-attaches the PVC → row still present.
* Both API pods deleted → Deployment schedules replacements → `/health` 200 again.

## 3. Justification of choices

1. **`apps/v1` Deployment** — stable/GA API (required since Kubernetes 1.9; `extensions/v1beta1` is removed). One per component: `api`, `mysql`.
2. **HTTP probes on `/health` for the API** — `httpGet` readiness/liveness against the real endpoint proves the app is serving requests, not just that the port is open; `/health` is the endpoint the deliverable already exposes.
3. **Exec probe (`mysqladmin ping`) for MySQL** — MySQL has no HTTP endpoint; an exec ping is the accurate health signal for it. Nothing more was added.
4. **2 API replicas** — the API is stateless, so replicas give availability and rolling updates; MySQL stays at 1 replica (single RWO volume, out of scope for HA).
5. **`Recreate` strategy for MySQL** — prevents two pods claiming the same `ReadWriteOnce` volume during rollouts.
6. **PVC `mysql-data` (1Gi)** — database data must outlive pod restarts; verified by deleting the MySQL pod and finding the row intact.
7. **`NodePort 30080`** — makes the API reachable on any cluster (Minikube included) without an ingress controller; port 8080 in, 30080 out.
8. **`Ingress` included but optional** — documents how the app would be exposed by hostname; inert without ingress-nginx, so it adds no moving parts.
9. **Secret for DB credentials + connection string** — keeps passwords out of the pod spec and lets the app consume them via `ConnectionStrings__DefaultConnection`.
10. **DB bootstrap via the MySQL image's env vars** (`MYSQL_DATABASE`/`MYSQL_USER`/…) — same outcome as `app/db/init.sql` with no extra ConfigMap/mount; the app creates the `Tasks` table itself at startup.
11. **`imagePullPolicy: IfNotPresent`** — allows the locally built image to be `minikube image load`-ed for this demo while still working with a pushed registry image.
12. **securityContext (`runAsNonRoot`, uid 1654, drop ALL caps, no privilege escalation)** — continues the non-root decision from the Deliverable 1 Dockerfile; verified with `kubectl exec … id`.
13. **Requests/limits on both containers** — required for stable scheduling and to prevent one pod from starving the node; sized for the workload (API 100m/128Mi→500m/512Mi, MySQL 100m/256Mi→1/512Mi).
14. **HPA (`autoscaling/v2`, 2→6 at 60% CPU)** — the API is stateless, so horizontal scale-out is the correct response to load; scale-up is instant (`stabilizationWindowSeconds: 0`) while scale-down waits 300s so traffic dips don't cause flapping. CPU % is measured against the container's `cpu: 100m` request — which is also why `requests` must be set. Requires `metrics-server`. Verified live: `cpu: 333%/60%` → 6 replicas, then back to 2 after the load stopped.

Nothing else was added: no ClusterAutoscaler, no ClusterIP-exposing Ingress controller install, no NetworkPolicy, no StatefulSet, no init containers — out of scope for this deliverable.

## 4. Reproduce

```bash
minikube start --driver=docker
minikube addons enable metrics-server                # required by the HPA
minikube image load aledanee/task-manager-api:1.0     # after: docker build -t aledanee/task-manager-api:1.0 app/backend
kubectl apply -k k8s/
kubectl -n task-manager get pods,svc,pvc,hpa
minikube service api -n task-manager --url            # or: kubectl -n task-manager port-forward svc/api 8080:8080

# optional: watch the HPA scale out under load
kubectl -n task-manager run load --image=busybox:1.37 --restart=Never --command -- \
  sh -c 'while true; do wget -qO- http://api:8080/api/tasks >/dev/null; done'
watch kubectl -n task-manager get hpa
kubectl -n task-manager delete pod load              # scale-down after the 300s window
```
