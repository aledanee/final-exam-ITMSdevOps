# Pod Drop Test — Results

**Repo:** https://github.com/aledanee/final-exam-ITMSdevOps
**Test:** force-delete the API pod on Minikube and measure recovery · 2026-11-09 14:44 UTC

## Result

| Metric | Value |
|---|---|
| Pod dropped | `api-699cbf7d4b-v45ws` at 14:44:29Z (`kubectl delete pod --force`) |
| Replacement ready | **6 seconds** (14:44:35Z) |
| Desired vs ready after | 1/1 `Running` — deployment fully self-healed |
| Health check after | `{"status":"Healthy"}` → 200 |
| Data intact | 3 594 rows before = 3 594 rows after |
| CRUD after the drop | POST → 201 · GET → 200 |

## Before / after

```
BEFORE 14:44:28Z
NAME                   READY   STATUS    RESTARTS   AGE   IP
api-699cbf7d4b-v45ws   1/1     Running   0          11m   10.244.0.36

AFTER 14:44:35Z   (6 s later)
NAME                   READY   STATUS    RESTARTS   AGE   IP
api-699cbf7d4b-n5cjw   1/1     Running   0          6s    10.244.0.37
```

The Deployment detected the missing pod and created a new one automatically; the new pod
passed its `/health` readiness probe before receiving traffic. Database data lives on the
PVC, so nothing was lost.
