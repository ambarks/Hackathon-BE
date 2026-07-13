# Code Setup

## 1. Purpose of this document

CODESETUP.md is for developers who want to run, debug, modify, and extend this POC locally. For a quick start and end-user-facing instructions, see [README.md](README.md). For scripted demo walkthroughs, see [DEMO.md](DEMO.md).

## 2. Prerequisites

Required:
- Docker Desktop or Rancher Desktop
- Docker Compose v2
- Git
- Claude / Anthropic API key (optional — the app runs in fallback demo mode without one)

Recommended:
- Visual Studio Code / Cursor / Rider

Optional (only needed if you want to run a service outside Docker):
- .NET 8 SDK
- Node.js 20 LTS
- Python 3.11
- SQL Server Management Studio / Azure Data Studio
- Postman / Bruno
- jq

## 3. Verify local tools

```
docker --version
docker compose version
git --version
dotnet --version
node --version
python3 --version
```

`dotnet`, `node`, and `python3` are optional — you only need them if you intend to run a service outside Docker Compose (see section 9). Docker Compose remains the recommended way to run the whole stack.

## 4. SQL Server Docker setup

- SQL Server 2022 runs inside Docker as the `sqlserver` service — no local SQL Server/SQLEXPRESS install is required.
- The `api` container always connects using the Docker service name: `sqlserver,1433`.
- SSMS/Azure Data Studio on the host can connect to `localhost,1433` if that port is free.
- If port 1433 is already used on your machine, remap it in `docker-compose.yml`: `"11433:1433"`. The `api` container is unaffected (it still uses `sqlserver,1433` internally); connect SSMS to `localhost,11433` instead.

## 5. Project structure

```
clinical-trial-prescreening-assistant/  (repo root)
  docker-compose.yml       - defines web, api, embedding-service, qdrant, sqlserver
  .env.example             - copy to .env and fill in ANTHROPIC_API_KEY
  README.md                - quick start and end-user instructions
  DEMO.md                  - scripted demo walkthroughs
  CODESETUP.md             - this file
  smoke-test.sh            - scripted health/endpoint checks
  sample-data/             - sample protocol, criteria, question bank, patient scenarios
  backend/ClinicalTrialPreScreening.Api/
    Controllers/           - Protocols, Criteria, ScreeningSessions, Health, AiHealth
    Services/               - Claude, Qdrant, Embedding, criteria/question-bank/screening logic, audit, export
    Models/                 - EF Core entities (8 tables)
    Data/                   - AppDbContext, DbInitializer
    Utils/                  - shared JSON/sample-data helpers
  frontend/
    src/api/                - typed API client + React context (AI status, workflow state)
    src/components/         - NavBar, Layout, banners
    src/pages/               - Dashboard, Upload & Criteria, Question Bank, Screening Session, Summary
  embedding-service/
    app/main.py             - FastAPI app (sentence-transformers all-MiniLM-L6-v2)
```

## 6. Environment configuration

```
cp .env.example .env
```

Key variables:
- `ANTHROPIC_API_KEY` — your Claude API key. **Never commit this to source control.** Leave as the placeholder value to run in fallback mode.
- `ANTHROPIC_MODEL` — Claude model name (default `claude-3-5-sonnet-latest`).
- `VITE_API_BASE_URL` — frontend's API base URL (default `http://localhost:8080`).
- `QDRANT_URL`, `EMBEDDING_SERVICE_URL` — internal Docker network URLs; leave as default unless you changed service names.
- `SQLSERVER_HOST`, `SQLSERVER_PORT`, `SQLSERVER_DATABASE`, `SQLSERVER_USER`, `SQLSERVER_PASSWORD` — used to build the SQL Server connection string.
- `CONNECTION_STRING` — optional full override of the SQL Server connection string; if set, it takes precedence over the individual `SQLSERVER_*` variables.
- `USE_FALLBACK_DEMO_MODE` — reserved flag for forcing fallback behavior; currently the app determines fallback mode automatically per dependency (Claude/Qdrant/embedding-service), so this flag has no separate wiring today.

## 7. First-time setup and run

```
docker compose up --build
```

Expected first-run behavior:
- Docker images are built for `web`, `api`, and `embedding-service`.
- The `sqlserver` container starts and initializes its data directory (can take 15–60 seconds).
- The `embedding-service` container downloads the `sentence-transformers/all-MiniLM-L6-v2` model on first start (can take a few minutes on a slow connection).
- The backend connects to SQL Server, retrying internally if it isn't ready yet, and creates the database and schema automatically.
- Qdrant starts and is ready quickly (a persistent volume stores its data).
- The frontend starts on port 5173.

## 8. Application URLs

- Frontend: http://localhost:5173
- Backend Swagger: http://localhost:8080/swagger
- Backend health: http://localhost:8080/api/health
- Backend database health: http://localhost:8080/api/health/database
- Backend AI health: http://localhost:8080/api/health/ai
- Qdrant: http://localhost:6333
- Embedding service health: http://localhost:8001/health

## 9. Local development outside Docker

Docker Compose remains the recommended way to run this POC. If you want to iterate on one service directly:

- **Backend**: `cd backend/ClinicalTrialPreScreening.Api && dotnet run` — requires `sqlserver`, `qdrant`, and `embedding-service` reachable (e.g. still running via `docker compose up sqlserver qdrant embedding-service`), and the `SQLSERVER_*`/`QDRANT_URL`/`EMBEDDING_SERVICE_URL` environment variables set to point at `localhost` instead of the Docker service names.
- **Frontend**: `cd frontend && npm install && npm run dev` — set `VITE_API_BASE_URL` to wherever the API is running.
- **Embedding service**: `cd embedding-service && pip install -r requirements.txt && uvicorn app.main:app --port 8001`.

## 10. Database initialization

- `AppDbContext` is at `backend/ClinicalTrialPreScreening.Api/Data/AppDbContext.cs`.
- `DbInitializer.EnsureSchemaAsync` (same folder) calls `db.Database.EnsureCreatedAsync()`, which creates the database and all tables automatically if they don't already exist. It does **not** apply incremental schema changes to an existing database.
- `Services/DatabaseInitializationService.cs` is a hosted service that retries `EnsureSchemaAsync` (10 attempts, 5 seconds apart) at startup, so the API tolerates SQL Server's Docker startup delay instead of failing permanently.
- Reset the database: `docker compose down -v` (also removes Qdrant and upload/export volumes), then `docker compose up --build`. **Do this after pulling any backend change that adds or renames a model property** — `EnsureCreatedAsync` will not add the new column to an existing database, and queries against the new column will fail with a SQL "Invalid column name" error until you reset.
- Inspect data with SSMS or Azure Data Studio (see section 4/17 for connection details).
- Troubleshooting "Invalid object name 'Protocols'": this should not occur since schema creation runs before any query; if you do see it, it likely means the API started before `EnsureSchemaAsync` completed on a very first run — wait a few seconds and retry, or check `docker compose logs api`.
- Do not point this application at a production or otherwise sensitive database.

## 11. Working with sample data

- Sample protocol: `sample-data/sample-protocol.txt` (synthetic Type 2 Diabetes trial).
- Sample criteria fallback: `sample-data/sample-criteria.json` — used automatically by `CriteriaExtractionService` whenever Claude is unavailable or returns an unparseable response.
- Sample question bank reference: `sample-data/sample-question-bank.json` — a fully worked example of the two-section, de-duplicated contract; the live fallback question bank is generated dynamically by `QuestionBankService`'s deterministic sequencing algorithm rather than reading this file directly.
- Sample patient scenarios: `sample-data/sample-patient-scenarios.json` — three scripted answer sets (Likely Eligible, Likely Ineligible, Needs Clinical Review) matching the criteria IDs in `sample-criteria.json`. See [DEMO.md](DEMO.md) for the live walkthrough.
- Use the sample protocol from the UI via **Upload & Criteria → Use Sample Protocol**, or via `POST /api/protocols/use-sample`.

## 12. Question section and sequencing design

- The app has exactly two sections everywhere: **Demographic Information** and **Eligibility Criteria Questions**.
- Inclusion and exclusion criteria are never separated into different UI sections — they are mixed within Eligibility Criteria Questions.
- Demographic answers can resolve (cover) one or more inclusion/exclusion criteria; a covered criterion is never asked again as a separate criteria question (enforced by `QuestionBankService`'s de-duplication pass, which runs regardless of whether Claude or the deterministic fallback produced the question bank).
- Criteria questions are ordered by priority (High → Low), with exclusion criteria placed ahead of inclusion criteria within the same priority tier, so high-impact disqualifying questions tend to surface early.

## 13. Running the demo flow

See [DEMO.md](DEMO.md) for the full scripted walkthrough: start app → open frontend → load sample protocol → extract criteria → approve criteria → generate question bank → review sections → start screening session → answer demographic questions → answer criteria questions → generate summary → export.

## 14. Troubleshooting

| Issue | What to check / do |
|---|---|
| Docker not running | Start Docker Desktop / Rancher Desktop and wait for it to report "running." |
| `docker compose` command not found | Update to a Docker version that bundles Compose v2, or install the `docker-compose-plugin`. |
| Port 5173/8080/1433/6333 already in use | Remap the host-side port in `docker-compose.yml` (left side of `"host:container"`). |
| SQL Server container startup delay | Normal on first run; the API retries internally. Check `docker compose logs sqlserver`. |
| SQL Server login failure | Confirm `SQLSERVER_PASSWORD` in `.env` matches what the `sqlserver` container was started with — if you changed it after the volume was created, run `docker compose down -v` first. |
| "Failed to open explicitly specified database" | Usually a transient timing issue during first-run schema creation; wait and retry. |
| "Invalid object name 'Protocols'" | Should not occur; see section 10. |
| SQL certificate / encryption issue | Confirm `SQLSERVER_TRUST_CERTIFICATE=true` and `SQLSERVER_ENCRYPT=false` in `.env` for local Docker use. |
| Claude API key missing | Expected fallback path — banner and `/api/health/ai` report it; not an error. |
| Claude API insufficient credits | Expected fallback path — see the exact banner text in README.md section 14. |
| Claude API response missing content | Handled defensively by `ClaudeService`; falls back automatically and logs the issue server-side. |
| Corporate proxy issue | See section 15 below. |
| Qdrant startup issue | Protocol upload still succeeds; vector storage for that upload is skipped (a warning is logged). |
| Embedding model download delay | First run only; the embedding-service health check tolerates several minutes for this. |
| PDF parsing failure | Falls back to `sample-data/sample-protocol.txt` automatically. |
| Frontend cannot reach backend API | Confirm `VITE_API_BASE_URL` in `.env` matches where the API is actually reachable from your browser (default `http://localhost:8080`). |
| JSON object cycle error | Should not occur; see section 12 of README.md / Architecture Rules in CLAUDE.md. |

## 15. Corporate proxy setup

If your network requires a proxy for outbound Claude API calls or package downloads during image builds:

```
HTTP_PROXY=
HTTPS_PROXY=
NO_PROXY=localhost,127.0.0.1,sqlserver,qdrant,embedding-service,api,web
```

Set these in your shell environment before running `docker compose up --build` (Docker Desktop also has its own proxy settings under Settings → Resources → Proxies for image pulls).

## 16. Useful commands

```
docker compose up --build
docker compose up
docker compose down
docker compose down -v
docker compose logs -f api
docker compose logs -f web
docker compose logs -f embedding-service
docker compose logs -f qdrant
docker compose logs -f sqlserver
docker compose restart api
docker ps
docker system prune
```

## 17. Connecting to SQL Server from SSMS

- If host port 1433 is mapped: Server name `localhost,1433`
- If host port 11433 is mapped: Server name `localhost,11433`
- Authentication: SQL Server Authentication
- Login: `sa`
- Password: value of `SQLSERVER_PASSWORD` in `.env`
- Enable **Trust Server Certificate** if prompted

## 18. Developer notes

- The frontend must not call Claude API directly — it only talks to the .NET backend.
- The Claude API key stays in the backend only; it is never sent to the browser.
- Controllers do not directly call Claude, Qdrant, or the embedding service, or execute raw SQL — they call the dedicated services below.
- `ClaudeService` handles all Claude API calls, including defensive response parsing.
- `AiStatusService` tracks Claude configuration/fallback status, exposed via `GET /api/health/ai`.
- `QdrantService` handles all Qdrant vector DB calls.
- `EmbeddingClient` handles all embedding-service calls.
- `AdaptiveQuestionService` computes next-question / section-progress / overall-status logic deterministically from stored questions, criteria, and answers.
- `ScreeningSessionService` owns session lifecycle, answer recording, and final summary generation.
- `AuditService` records the audit trail (protocol upload, criteria extraction/approval, question bank generation, de-duplication, screening answers, summary generation) into `AuditEvents`.

## 19. Known hackathon simplifications

- Single sample protocol and three synthetic patient scenarios; no real patient data.
- SQL Server runs in Docker instead of an enterprise-managed database.
- Qdrant runs locally in Docker; no cloud-hosted vector database.
- No authentication or multi-user support.
- Simplified, heuristic PDF parsing and section-chunking (paragraph/heading based, not a full document-structure parser).
- Limited clinical validation — mapped eligibility status uses a first-word yes/no heuristic plus a `requiresClinicalReview` downgrade rule, not real clinical logic.
- Rule-based/deterministic fallback logic is used whenever Claude is unavailable, for criteria extraction, question bank generation, summary narration, and answer evaluation.
- The early-stop feature's AI-assisted answer evaluation (`AnswerEvaluationService`) only calls Claude for questions where `canTriggerEarlyStop` is true (linked to an Exclusion or Required-Inclusion criterion) — every other answer keeps the original yes/no heuristic. Claude never decides whether to stop; it only classifies the answer, and a fixed backend rule in `AdaptiveQuestionService`/`ScreeningSessionService` decides the recommendation. In fallback mode, evaluation degrades to the same yes/no heuristic, which cannot resolve numeric-range criteria (e.g. an age outside a required range) — a known, documented limitation rather than a crash.
- `EnsureCreatedAsync` is used for schema creation instead of production-grade, versioned EF Core migrations.

## 20. Safety and Responsible AI reminder

This application is a hackathon proof of concept. It provides AI-assisted pre-screening guidance only. It must not be used for real patient recruitment, clinical decision-making, diagnosis, treatment, or final eligibility determination. Final eligibility must be confirmed by qualified clinical staff.
