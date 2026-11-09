# Report — Task Manager API (.NET Port)

## 1. Overview

A Task Manager REST API originally written in Node.js, re-implemented in **C# / ASP.NET Core 8**
using **minimal APIs**, persisting to **MySQL 8** via Entity Framework Core (Pomelo provider).

All application code lives in a **single file**: `app/backend/Program.cs`
(the model, DbContext, DTOs and every endpoint are in that one file, as required).

## 2. CRUD endpoint parity with the original Node.js app

| Node.js (original) | C# implementation (this project) | Result codes |
|---|---|---|
| `POST /api/tasks` | `app.MapPost("/api/tasks", ...)` | 201 + body / 400 |
| `GET /api/tasks` | `app.MapGet("/api/tasks", ...)` | 200 + array |
| `GET /api/tasks/:id` | `app.MapGet("/api/tasks/{id}", ...)` | 200 / 404 |
| `PUT /api/tasks/:id` | `app.MapPut("/api/tasks/{id}", ...)` | 200 / 400 / 404 |
| `DELETE /api/tasks/:id` | `app.MapDelete("/api/tasks/{id}", ...)` | 204 / 404 |
| `GET /health` | `app.MapGet("/health", ...)` | 200 `{"status":"Healthy"}` |

Task payload shape: `{ id, title, description, isCompleted }` (+ `createdAt`, an added
convenience field that does not alter the original contract).

**Assumption:** the original Node.js app was not present in this repository, so parity is
defined against the course specification given in the brief (the endpoints listed above).
Error responses use JSON `{ "error": "..." }`, a common convention in the original Express app.

## 3. Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build     # full image: restores + publishes
...
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime   # slim image: runs the app
ENTRYPOINT ["dotnet", "TaskManager.dll"]
```

* **Official Microsoft images only** — `mcr.microsoft.com/dotnet/sdk:8.0` (build) and
  `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` (runtime, multi-stage build so the
  SDK never ships in the final image).
  *Note:* there is no `8.0-slim` tag — starting with .NET 8 the Debian slim variant is
  published as `8.0-bookworm-slim` (verified against MCR).
* **Kestrel**, ASP.NET Core's built-in web server, serves the app.
  `ASPNETCORE_URLS=http://+:8080` binds it inside the container; no reverse proxy is used.
* Restores from the `.csproj` in its own layer (cached) before copying `Program.cs`.

## 4. docker-compose.yaml

* **Named network:** `taskmanager-network` (driver `bridge`, explicit `name:`), both `api`
  and `db` are attached; the API reaches the database over the service name `db`.
* **Named volume:** `taskmanager-mysql-data` → `/var/lib/mysql`, so MySQL data survives
  `docker compose down` (add `-v` to wipe it).
* `init.sql` is mounted into `/docker-entrypoint-initdb.d/` and runs **only on first start**
  (empty data volume): creates `taskdb`, the `taskuser` account and the `Tasks` table.
* Startup ordering: `api` waits for the `db` **healthcheck** (`mysqladmin ping`) via
  `depends_on: condition: service_healthy`, plus the app itself retries connecting at boot.

## 5. Assumptions

### Ports
| Where | Port | Why |
|---|---|---|
| Host → API container | **8080** | Default ASP.NET Core 8 container port; non-privileged. Swagger UI at `http://localhost:8080/swagger`. |
| Host → MySQL | **3306** | MySQL default; exposed only for local debugging (the API talks over the internal network). |
| API → MySQL (in-network) | **3306** | Standard MySQL port. |

### .NET / runtime versions
* **.NET 8 (LTS)** — current LTS; images `sdk:8.0` / `aspnet:8.0-bookworm-slim`, `<TargetFramework>net8.0</TargetFramework>`.
* **MySQL 8.0** official image; server version 8.0.36 supplied to EF for correct SQL dialect.
* **Package versions:** EF Core 8.0.11, Pomelo.EntityFrameworkCore.MySql 8.0.2,
  Swashbuckle 6.6.2, Microsoft.AspNetCore.OpenApi 8.0.11.
* The .NET SDK is not installed on the dev machine, so the build was validated inside the
  Docker build stage (compiles clean; full CRUD smoke-tested against a live MySQL container).

### Database configuration
* Database `taskdb`, tables created **without** EF migrations — see next point.
* Credentials (dev/compose): root/root for the MySQL container, `taskuser`/`taskpass` for the app.
* Connection string is consumed from configuration: `ConnectionStrings:DefaultConnection`.

### Database migrations
* **No EF migration files are used.** Two-layer approach instead:
  1. `app/db/init.sql` creates the schema when MySQL initialises (idempotent `CREATE TABLE IF NOT EXISTS`);
  2. at boot the app calls `EnsureCreated()` and verifies `Tasks` exists in
     `information_schema`, creating it if the database pre-existed without a schema.
* **Assumption:** an exam-scale app with a single table doesn't need migration history;
  for production, `dotnet ef migrations add` would replace both steps.

### Environment variables
| Variable | Set where | Value / purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | compose (`api`) | `Server=db;Port=3306;Database=taskdb;User=taskuser;Password=taskpass;...` — overrides `appsettings.json` |
| `ASPNETCORE_ENVIRONMENT` | compose (`api`) | `Production` (Swagger stays on, see note) |
| `ASPNETCORE_URLS` | Dockerfile | `http://+:8080` — Kestrel bind address |
| `MYSQL_ROOT_PASSWORD` | compose (`db`) | `root` — required by the official MySQL image |

* **Assumption:** Swagger UI is left enabled in Production for exam/demo convenience; in a
  real deployment it would be gated behind `if (app.Environment.IsDevelopment())`.
* Local (non-Docker) runs fall back to `appsettings.json` (`localhost:3306`, root/root).

## 6. Verification performed

```
docker compose up --build        # db healthy → api up
GET    /health                   → 200 {"status":"Healthy",...}
POST   /api/tasks                → 201, task persisted
GET    /api/tasks  /{id}         → 200 list / 200 one / 404 missing
PUT    /api/tasks/1              → 200 updated (verified in MySQL)
DELETE /api/tasks/2              → 204, row removed (verified in MySQL)
POST   invalid (no title)        → 400
GET    /swagger/index.html       → 200

# named volume persistence
docker compose down && docker compose up -d   → row still present after full teardown
docker network inspect taskmanager-network    → api + db both attached
docker volume inspect taskmanager-mysql-data  → named volume present
```

Containers and the test volume were removed afterwards (`docker compose down -v`),
so the project starts from a clean database.
