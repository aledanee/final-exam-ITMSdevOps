# Task Manager API

Simple Task Manager API built with **ASP.NET Core 8** (minimal APIs) and **MySQL 8**.
All application code lives in a single file: `backend/Program.cs`.

## Structure

```
app/
├── backend/          ASP.NET Core API (single-file app)
│   ├── Program.cs    Models, DbContext, DTOs and all endpoints
│   ├── TaskManager.csproj
│   ├── appsettings.json
│   └── Dockerfile
├── db/
│   └── init.sql      Database + schema bootstrap (runs on first MySQL start)
└── docker-compose.yaml
```

## Run with Docker (recommended)

```bash
cd app
docker compose up --build
```

- API + Swagger UI: http://localhost:8080/swagger
- Health check: http://localhost:8080/health
- MySQL: `localhost:3306`, db `taskdb`, user `taskuser` / `taskpass`

## Run locally (requires .NET 8 SDK + MySQL)

```bash
# start only MySQL
cd app && docker compose up -d db

cd backend
dotnet run
```

## Endpoints

| Method | Path              | Description       |
|--------|-------------------|-------------------|
| GET    | `/health`         | Health check      |
| GET    | `/api/tasks`      | List all tasks    |
| GET    | `/api/tasks/{id}` | Get one task      |
| POST   | `/api/tasks`      | Create a task     |
| PUT    | `/api/tasks/{id}` | Update a task     |
| DELETE | `/api/tasks/{id}` | Delete a task     |

### Examples

```bash
curl -X POST http://localhost:8080/api/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"Write report","description":"Quarterly summary","isCompleted":false}'

curl http://localhost:8080/api/tasks
curl http://localhost:8080/api/tasks/1

curl -X PUT http://localhost:8080/api/tasks/1 \
  -H "Content-Type: application/json" \
  -d '{"title":"Write report","description":"Quarterly summary","isCompleted":true}'

curl -X DELETE http://localhost:8080/api/tasks/1
curl http://localhost:8080/health
```

### Task shape

```json
{
  "id": 1,
  "title": "Write report",
  "description": "Quarterly summary",
  "isCompleted": false,
  "createdAt": "2026-11-09T12:00:00Z"
}
```
