# Deliverable 2 — Kubernetes Deployment

Manifests that run the Deliverable 1 Task Manager API (`app/backend`) and MySQL 8 on Kubernetes.

## Files

| File | Kind | Purpose |
|---|---|---|
| `namespace.yaml` | Namespace | Isolates everything in `task-manager` |
| `secret.yaml` | Secret | DB credentials + API connection string |
| `pvc.yaml` | PersistentVolumeClaim | `mysql-data` (1Gi, RWO) so data survives pod restarts |
| `mysql-deployment.yaml` | Deployment | MySQL 8, `Recreate` strategy, probes, limits, PVC mount |
| `mysql-service.yaml` | Service | `ClusterIP` — internal only, reachable as `mysql:3306` |
| `api-deployment.yaml` | Deployment | 1 replica of the API (**`spec.replicas: 1`**), HTTP probes on `/health`, non-root |
| `api-service.yaml` | Service | `NodePort 30080` → API on port 8080 |
| `hpa.yaml` | HorizontalPodAutoscaler | scales the API 1 → 6 pods on 60% CPU (floor = 1) |
| `ingress.yaml` | Ingress | Optional host-based route (`task-manager.local`), needs ingress-nginx |
| `kustomization.yaml` | Kustomization | `kubectl apply -k k8s/` applies everything in order |

## Deploy

```bash
# 1. build the API image from Deliverable 1
cd app && docker build -t aledanee/task-manager-api:1.0 backend

# 2. apply everything
kubectl apply -k k8s/

# 3. watch it come up
kubectl -n task-manager get pods -w
kubectl -n task-manager get pvc
```

## Call the API

```bash
# any cluster: through the NodePort
curl http://<node-ip>:30080/health
curl http://<node-ip>:30080/api/tasks

# handy local shortcut
kubectl -n task-manager port-forward svc/api 8080:8080
curl http://localhost:8080/api/tasks
```

## Useful commands

```bash
kubectl -n task-manager get all                 # deployments, pods, services
kubectl -n task-manager get hpa                 # autoscaler: CURRENT/60% TARGETS
kubectl -n task-manager logs deploy/api         # API logs
kubectl -n task-manager scale deploy/api --replicas=4   # manual override (HPA resumes control after)
kubectl -n task-manager delete pod -l app=api   # force a rolling restart
```

The HPA needs metrics:

```bash
minikube addons enable metrics-server           # minikube
kubectl top pods -n task-manager                # sanity check that metrics flow
```

## Report

Deployment evidence (Minikube `kubectl` output), probe configuration and justification of
every choice: see **[REPORT.md](REPORT.md)**.

## Assumptions (Deliverable 2)

* **Kubernetes version:** manifests use stable GA APIs (`apps/v1`, `v1`, `networking.k8s.io/v1`),
  compatible with Kubernetes ≥ 1.19 (verified on **Minikube, Kubernetes v1.37.0**).
* **Image location:** the API image is referenced as `aledanee/task-manager-api:1.0`.
  Push it to a registry (`docker push`) for a remote cluster, or load the locally built image
  into a local cluster (`kind load docker-image …` / `minikube image load …`).
  `imagePullPolicy: IfNotPresent` makes both work. MySQL uses the public `mysql:8.0`.
* **Ports:** container/service port **8080** (Kestrel), MySQL **3306**, exposed to the outside
  as **NodePort 30080**. Choose a different `nodePort` if 30080 is taken.
* **Storage:** the PVC uses the cluster's **default StorageClass** (provisioner-dependent:
  `standard`/`local-path` on kind/minikube, `gp3`/`standard` on cloud). 1Gi, `ReadWriteOnce`.
* **Database bootstrap:** the MySQL image's own env vars (`MYSQL_DATABASE`, `MYSQL_USER`,
  `MYSQL_PASSWORD`, `MYSQL_ROOT_PASSWORD`) create `taskdb` + `taskuser` on first boot —
  the same result as `app/db/init.sql`, which is therefore **not** mounted here. The `Tasks`
  table is created by the API itself on startup (`EnsureCreated` + `information_schema` check).
* **Environment variables:** `ASPNETCORE_ENVIRONMENT=Production` (plain env) and
  `ConnectionStrings__DefaultConnection` (from the Secret, note the `__` nesting separator).
* **Secrets are plaintext** `stringData` — acceptable for an exam; use Sealed/External Secrets
  or your platform's secret store in production.
* **Scaling:** API is stateless → starts at `spec.replicas: 1` (assignment requirement), with
  an HPA floor of 1 and a max of 6 (see `hpa.yaml`). MySQL stays at 1 replica
  (single RWO volume, `Recreate` strategy); HA/StatefulSet is out of scope. The HPA requires
  `metrics-server` (`minikube addons enable metrics-server`).
* **Security context:** the API pod runs as the image's non-root `app` user (uid 1654),
  all capabilities dropped, no privilege escalation — consistent with the Deliverable 1 Dockerfile.
* **Ingress** requires an ingress-nginx controller; without one the resource is inert.
