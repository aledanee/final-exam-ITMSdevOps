# Task Manager API — Final Submission (ITMS DevOps)

A **Task Manager REST API** built with ASP.NET Core 8 + MySQL 8, containerized with Docker,
orchestrated on Kubernetes, load-tested and fault-tolerance tested.

**Repository:** https://github.com/aledanee/final-exam-ITMSdevOps

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Health check (`{"status":"Healthy"}`) |
| POST | `/api/tasks` | Create a task |
| GET | `/api/tasks` | List tasks |
| GET | `/api/tasks/{id}` | Get one task |
| PUT | `/api/tasks/{id}` | Update a task |
| DELETE | `/api/tasks/{id}` | Delete a task |

Task: `{ id, title, description, isCompleted, createdAt }`

## Repository layout

```
├── .gitignore
├── README.md                ← this file (root overview)
├── REPORT.md                ← Deliverable 1 report (Docker/compose + assumptions)
├── app/                     ← Deliverable 1: containerized application
│   ├── README.md
│   ├── docker-compose.yaml  ← named network + named volume, db & api healthchecks
│   ├── db/
│   │   └── init.sql         ← MySQL first-boot schema
│   └── backend/             ← ASP.NET Core app — ALL code in a single file (Program.cs)
│       ├── Program.cs       ← endpoints, model, DbContext, DTOs
│       ├── TaskManager.csproj
│       ├── appsettings.json
│       ├── Dockerfile       ← multi-stage: sdk:8.0 → aspnet:8.0-bookworm-slim, non-root, Kestrel
│       └── .dockerignore
├── k8s/                     ← Deliverable 2: Kubernetes manifests (12 files, one per line)
│   ├── namespace.yaml       ← namespace: task-manager
│   ├── secret.yaml          ← DB credentials + API connection string
│   ├── pvc.yaml             ← mysql-data, 1Gi, ReadWriteOnce
│   ├── mysql-deployment.yaml← apps/v1, 1 replica, Recreate, exec probes
│   ├── mysql-service.yaml   ← ClusterIP:3306 (internal only)
│   ├── api-deployment.yaml  ← apps/v1, spec.replicas: 1, /health probes, non-root
│   ├── api-service.yaml     ← NodePort 30080 → 8080
│   ├── hpa.yaml             ← autoscaling/v2: 1→6 pods at 60 % CPU
│   ├── ingress.yaml         ← optional host routing (needs ingress-nginx)
│   ├── kustomization.yaml   ← kubectl apply -k k8s/
│   ├── README.md            ← how to deploy + assumptions
│   └── REPORT.md            ← k8s evidence: pods/services, HPA, restarts
├── tests/
│   └── functional-test.sh   ← Deliverable 3: functional assertions (14 checks)
└── load/                    ← Deliverable 3: load testing
    ├── load-test.js         ← k6 script: 500 VUs × 3 min
    └── REPORT.md            ← latency/error tables over time, root cause, CPU vs memory
```

## Quick start

**Docker Compose:**
```bash
cd app && docker compose up --build
# API + Swagger: http://localhost:8080/swagger   health: http://localhost:8080/health
```

**Kubernetes (Minikube):**
```bash
minikube start --driver=docker
minikube addons enable metrics-server
minikube image load aledanee/task-manager-api:1.0
kubectl apply -k k8s/
kubectl -n task-manager get pods,svc,hpa
curl http://$(minikube ip):30080/health
```

## Key results (evidence in the reports)

| Check | Result |
|---|---|
| Compose | `db (healthy)` + `api (healthy)` — API healthcheck polls `/health` |
| K8s rollout | `deploy/api 1/1`, `deploy/mysql 1/1`, PVC `Bound` |
| CRUD through Service | 201 / 200 / 200 / 204 / 404 / 400 as specified |
| Non-root | Dockerfile `USER $APP_UID` and pod `securityContext` → `uid=1654(app)` |
| Autoscaling | HPA `1 → 5` replicas under load (CPU 497 % vs 60 % target), back to 1 when idle |
| Fault tolerance | `kill 1` in a pod → **RESTARTS 0 → 1**, pod healthy again, data intact (1 112 rows) |
| Functional validation | `tests/functional-test.sh` → **14/14 passed** (exit 0) |
| Load test (k6, 500 VU × 3 min) | 21 696 reqs, **117.1 req/s**, p95 **11.57 s**, **22.38 % errors** (42 % peak) → root-caused to MySQL `max_connections=151`, restarts 2 → 9 (see `load/REPORT.md` §5) |

## Reports

* `REPORT.md` — Deliverable 1 (Dockerfile, compose, ports/versions/DB/env assumptions)
* `k8s/REPORT.md` — Deliverable 2 (kubectl evidence, probes, HPA, restart count)
* `k8s/README.md` — deployment steps + assumptions
* `load/REPORT.md` — Deliverable 3 (functional validation, latency/error tables over time, root-cause and CPU/memory analysis)
