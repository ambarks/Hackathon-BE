# CLAUDE.md

Repo-level rules for implementing the AI-Powered Clinical Trial Patient Pre-Screening Assistant POC. Read alongside `prompt.txt` (full requirements) and `plan.md` (phased execution plan) before making changes.

## Project Overview
- Hackathon POC demonstrating AI-assisted patient pre-screening for clinical trials.
- Flow: upload/parse protocol → extract criteria → review/approve → generate two-section question bank → run adaptive screening session → produce final recommendation + export.
- Not a production clinical system. No final medical/diagnostic/recruitment decisions are made by the app.

## Product Guardrails
- Sample/synthetic data only — never real patient data or PHI.
- Human review required: recruiter/clinical reviewer makes the final call.
- Every screen/output must be framed as guidance, not a decision.
- UI disclaimer (verbatim, must appear): "This tool provides AI-assisted pre-screening guidance only. Final eligibility must be confirmed by qualified clinical staff."
- Store prompt version + model name with AI-generated outputs where practical.
- Audit trail required for: protocol upload, criteria extraction, criteria approval, question bank generation, de-duplication, screening answers, summary generation.

## Tech Stack
- Frontend: React + Vite (port 5173)
- Backend: .NET 8 Web API (port 8080, Swagger enabled)
- AI reasoning: Claude API (backend-only)
- PDF parsing: PdfPig (`UglyToad.PdfPig` version `1.7.0-custom-5`, NOT `0.1.9`)
- Embedding service: Python FastAPI + sentence-transformers `all-MiniLM-L6-v2` (port 8001)
- Vector DB: Qdrant (ports 6333/6334)
- Database: SQL Server 2022 in Docker (service name `sqlserver`, port 1433, EF Core provider)
- Orchestration: custom backend services (no external workflow engine)
- Storage: Docker volumes + local mounted folder (uploads/exports)
- Logging: default .NET logging (no secrets)
- Everything must run via a single `docker compose up --build`

## Architecture Rules
- Layering: Controllers → Services → Data Access → External Clients. Controllers never call Claude, Qdrant, embedding service, or raw SQL directly.
- Isolate external integrations in dedicated services: `ClaudeService`, `QdrantService`, `EmbeddingClient`.
- Business logic isolation: `AdaptiveQuestionService` (next-question logic), `ScreeningSessionService` (session state + final recommendation), `ProtocolTextExtractionService` (PdfPig), `ProtocolChunkingService`, `AuditService`, `ExportService`, `DatabaseInitializationService`/`DbInitializer`.
- Use DI for all services; async/await for all I/O.
- Never expose the Claude API key to the frontend or browser network calls.
- Return DTOs/shaped objects from controllers, not raw EF entities with navigation properties (avoids JSON reference cycles). If entities must be returned, set `ReferenceHandler.IgnoreCycles` globally as a backstop, not a substitute for DTOs.

## Question Flow Rules
- Exactly two sections everywhere (API, UI, sample data): **Demographics / Demographic Information** and **Criteria / Eligibility Criteria Questions**. Never surface separate Inclusion/Exclusion UI sections.
- Order: demographic questions first, then eligibility criteria questions in an AI-recommended (or deterministic fallback) mixed order — not inclusion-then-exclusion or exclusion-then-inclusion blindly.
- Mixed ordering favors: high-impact exclusion criteria that can quickly disqualify, essential high-priority inclusion criteria, easy-to-answer questions, and questions likely to end screening early.
- De-duplication: if a demographic answer covers a criterion, do not re-ask it under Criteria. Track `linkedCriteria`, `coveredCriteria`, `suppressedDuplicateCriteria`. One demographic question may cover multiple criteria; answering it updates all linked criteria statuses.
- Every question needs: questionId, section, questionText, answerType, options, priority, displayOrder, linkedCriteria, coveredCriteria, sourceCriteriaText, whyAsked, isDemographicQuestion, isDuplicateSuppressed, canTriggerEarlyStop, earlyStopReason.
- Adaptive logic is backend-controlled and deterministic (state machine), not LLM-driven — Claude only *recommends* sequencing/explanations; backend enforces the actual flow and criteria status.
- Track per-criterion status: satisfied, failed, needs review, unanswered, covered-by-demographics, skipped-by-dedup, skipped-by-adaptive-logic. Track section + section progress + overall likely status.

## Claude / AI Rules
- Claude is called only from backend `ClaudeService`, never from frontend or controllers directly.
- Claude is not the sole decision engine — backend maintains explicit rules/state for satisfied/failed/unanswered/needs-review criteria; Claude assists with extraction, simplification, sequencing suggestions, explanations, and summarization only.
- Every prompt must include the guardrails from `prompt.txt` (pre-screening only, no final decisions, use only provided text, flag missing/uncertain info, return valid JSON, simple wording, exactly two sections, optimized mixed criteria order, avoid duplicates).
- `ClaudeService.SendAsync` must defensively parse responses: check HTTP status before parsing, use `TryGetProperty` (never `GetProperty`) for optional fields, detect Anthropic error-shaped JSON (`type: "error"`), detect insufficient-credit errors, return `null` on any failure instead of throwing, and never throw `KeyNotFoundException`.
- Insufficient-credit log message (verbatim): "Claude API configured but API call failed due to insufficient credits. Running in fallback mode."
- `AiStatusService` (singleton) tracks ClaudeConfigured, LastCallSucceeded, RunningInFallbackMode, LastError, LastStatusMessage, LastCheckedAt; exposed via `GET /api/health/ai`. Frontend polls this and refreshes after criteria extraction, question bank generation, and summary generation, showing the fallback banner when applicable.
- Fallback data sources: `sample-data/sample-criteria.json` is read directly by `CriteriaExtractionService` when Claude fails. `sample-data/sample-question-bank.json` is a populated reference/demo fixture satisfying the two-section + de-dup rules; the actual runtime fallback for question generation is `QuestionBankService`'s deterministic backend sequencing algorithm (computed from whatever criteria are approved), not a file read.
- No Claude call anywhere may crash the app — every failure path degrades to fallback/rule-based behavior.

## Database Rules
- SQL Server 2022 runs as the `sqlserver` Docker service; backend connects via `SQLSERVER_HOST=sqlserver` (or `CONNECTION_STRING` override).
- Never assume a locally installed SQL Server/SQLEXPRESS; no manual TCP/IP or Configuration Manager setup required.
- Schema creation must be automatic on first run: prefer `db.Database.EnsureCreatedAsync()`; optionally attempt migrations first and fall back safely to `EnsureCreatedAsync()`. Never require manual SQL scripts.
- Must never fail with `Invalid object name 'Protocols'` — schema must exist before first query.
- Required tables: Protocols, ProtocolChunks, EligibilityCriteria, ScreeningQuestions, ScreeningSessions, ScreeningAnswers, ScreeningSummaries, AuditEvents.
- Backend must tolerate delayed SQL Server/Qdrant/embedding-service startup via retry logic, not permanent failure.
- Never log the SQL Server password or Claude API key.

## Development Commands
- `docker compose up --build` — build and start full stack
- `docker compose up` — start without rebuild
- `docker compose down` — stop stack
- `docker compose down -v` — stop and wipe volumes (resets DB/Qdrant data)
- `docker compose logs -f api|web|embedding-service|qdrant|sqlserver` — tail logs per service
- `docker compose restart api` — restart backend only
- `docker ps` / `docker system prune` — inspect / clean up

## Health Check URLs
- Frontend: http://localhost:5173
- Swagger: http://localhost:8080/swagger
- API health: http://localhost:8080/api/health
- Database health: http://localhost:8080/api/health/database
- AI health: http://localhost:8080/api/health/ai
- Qdrant: http://localhost:6333
- Embedding service health: http://localhost:8001/health

## Coding Standards
- .NET: nullable-aware, async/await for I/O, DI-registered services, DTOs for API responses, no business logic in controllers.
- Keep NuGet versions .NET-8-compatible; use `UglyToad.PdfPig 1.7.0-custom-5`. If PdfPig restore fails, fall back to `sample-data/sample-protocol.txt` and keep upload flow working rather than blocking the build.
- React: functional components, colocate API calls under `src/api/`, keep section rendering logic centered on exactly two sections.
- Python embedding service: CPU-only, no GPU dependency, minimal FastAPI app with `/embed` and `/health`.
- Do not hardcode `linux/amd64`; keep images cross-platform (macOS Apple Silicon/Intel, Windows, Linux).

## Testing Guidance
- Use `smoke-test.sh` / curl to verify health endpoints, sample protocol load, fallback criteria extraction, question sections endpoint, and next-question endpoint after each relevant phase.
- Manually verify the three demo scenarios (Likely Eligible, Likely Ineligible, Needs Clinical Review) end-to-end before declaring a phase involving screening/summary complete.
- Verify fallback mode explicitly (missing/invalid Claude key) does not crash any endpoint.

## Documentation Expectations
- Keep README.md, DEMO.md, CODESETUP.md, and .env.example in sync with actual behavior — do not describe endpoints or flags that don't exist.
- Mark any hackathon simplification explicitly in README.md/CODESETUP.md rather than silently deviating from `prompt.txt`.
- Each implementation phase should update docs only as needed for what it delivered — don't pre-document future phases.
