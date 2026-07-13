# Implementation Plan

Reference: `prompt.txt` (full requirements, not repeated here) and `CLAUDE.md` (standing rules). This plan sequences implementation into 13 phases (0–12), each independently verifiable.

## 1. Goal
Build a working local Docker Compose POC where a recruiter uploads/loads a clinical trial protocol, gets AI-extracted eligibility criteria, generates a two-section (Demographics + Criteria) question bank with mixed optimized ordering and de-duplication, runs an adaptive one-at-a-time screening session, and receives a final Likely Eligible / Likely Ineligible / Needs Clinical Review recommendation with full reasoning — all degrading gracefully to deterministic fallback when Claude is unavailable.

## 2. Scope
**In scope**
- Docker Compose stack: web, api, embedding-service, qdrant, sqlserver.
- Full protocol upload → criteria → question bank → screening → summary/export flow.
- Claude integration with defensive parsing and fallback mode.
- SQL Server persistence via EF Core with `EnsureCreatedAsync` schema init.
- Sample data-driven fallback for every AI-dependent step.

**Out of scope**
- Authentication/authorization, multi-user support, real patient data.
- Production-grade EF migrations, HA/scaling, cloud deployment, Kubernetes.
- Advanced UI styling/design system — functional over polished.
- Any final medical/diagnostic/recruitment decisioning by the app itself.

**Hackathon simplifications** (must be documented in README/CODESETUP)
- `EnsureCreatedAsync` instead of versioned migrations.
- Single sample protocol + 3 synthetic patient scenarios.
- Rule-based/deterministic fallback in place of Claude when unavailable.
- No auth; local Docker-only networking; CPU-only embeddings.

## 3. Architecture Summary
- **Frontend (React+Vite, :5173)**: workflow dashboard, upload, criteria review, question bank preview, screening session, summary/export, status banners. Calls only the .NET API; never calls Claude/Qdrant/embedding-service directly.
- **Backend (.NET 8 Web API, :8080)**: Controllers → Services → Data/External Clients layering. Owns all PDF parsing (PdfPig), chunking, Claude orchestration, Qdrant/embedding calls, screening state machine, audit trail, and exports. Exposes Swagger.
- **Claude API**: called only from `ClaudeService`; used for criteria extraction, question generation/sequencing suggestions, explanations, and summarization — never the sole decision engine. Defensive parsing required (see [[CLAUDE.md]] rules).
- **Embedding service (Python FastAPI, :8001)**: stateless `/embed` using `all-MiniLM-L6-v2`, CPU-only, `/health`.
- **Qdrant (:6333/6334)**: stores protocol chunk vectors + metadata for retrieval-augmented explanation/question context; falls back to SQL Server keyword search if unavailable.
- **SQL Server 2022 (Docker, :1433)**: system of record for protocols, chunks, criteria, questions, sessions, answers, summaries, audit events. Schema auto-created via `EnsureCreatedAsync` (optionally migrations-first with safe fallback).
- **Local storage**: Docker volumes (`sqlserver-data`, qdrant volume) + mounted folder for uploads/exports.
- **Fallback behavior**: any external dependency failure (Claude, Qdrant, embedding service, PDF parsing) degrades to sample-data/deterministic logic and surfaces a clear status banner or error — the app never crashes.

## 4. Key Design Decisions
- **Two-section flow only**: Demographics + Criteria, everywhere (data model, API, UI, sample data, prompts). No Inclusion/Exclusion UI split.
- **Mixed criteria sequencing**: Criteria section order blends inclusion/exclusion by screening value (early disqualifiers, essential inclusions, easy answers) — never grouped by type.
- **Demographic de-duplication**: demographic answers can satisfy linked criteria; those criteria are suppressed from the Criteria section but still reported as "covered by demographics" in the summary. One question → many criteria supported via `linkedCriteria`/`coveredCriteria`.
- **Human review required**: every recommendation carries a disclaimer and is framed as guidance; UI never claims a final decision.
- **Claude fallback**: `ClaudeService` never throws; returns null on any parse/HTTP failure; `AiStatusService` tracks fallback state; UI banner reflects it; all AI-dependent endpoints have a deterministic/sample-data fallback path.
- **SQL schema initialization**: `DbInitializer.EnsureSchemaAsync` using `EnsureCreatedAsync` (migrations-first-then-fallback optional) so first run never hits `Invalid object name` errors; startup retries tolerate delayed SQL Server/Qdrant/embedding-service readiness.
- **Defensive Claude parsing**: `TryGetProperty` everywhere, explicit check for Anthropic error-shaped JSON and insufficient-credit messages, no `KeyNotFoundException` possible.
- **Avoiding EF JSON cycles**: prefer DTOs from controllers; `ReferenceHandler.IgnoreCycles` as a global backstop only.

## 5. Implementation Phases

### Phase 0: Repo validation and baseline setup

#### Objective
Establish the root folder/file skeleton and baseline tooling so later phases have a consistent structure to build into.

#### Tasks
- Create root folder structure per `prompt.txt` (backend/, frontend/, embedding-service/, sample-data/, docker-compose.yml placeholder, .env.example, README.md, DEMO.md, CODESETUP.md, smoke-test.sh stubs).
- Add `.gitignore` covering .NET, Node, Python, and env files.
- Confirm `plan.md` and `CLAUDE.md` are present and accurate (no code yet).

#### Files / Areas Likely Touched
Root folders/placeholders only; no application code.

#### Verification Steps
- `Get-ChildItem -Recurse` shows the expected folder tree.
- No stack-specific files (csproj, package.json, requirements.txt) contain real logic yet.

#### Expected Result
Empty-but-structured repo skeleton matching the required folder layout, ready for stack-specific scaffolding.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 0 only: repo validation and baseline setup.

Scope for this phase:
- Create the root folder/file skeleton (backend/, frontend/, embedding-service/, sample-data/, docker-compose.yml, .env.example, README.md, DEMO.md, CODESETUP.md, smoke-test.sh) as placeholders per prompt.txt's folder structure.
- Add a .gitignore for .NET/Node/Python/env files.
- Do not add real implementation code yet.

Do not implement future phases yet.
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps.
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 1: Docker Compose and environment setup

#### Objective
Stand up the full Docker Compose stack skeleton (web, api, embedding-service, qdrant, sqlserver) so all services can start together even before business logic exists.

#### Tasks
- Write `docker-compose.yml` with all 5 services, ports, health checks, `depends_on` with health conditions, volumes (`sqlserver-data`, qdrant volume), and shared network.
- Write `.env.example` with all variables from `prompt.txt` (Claude, ports, Qdrant/embedding URLs, SQL Server vars, connection string override, upload/export dirs, fallback flag).
- Add minimal Dockerfiles for api, web, embedding-service (can build trivial placeholder apps at this stage).
- Document port-conflict alternative (11433) inline as comments.

#### Files / Areas Likely Touched
`docker-compose.yml`, `.env.example`, `backend/.../Dockerfile`, `frontend/Dockerfile`, `embedding-service/Dockerfile`.

#### Verification Steps
- `docker compose config` validates without errors.
- `docker compose up --build` starts all 5 containers (placeholder apps OK) without crash-looping.
- `docker compose down` cleans up; `docker compose down -v` removes volumes.

#### Expected Result
Full stack boots via one command; ports 5173/8080/8001/6333/6334/1433 are reachable/bound.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 1 only: Docker Compose and environment setup.

Scope for this phase:
- Write docker-compose.yml with web, api, embedding-service, qdrant, sqlserver services, correct ports, health checks, depends_on, and volumes per prompt.txt.
- Write .env.example with all required variables.
- Add minimal/placeholder Dockerfiles for api, web, embedding-service so the stack builds and starts.

Do not implement future phases yet (no business logic).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (docker compose config, docker compose up --build).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 2: Backend foundation and database schema

#### Objective
Scaffold the .NET 8 Web API project with EF Core, models, `AppDbContext`, and `DbInitializer` so the database and schema are created automatically on startup.

#### Tasks
- Create `ClinicalTrialPreScreening.Api` project with required NuGet packages (`Microsoft.EntityFrameworkCore.SqlServer`, `.Design`, `Swashbuckle.AspNetCore`, PdfPig `1.7.0-custom-5`).
- Add models: Protocol, ProtocolChunk, EligibilityCriterion, ScreeningQuestion, ScreeningSession, ScreeningAnswer, ScreeningSummary, AuditEvent.
- Add `AppDbContext` with DbSets for all 8 tables; add `DbInitializer.EnsureSchemaAsync` using `EnsureCreatedAsync` per the preferred implementation in `prompt.txt`.
- Wire connection string resolution: `CONNECTION_STRING` override else build from `SQLSERVER_*` env vars.
- Add `HealthController` (`/api/health`, `/api/health/database`) and call `EnsureSchemaAsync` on startup with retry tolerance for delayed SQL Server readiness.
- Add global JSON cycle handling (`ReferenceHandler.IgnoreCycles`) in `Program.cs` as a backstop.

#### Files / Areas Likely Touched
`backend/ClinicalTrialPreScreening.Api/{Program.cs, appsettings.json, Models/*, Data/AppDbContext.cs, Data/DbInitializer.cs, Controllers/HealthController.cs}`, `.csproj`.

#### Verification Steps
- `dotnet build` succeeds (or `docker compose build api`).
- `docker compose up --build` → `GET /api/health` returns 200.
- `GET /api/health/database` returns healthy after SQL Server is ready; app does not fail with `Invalid object name 'Protocols'`.
- Connect via SSMS to confirm all 8 tables exist.

#### Expected Result
Backend starts, connects to SQL Server in Docker, and auto-creates all required tables with no manual scripts.

#### Claude Implementation Prompt
```text
Read prompt.txt. Implement Phase 2 only: backend foundation and database schema.

Scope for this phase:
- Scaffold the .NET 8 Web API project with required NuGet packages including UglyToad.PdfPig 1.7.0-custom-5.
- Add the 8 required models, AppDbContext, and DbInitializer using EnsureCreatedAsync per prompt.txt.
- Wire SQLSERVER_* env vars / CONNECTION_STRING override into the connection string.
- Add HealthController (/api/health, /api/health/database) and startup retry tolerance for delayed SQL Server.
- Add global JSON ReferenceHandler.IgnoreCycles in Program.cs.

Do not implement future phases yet (no PDF/Claude/screening logic).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (docker compose up --build, curl /api/health, /api/health/database).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 3: Sample data and protocol upload

#### Objective
Implement protocol upload/sample-load with PdfPig text extraction, chunking, and SQL Server persistence, backed by sample data files.

#### Tasks
- Add `sample-data/sample-protocol.txt`, `sample-patient-scenarios.json` (3 scenarios), placeholders for `sample-criteria.json`/`sample-question-bank.json` (filled fully in later phases).
- Implement `ProtocolTextExtractionService` (PdfPig) with fallback to sample protocol text on parse failure.
- Implement `ProtocolChunkingService` to split protocol text into sections/chunks.
- Implement `ProtocolsController`: `POST /api/protocols/upload`, `POST /api/protocols/use-sample`, `GET /api/protocols`, `GET /api/protocols/{protocolId}`.
- Persist Protocol + ProtocolChunk rows in SQL Server.

#### Files / Areas Likely Touched
`sample-data/*`, `backend/.../Services/ProtocolTextExtractionService.cs`, `ProtocolChunkingService.cs`, `Controllers/ProtocolsController.cs`, `Models/Protocol.cs`, `ProtocolChunk.cs`.

#### Verification Steps
- `POST /api/protocols/use-sample` returns a protocolId; `GET /api/protocols/{id}` returns stored text/chunks.
- Upload a real PDF (or simulate failure) to confirm fallback to sample text works without crashing.
- Confirm rows appear in `Protocols`/`ProtocolChunks` tables.

#### Expected Result
Recruiter can upload a PDF or use the sample protocol; text is extracted, chunked, and stored reliably with PDF-failure fallback.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 3 only: sample data and protocol upload.

Scope for this phase:
- Add sample-data/sample-protocol.txt and sample-patient-scenarios.json (3 scenarios: eligible, ineligible, needs review).
- Implement ProtocolTextExtractionService (PdfPig) with fallback to sample-protocol.txt on failure.
- Implement ProtocolChunkingService and ProtocolsController endpoints: upload, use-sample, list, get-by-id.
- Persist Protocol and ProtocolChunk records in SQL Server.

Do not implement future phases yet (no criteria extraction, Claude, or embeddings).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (use-sample endpoint, get protocol, check DB rows).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 4: Criteria extraction and fallback mode

#### Objective
Extract structured eligibility criteria from protocol text via Claude, with full fallback to sample criteria and defensive error handling — no crashes on Claude failure.

#### Tasks
- Implement `ClaudeService.SendAsync` with defensive parsing (status check, `TryGetProperty`, error-shape/insufficient-credit detection, return null on failure) and required guardrail text in every prompt.
- Implement `AiStatusService` (singleton) tracking Claude config/fallback state; register in DI.
- Implement `CriteriaExtractionService`: builds criteria-extraction prompt from chunks, calls `ClaudeService`, falls back to `sample-data/sample-criteria.json` on any failure.
- Implement `CriteriaController`: `POST /api/protocols/{protocolId}/extract-criteria`, `GET /api/protocols/{protocolId}/criteria`, `PUT /api/criteria/{criterionId}`, `POST /api/protocols/{protocolId}/criteria/approve`.
- Add `GET /api/health/ai` returning AiStatusService state.
- Fill out full `sample-data/sample-criteria.json` matching the schema in `prompt.txt`.

#### Files / Areas Likely Touched
`Services/ClaudeService.cs`, `AiStatusService.cs`, `CriteriaExtractionService.cs`, `Controllers/CriteriaController.cs`, `Controllers/AiHealthController.cs` (or health controller extension), `Models/EligibilityCriterion.cs`, `sample-data/sample-criteria.json`.

#### Verification Steps
- With no/invalid `ANTHROPIC_API_KEY`: extraction still returns sample criteria; `/api/health/ai` shows `runningInFallbackMode: true`.
- With a valid key: extraction returns Claude-generated JSON matching the criteria schema.
- Simulate Claude error JSON (insufficient credits) → confirm no `KeyNotFoundException`, correct log message emitted.
- Approve/edit a criterion via `PUT`/`approve` and confirm persistence.

#### Expected Result
Criteria extraction works end-to-end in both live-Claude and fallback modes without ever crashing the API.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 4 only: criteria extraction and fallback mode.

Scope for this phase:
- Implement ClaudeService.SendAsync with defensive parsing (TryGetProperty, error/insufficient-credit detection, return null, never throw KeyNotFoundException) and required prompt guardrails.
- Implement AiStatusService (singleton) and GET /api/health/ai.
- Implement CriteriaExtractionService with fallback to sample-data/sample-criteria.json.
- Implement CriteriaController endpoints (extract, list, update, approve).
- Fill out sample-data/sample-criteria.json per the schema in prompt.txt.

Do not implement future phases yet (no question bank generation or screening).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (extract with/without Claude key, check /api/health/ai, approve a criterion).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 5: Question bank generation with two-section logic

#### Objective
Generate the two-section (Demographics + Criteria) question bank with mixed optimized sequencing and demographic de-duplication, backed by Claude with deterministic fallback.

#### Tasks
- Implement `QuestionBankService`: builds the question-generation prompt (exact rules/JSON structure from `prompt.txt`), calls `ClaudeService`, applies deterministic fallback sequencing (demographics first, then priority-mixed criteria) if Claude fails or returns invalid JSON.
- Enforce de-duplication: suppress criteria covered by demographic questions; populate `linkedCriteria`, `coveredCriteria`, `suppressedDuplicateCriteria`.
- Add `POST /api/protocols/{protocolId}/generate-question-bank`, `GET /api/protocols/{protocolId}/questions`, `GET /api/protocols/{protocolId}/questions/sections` (grouped `{demographics: [], criteria: []}`).
- Persist `ScreeningQuestion` rows with all required fields.
- Fill out full `sample-data/sample-question-bank.json` (two sections, an age-covers-criterion example, suppressed duplicates).

#### Files / Areas Likely Touched
`Services/QuestionBankService.cs`, `Controllers/CriteriaController.cs` (generate endpoint) or new endpoint location, `Models/ScreeningQuestion.cs`, `sample-data/sample-question-bank.json`.

#### Verification Steps
- `POST generate-question-bank` then `GET .../questions/sections` returns exactly two sections with no Inclusion/Exclusion split.
- Confirm an age (or similar) demographic question suppresses its linked criterion from the Criteria section.
- Force Claude fallback and confirm `sample-question-bank.json` shape still yields exactly two sections and de-dup example.

#### Expected Result
Question bank always has exactly two sections, mixed-ordered criteria, and correct de-duplication in both live and fallback modes.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 5 only: question bank generation with two-section logic.

Scope for this phase:
- Implement QuestionBankService using the exact Claude prompt/JSON contract from prompt.txt (Demographics + Criteria sections, linkedCriteria, coveredCriteria, suppressedDuplicateCriteria, sequencingRationale).
- Implement deterministic fallback sequencing if Claude fails/returns invalid JSON.
- Add generate-question-bank, questions, and questions/sections endpoints.
- Fill out sample-data/sample-question-bank.json with a demographic-covers-criterion example and suppressed duplicates.

Do not implement future phases yet (no screening session logic).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (generate bank, fetch sections, confirm two sections and de-dup).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 6: Adaptive screening session flow

#### Objective
Implement the recruiter-facing screening session with deterministic, backend-controlled adaptive next-question logic and full state tracking.

#### Tasks
- Implement `ScreeningSessionService`: create session (patient alias only), track per-criterion status (satisfied/failed/needs-review/unanswered/covered-by-demographics/skipped-dedup/skipped-adaptive), current section, section progress, overall likely status.
- Implement `AdaptiveQuestionService`: enforces demographics-first-then-mixed-criteria order, applies demographic answers to linked criteria, skips suppressed/irrelevant questions, flags early-stop candidates.
- Implement `ScreeningSessionsController`: `POST /api/screening-sessions`, `GET /{sessionId}`, `GET /{sessionId}/next-question`, `GET /{sessionId}/current-section`, `GET /{sessionId}/section-progress`, `POST /{sessionId}/answers`.
- Persist `ScreeningSession`/`ScreeningAnswer` rows with all required fields (per `prompt.txt` section E).

#### Files / Areas Likely Touched
`Services/ScreeningSessionService.cs`, `AdaptiveQuestionService.cs`, `Controllers/ScreeningSessionsController.cs`, `Models/ScreeningSession.cs`, `ScreeningAnswer.cs`.

#### Verification Steps
- Start a session; confirm `next-question` returns demographics first, then mixed criteria.
- Answer a demographic question linked to a criterion; confirm that criterion is auto-marked and its dedicated question is skipped.
- Answer to fail a high-priority exclusion criterion; confirm likely-ineligible flag/early-stop surfaces without ending the API's ability to continue if recruiter chooses.
- Confirm `section-progress` reflects correct counts per section.

#### Expected Result
A recruiter can run a full one-at-a-time adaptive session with correct sequencing, de-duplication, and state tracking, entirely backend-controlled.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 6 only: adaptive screening session flow.

Scope for this phase:
- Implement ScreeningSessionService (session lifecycle, criterion status tracking, section progress, overall likely status).
- Implement AdaptiveQuestionService (demographics-first-then-mixed-criteria logic, demographic-answer propagation to linked criteria, early-stop flagging) per the deterministic rules in prompt.txt section F.
- Implement ScreeningSessionsController endpoints: create session, get session, next-question, current-section, section-progress, answers.
- Persist ScreeningSession/ScreeningAnswer with all required fields.

Do not implement future phases yet (no Claude explanation calls, embeddings, or summary generation).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (start session, answer demographic question, confirm linked criterion auto-resolves, check section-progress).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 7: Claude integration and AI status tracking

#### Objective
Wire Claude into explanation ("why is this asked") and sequencing-rationale generation for the live session, plus verify AI status/banner plumbing end-to-end.

#### Tasks
- Extend `ClaudeService`/relevant services to generate per-question `whyAsked` explanations referencing protocol criterion text, with rule-based fallback explanation if Claude fails.
- Ensure `AiStatusService` is updated on every Claude call site (extraction, question bank, explanation, summary) and `LastCheckedAt`/`LastStatusMessage` stay current.
- Confirm `/api/health/ai` reflects real-time state across all call sites.
- Add prompt-version/model-name tagging to AI-generated outputs where stored.

#### Files / Areas Likely Touched
`Services/ClaudeService.cs`, `AiStatusService.cs`, `AdaptiveQuestionService.cs` or `QuestionBankService.cs` (whyAsked wiring), `Prompts/` templates.

#### Verification Steps
- Toggle `ANTHROPIC_API_KEY` missing/invalid/valid and confirm `/api/health/ai` and each affected endpoint respond correctly without crashing.
- Confirm `whyAsked` text appears for both demographic and criteria questions, including criterion mapping for demographic questions covering criteria.

#### Expected Result
Claude explanation/status integration is consistent and observable across the whole flow, with fallback always safe.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 7 only: Claude integration and AI status tracking.

Scope for this phase:
- Wire Claude-generated whyAsked explanations into question/session flow with rule-based fallback on failure.
- Ensure AiStatusService updates consistently across extraction, question bank, explanation, and summary call sites.
- Add prompt-version/model-name tagging to stored AI outputs where practical.

Do not implement future phases yet (no embedding/Qdrant work, no summary export).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (toggle API key states, confirm /api/health/ai and whyAsked behavior).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 8: Embedding service and Qdrant retrieval

#### Objective
Implement the Python embedding microservice and backend Qdrant integration for retrieval-augmented protocol context, with SQL Server keyword-search fallback.

#### Tasks
- Implement `embedding-service/app/main.py`: FastAPI with `POST /embed` (sentence-transformers all-MiniLM-L6-v2) and `GET /health`.
- Implement `EmbeddingClient` (backend) to call the embedding service per chunk.
- Implement `QdrantService`: upsert chunk vectors + metadata (protocolId, sectionTitle, chunkText, pageNumber, criterionId), and retrieval-by-similarity for explanation/question generation context.
- Wire protocol upload/chunking flow (Phase 3) to also embed + store vectors.
- Implement fallback: if Qdrant/embedding-service unreachable, retrieve via SQL Server keyword search over `ProtocolChunks`.

#### Files / Areas Likely Touched
`embedding-service/{Dockerfile, requirements.txt, app/main.py}`, `backend/.../Services/EmbeddingClient.cs`, `QdrantService.cs`.

#### Verification Steps
- `curl http://localhost:8001/health` returns healthy; `POST /embed` returns a vector.
- After protocol load, confirm vectors exist in Qdrant collection (`curl http://localhost:6333/collections/...`).
- Stop/simulate Qdrant unavailability and confirm the backend falls back to SQL Server keyword search without error.

#### Expected Result
Protocol chunks are embedded and retrievable via Qdrant, with a working non-crashing fallback path.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 8 only: embedding service and Qdrant retrieval.

Scope for this phase:
- Implement embedding-service (FastAPI, sentence-transformers all-MiniLM-L6-v2, /embed and /health), CPU-only.
- Implement EmbeddingClient and QdrantService in the backend, wired into the existing protocol upload/chunking flow.
- No need to implement fallback when Qdrant/embedding-service is unavailable.e.

Do not implement future phases yet (no frontend screens, no summary export).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (embedding health, embed call, Qdrant collection check, simulate Qdrant down).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 9: Frontend workflow screens

#### Objective
Build the React + Vite UI covering the full workflow: dashboard, upload, criteria review, question bank preview, screening session, and status/banner area. UI should be with enterprise looks n feel, with separate screens for 1. dashboard, 2. upload and criteria review, 3. question bank preview, 4. screening session with proper navigation.

#### Tasks
- Implement `src/pages/`: Home/Workflow Dashboard, Protocol Upload, Criteria Review, Question Bank Preview, Screening Session page (sections/progress/why-asked/next/end buttons).
- Implement `src/api/` client wrapping all backend endpoints; base URL from `VITE_API_BASE_URL`.
- Implement fallback banners: Claude fallback (`GET /api/health/ai` polling, refreshed after extraction/question-bank/summary), missing-key banner, using the exact copy and CSS from `prompt.txt`.
- Ensure UI enforces exactly two question sections everywhere (no Inclusion/Exclusion split) and shows the responsible-AI disclaimer.

#### Files / Areas Likely Touched
`frontend/src/{main.tsx, App.tsx, api/*, pages/*, components/*, styles/*}`.

#### Verification Steps
- Manually walk: dashboard → upload/use-sample → extract criteria → review/approve → generate question bank → start session → answer questions → reach summary link.
- Confirm banner appears when Claude key missing/invalid; confirm section labels match spec exactly.
- Confirm "why is this asked" and demographic-covers-criteria messaging render correctly.

#### Expected Result
A recruiter can complete the entire pre-summary workflow through the browser with correct section structure and fallback banners.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 9 only: frontend workflow screens.

Scope for this phase:
- Implement React pages: workflow dashboard, protocol upload, criteria review, question bank preview, screening session (sections, progress, why-asked, next/end buttons). UI should be with enterprise looks n feel, with separate screens for 1. dashboard, 2. upload and criteria review, 3. question bank preview, 4. screening session with proper navigation.
- Implement API client under src/api/ using VITE_API_BASE_URL.
- Implement Claude fallback / missing-key banners , refreshed after extraction/question-bank/summary.
- Enforce exactly two question sections in all UI and show the responsible-AI disclaimer.

Do not implement the Summary/export page yet (Phase 10).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (manual walkthrough upload through screening, banner checks).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 10: Summary, export, and audit trail

#### Objective
Generate the final recommendation/summary, expose export endpoints, build the Summary UI page, and implement the audit trail.

#### Tasks
- Implement summary generation in `ScreeningSessionService` (Claude-assisted reasoning/summarization with rule-based fallback) producing the full JSON contract from `prompt.txt` (recommendation, demographicSummary, criteriaSummary, coveredByDemographics, satisfied/failed/needsReview/skipped, missingInformation, reasoning, recommendedNextAction, disclaimer).
- Implement `ExportService` for JSON/CSV export; add `GET /{sessionId}/summary`, `/export/json`, `/export/csv`.
- Implement `AuditService` recording events for upload, extraction, approval, question bank generation, de-duplication, answers, summary; persist to `AuditEvents`.
- Build Summary page in frontend showing all required fields + export buttons.

#### Files / Areas Likely Touched
`Services/ScreeningSessionService.cs` (summary logic), `ExportService.cs`, `AuditService.cs`, `Controllers/ScreeningSessionsController.cs`, `Models/ScreeningSummary.cs`, `AuditEvent.cs`, `frontend/src/pages/Summary*`.

#### Verification Steps
- Complete a session for each of the 3 demo scenarios; confirm correct recommendation and full summary fields.
- `GET .../export/json` and `/export/csv` return valid downloadable content.
- Confirm `AuditEvents` table has rows for each tracked action type.

#### Expected Result
End-to-end recommendation generation, export, and audit logging all function correctly across all three scenario types.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 10 only: summary, export, and audit trail.

Scope for this phase:
- Implement final summary generation (Claude-assisted with rule-based fallback) matching the exact JSON contract in prompt.txt.
- Implement ExportService and summary/export/json/export/csv endpoints.
- Implement AuditService recording all required event types into AuditEvents.
- Build the frontend Summary page with export buttons.

Do not implement future phases yet (error-handling hardening is Phase 11, docs are Phase 12).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (run all 3 demo scenarios, check exports, check AuditEvents rows).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 11: Error handling, fallback, and responsible AI checks

#### Objective
Harden every failure path called out in `prompt.txt` (Claude, SQL Server, Qdrant, embedding service, PDF parsing, JSON cycles) and confirm responsible-AI guardrails are enforced everywhere.

#### Tasks
- Sweep all services/controllers for the full error list in `prompt.txt` (missing key, Claude failure/insufficient credits, invalid JSON, missing content field, SQL Server unavailable/login failure/db-open failure, missing tables, Qdrant unavailable, embedding service unavailable, PDF parsing failure, empty protocol text, no approved criteria, no remaining questions, JSON cycle issues) and confirm each degrades gracefully with a clear message instead of crashing.
- Verify no secret (Claude key, SQL password) appears in any log statement.
- Confirm disclaimer text and two-section rules hold under all fallback paths.
- Add/confirm Docker health checks and `depends_on` conditions tolerate delayed dependency startup (retry, not permanent failure).

#### Files / Areas Likely Touched
Cross-cutting: `Services/*`, `Controllers/*`, `Program.cs`, `docker-compose.yml` (health checks).

#### Verification Steps
- Deliberately break each dependency (stop sqlserver/qdrant/embedding-service, unset/invalidate Claude key, feed malformed PDF) one at a time and confirm the app stays up with a clear error/banner each time.
- Grep logs for the Claude key and SQL password values to confirm they never appear.

#### Expected Result
The application is resilient to every documented failure mode with no crashes and no leaked secrets.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 11 only: error handling, fallback, and responsible AI checks.

Scope for this phase:
- Sweep all services/controllers to confirm every failure mode listed in prompt.txt's Error Handling section degrades gracefully instead of crashing.
- Confirm no secrets (Claude API key, SQL Server password) appear in logs anywhere.
- Confirm disclaimer and two-section rules hold under all fallback paths.
- Tune Docker health checks / depends_on so delayed dependency startup is tolerated via retry.

Do not implement future phases yet (documentation is Phase 12).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (break each dependency one at a time, grep logs for secrets).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 12: Documentation and final demo validation

#### Objective
Finalize README.md, DEMO.md, CODESETUP.md, smoke-test.sh, and confirm the full Acceptance Criteria list in `prompt.txt` passes.

#### Tasks
- Write README.md per the 14-point outline in `prompt.txt` (prereqs, SQL Server Docker setup, .env, Claude key, run/URLs, sample protocol, demo session, two-section flow, de-dup, stop/reset, SSMS connection, troubleshooting).
- Write DEMO.md with the 3 scenario walkthroughs (Likely Eligible, Likely Ineligible, Needs Clinical Review) showing demographics-first, mixed criteria order, a de-dup example, and an early-ineligibility example.
- Write CODESETUP.md per the full 20-section outline in `prompt.txt`.
- Finalize `smoke-test.sh` covering all endpoints/checks listed in `prompt.txt`.
- Run through the full Acceptance Criteria checklist (44 items) and fix any gaps found.

#### Files / Areas Likely Touched
`README.md`, `DEMO.md`, `CODESETUP.md`, `smoke-test.sh`.

#### Verification Steps
- Run `./smoke-test.sh` (or documented equivalent) against a fresh `docker compose up --build` and confirm all checks pass.
- Manually confirm each of the 44 acceptance criteria in `prompt.txt`.
- Confirm hackathon simplifications are explicitly listed in both README.md and CODESETUP.md.

#### Expected Result
Documentation is accurate and complete; the full stack passes every acceptance criterion end-to-end.

#### Claude Implementation Prompt
```text
Read prompt.txt and CLAUDE.md for project rules. Implement Phase 12 only: documentation and final demo validation.

Scope for this phase:
- Write README.md, DEMO.md, and CODESETUP.md per the detailed outlines in prompt.txt.
- Finalize smoke-test.sh to cover all endpoints/checks in prompt.txt.
- Validate against the full 44-item Acceptance Criteria list and fix any small gaps found (docs/config only, not new features).

Do not add new application features.
Do not rewrite unrelated files unless required to close an acceptance-criteria gap.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (run smoke-test.sh, walk the acceptance criteria list).
- List changed files.
- List assumptions.
- List blockers, if any.
```

## 6. Verification Checklist
- `docker compose up --build` — full stack builds and starts.
- `docker compose down` / `docker compose down -v` — clean stop / full reset.
- `docker compose logs -f api|web|embedding-service|qdrant|sqlserver` — per-service logs.
- Frontend: http://localhost:5173
- Swagger: http://localhost:8080/swagger
- Health: http://localhost:8080/api/health
- Database health: http://localhost:8080/api/health/database
- AI health: http://localhost:8080/api/health/ai
- Embedding health: http://localhost:8001/health
- Qdrant: http://localhost:6333
- Sample protocol load: `POST /api/protocols/use-sample`
- Criteria extraction: `POST /api/protocols/{id}/extract-criteria`
- Question sections: `GET /api/protocols/{id}/questions/sections`
- Next question: `GET /api/screening-sessions/{id}/next-question`
- Summary export: `GET /api/screening-sessions/{id}/export/json` and `/export/csv`

## 7. Test Scenarios

**Likely Eligible**
- Input pattern: patient meets all inclusion criteria, no exclusion criteria triggered, demographics satisfy age/consent/logistics.
- Expected behavior: all demographic + criteria questions answered favorably, no early-stop triggered.
- Expected recommendation: `Likely Eligible`.
- UI verification: summary shows all-satisfied criteria, no failed/needs-review items, correct demographics-first + mixed-criteria ordering visible in session history.

**Likely Ineligible**
- Input pattern: an early high-priority exclusion criterion answer disqualifies the patient (e.g., disallowed condition = yes).
- Expected behavior: early-stop flagged as soon as the disqualifying question is answered; recruiter may continue or end session.
- Expected recommendation: `Likely Ineligible`.
- UI verification: summary highlights the specific failed criterion and reasoning; confirms screening could have ended early.

**Needs Clinical Review**
- Input pattern: uncertain/missing medication-history-type answer (e.g., "not sure" or free-text ambiguous response) on a criterion requiring clinical judgment.
- Expected behavior: criterion marked needs-review rather than pass/fail; session continues to completion.
- Expected recommendation: `Needs Clinical Review`.
- UI verification: summary lists missing/uncertain information and recommended next action (e.g., escalate to clinical reviewer).

**Cross-cutting checks for all 3 scenarios**
- Demographics are always asked before any criteria question.
- Criteria section order is visibly mixed (not all-inclusion-then-exclusion or vice versa).
- At least one criterion is shown as "covered by demographics" and not re-asked.
- Summary always includes demographic answers, covered-by-demographics list, satisfied/failed/needs-review/skipped lists, reasoning, next action, and the disclaimer text.

## 8. Risks and Mitigations
- **Claude insufficient credits** → `ClaudeService` detects error-shaped response, logs required message, `AiStatusService` flips to fallback, UI shows banner; criteria/question-bank/summary fall back to sample data/rule-based logic.
- **Invalid Claude response** → defensive JSON parsing (`TryGetProperty`), schema validation before use, fallback to sample data on any mismatch.
- **Qdrant unavailable** → `QdrantService` catches connection errors, backend falls back to SQL Server keyword search over `ProtocolChunks`.
- **SQL startup delay** → API retries connection on startup with backoff instead of failing permanently; Docker health checks + `depends_on` conditions gate readiness.
- **Missing tables / `Invalid object name`** → `EnsureCreatedAsync` runs before any query; startup fails loudly only if schema creation itself fails (logged, not silently ignored).
- **Login failure** → clear error surfaced via `/api/health/database`, no password logged.
- **EF JSON cycles** → DTOs preferred from controllers; `ReferenceHandler.IgnoreCycles` as backstop.
- **PdfPig restore failure** → pin to `1.7.0-custom-5`; document fallback to `sample-protocol.txt` and continued demo without live PDF parsing.
- **Port conflicts (1433, 5173, 8080, 6333)** → document alternate host port mapping (e.g., `11433:1433`) in README/CODESETUP while internal Docker networking stays unchanged.
- **Frontend/API connectivity** → `VITE_API_BASE_URL` env-driven; CORS configured on API for local dev origin.
- **Embedding model first-run delay** → document expected download time in CODESETUP; health check retries tolerate slow first start.
- **Corporate proxy** → document `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` (including all service hostnames) in README/CODESETUP.

## 9. Definition of Done
- [ ] `docker compose up --build` starts web, api, embedding-service, qdrant, sqlserver together.
- [ ] All required health/status URLs respond as documented.
- [ ] Backend auto-creates SQL Server schema with no manual scripts; no `Invalid object name` errors.
- [ ] No JSON object-cycle serialization errors from any endpoint.
- [ ] Protocol can be uploaded or loaded from sample data, with extraction fallback working.
- [ ] Criteria can be extracted, reviewed, edited, and approved.
- [ ] Question bank always has exactly two sections (Demographics, Criteria) with mixed criteria ordering and correct de-duplication.
- [ ] Screening session asks demographics first, then mixed criteria, one question at a time, with why-asked/criterion-mapping shown.
- [ ] Final summary/recommendation covers all three outcome types with full required fields and disclaimer.
- [ ] Export to JSON and CSV works.
- [ ] Claude failures (missing key, invalid response, insufficient credits) never crash the app and always show the correct fallback banner/message.
- [ ] Audit trail records all required event types.
- [ ] README.md, DEMO.md, CODESETUP.md, smoke-test.sh exist and are accurate; hackathon simplifications are explicitly documented.
- [ ] All 44 Acceptance Criteria items in `prompt.txt` are satisfied.
