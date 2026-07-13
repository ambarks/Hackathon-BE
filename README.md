# Clinical Trial Pre-Screening Assistant

AI-assisted pre-screening guidance for clinical trial recruiters — a hackathon proof of concept.

> "This tool provides AI-assisted pre-screening guidance only. Final eligibility must be confirmed by qualified clinical staff."

This is **not** a production clinical decisioning system. It uses only synthetic/sample data and does not make final medical, diagnostic, or recruitment decisions.

## 1. Prerequisites

- Docker Desktop (or Rancher Desktop) with Docker Compose v2
- Git
- (Optional) A Claude / Anthropic API key — the app runs fully in fallback demo mode without one
- (Optional) SQL Server Management Studio / Azure Data Studio, if you want to inspect the database directly

See [CODESETUP.md](CODESETUP.md) for a full local development setup guide.

## 2. How Docker SQL Server is configured

SQL Server 2022 runs as the `sqlserver` service inside `docker-compose.yml` — you do **not** need a local SQL Server or SQLEXPRESS install. The `api` container always connects to it via the Docker network as `sqlserver,1433`. On first run, the backend automatically creates the `ClinicalTrialPreScreening` database and all required tables (`Protocols`, `ProtocolChunks`, `EligibilityCriteria`, `ScreeningQuestions`, `ScreeningSessions`, `ScreeningAnswers`, `ScreeningSummaries`, `AuditEvents`) using `EnsureCreatedAsync` — no manual SQL scripts required.

If host port `1433` is already in use (e.g. by a local SQL Server install), change the `sqlserver` service's port mapping in `docker-compose.yml` to `"11433:1433"`. The `api` container is unaffected (it always talks to `sqlserver,1433` on the internal Docker network); use `localhost,11433` from SSMS instead.

## 3. How to create .env

```
cp .env.example .env
```

Edit `.env` and fill in `ANTHROPIC_API_KEY` if you have one. Every other variable has a working default for local Docker Compose use. **Never commit `.env` to source control.**

## 4. How to configure the Claude key

Set `ANTHROPIC_API_KEY` in `.env` to your real key, and optionally change `ANTHROPIC_MODEL` (defaults to `claude-3-5-sonnet-latest`). If the key is missing, invalid, or the account has insufficient credits, the app automatically runs in fallback mode (sample criteria / deterministic sequencing / rule-based summaries) and shows a warning banner — it never crashes.

## 5. How to run

```
docker compose up --build
```

First run downloads Docker images, builds the frontend/backend/embedding-service images, starts SQL Server, and creates the database schema automatically. The embedding service downloads the `all-MiniLM-L6-v2` model on first start, which can take a few minutes on a slow connection.

## 6. URLs to open

| Service | URL |
|---|---|
| Frontend | http://localhost:5173 |
| Backend Swagger | http://localhost:8080/swagger |
| Backend health | http://localhost:8080/api/health |
| Backend database health | http://localhost:8080/api/health/database |
| Backend AI health | http://localhost:8080/api/health/ai |
| Qdrant | http://localhost:6333 |
| Embedding service health | http://localhost:8001/health |

## 7. How to use the sample protocol

Open the frontend, go to **Upload & Criteria** and click **Use Sample Protocol** (no file needed). This loads `sample-data/sample-protocol.txt`, a synthetic Type 2 Diabetes trial protocol. You can also upload a real PDF — if PDF text extraction fails for any reason, the app automatically falls back to the sample protocol text and keeps working.

## 8. How to run a demo screening session

1. **Upload & Criteria** page → Use Sample Protocol → Extract Criteria → Approve All Criteria → Generate Question Bank.
2. **Question Bank** page → review the two sections and sequencing rationale → enter a patient alias → Start Screening Session.
3. **Screening Session** page → answer questions one at a time until complete, or click **End and Generate Summary** at any point.
4. **Summary** page → view the recommendation, reasoning, and export as JSON/CSV.

See [DEMO.md](DEMO.md) for three full walkthroughs (Likely Eligible, Likely Ineligible, Needs Clinical Review).

## 9. How the two-section question flow works

Every generated question bank and every screening session has **exactly two sections**:

1. **Demographic Information** — age, pregnancy status (when relevant), consent capability, visit availability, and other patient-profile/logistical questions. Always asked first.
2. **Eligibility Criteria Questions** — all remaining inclusion and exclusion criteria, in a single mixed list. High-impact exclusion criteria and essential inclusion criteria are ordered early for faster screening. Inclusion and exclusion questions are **never** split into separate UI sections.

If Claude is unavailable, a deterministic backend algorithm produces the same two-section structure: demographics-coverable criteria become Demographics questions, and the remainder are ordered by priority (High → Low), with exclusion criteria placed ahead of inclusion criteria within the same priority tier.

## 10. How duplicate criteria are suppressed

When a demographic question already resolves a criterion (e.g. age resolves the "18–75 years" inclusion criterion), that criterion is **never** asked again as a separate question in the Eligibility Criteria Questions section. The question bank generation response and the Question Bank Preview page list these as `suppressedDuplicateCriteria`, and the final summary reports them under "criteria covered by demographics."

## 11. How to stop

```
docker compose down
```

## 12. How to reset all data

```
docker compose down -v
```

This removes the SQL Server, Qdrant, and uploads/exports volumes, so the next `docker compose up --build` starts from a completely empty database. **This is also required after pulling backend changes that add new database columns**, since `EnsureCreatedAsync` only creates the schema on a fresh database — it does not apply incremental migrations to an existing one.

## 13. Connecting to SQL Server from SSMS

- Server name: `localhost,1433` (or `localhost,11433` if you remapped the port — see section 2)
- Authentication: SQL Server Authentication
- Login: `sa`
- Password: the value of `SQLSERVER_PASSWORD` in your `.env`
- Enable **Trust Server Certificate** if prompted

## 14. Troubleshooting

| Symptom | Fix |
|---|---|
| Claude API key missing | App runs in fallback mode automatically; banner reads "Claude API key is not configured. Running in local fallback demo mode." |
| Claude insufficient credits | App runs in fallback mode automatically; banner reads "Claude API configured but API call failed due to insufficient credits. Running in fallback mode." |
| SQL Server container not ready | The `api` service waits for `sqlserver`'s health check and retries schema creation internally; first run can take up to ~1–2 minutes. |
| "Failed to open explicitly specified database" | Usually a transient startup timing issue — wait and retry, or check `docker compose logs sqlserver`. |
| "Invalid object name 'Protocols'" | Should not occur — schema is created automatically before first query. If you see this, run `docker compose down -v` to reset and rebuild. |
| JSON object cycle error | Should not occur — all API responses use DTOs and the API applies `ReferenceHandler.IgnoreCycles` globally as a backstop. |
| Qdrant not reachable | Protocol upload still succeeds; vector storage for that protocol is skipped and a warning is logged. |
| Embedding model first-run delay | The embedding-service health check tolerates several minutes on first start while the model downloads. |
| PDF extraction failure | The app automatically falls back to `sample-data/sample-protocol.txt` and keeps the upload flow working. |
| Port conflicts (1433, 5173, 8080, 6333) | Change the host-side port mapping in `docker-compose.yml` (left side of `"host:container"`); internal Docker networking is unaffected. |
| Corporate proxy issues | Set `HTTP_PROXY` / `HTTPS_PROXY` / `NO_PROXY` — see [CODESETUP.md](CODESETUP.md) section 15. |

## Known hackathon simplifications

- Single sample protocol and three synthetic patient scenarios; no real patient data.
- `EnsureCreatedAsync` is used for schema creation instead of production-grade EF Core migrations.
- SQL Server and Qdrant run locally in Docker rather than as managed enterprise services.
- No authentication or multi-user support.
- Rule-based/deterministic fallback logic stands in for Claude whenever it is unavailable.
- AI-assisted early-stop evaluation (interpreting an answer against a disqualifying criterion, e.g. age vs a required range) calls Claude only for questions linked to an Exclusion or Required-Inclusion criterion, not every answer; in fallback mode it degrades to the same yes/no heuristic used elsewhere, which does not catch numeric-range cases.

## Responsible AI

This application is a hackathon proof of concept. It provides AI-assisted pre-screening guidance only. It must not be used for real patient recruitment, clinical decision-making, diagnosis, treatment, or final eligibility determination. Final eligibility must be confirmed by qualified clinical staff.
