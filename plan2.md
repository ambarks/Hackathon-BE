# Implementation Plan 2 — AI-Assisted Adaptive Early-Stop Screening

Reference: `prompt.txt` (full requirements), `CLAUDE.md` (standing rules), and `plan.md` (original 13-phase plan, phases 0–12, already implemented). This is an **addendum plan** that extends the existing adaptive screening session flow (`plan.md` Phase 6/7) with AI-assisted per-answer criterion evaluation and early-stop recommendation. Phases here are numbered 13–18 to continue `plan.md`'s sequence; each phase is independently verifiable and must not regress any phase 0–12 behavior.

## 1. Problem Statement

Today, `AdaptiveQuestionService.DetermineMappedStatus` classifies every answer with a crude yes/no heuristic (first word of the free-text response), and `AdaptiveQuestionService.ComputeState` already computes an `earlyStopTriggered` flag (`any failed Exclusion criterion`) on every answer — but this flag is discarded before it reaches any controller response. Two gaps result:

1. **Classification gap**: the yes/no heuristic cannot correctly evaluate numeric/range-based criteria (e.g., patient answers age `16` against a criterion requiring `18–75`) — it has no way to interpret free-text/numeric answers against criterion text.
2. **Surfacing gap**: even when disqualification is deterministically computable, the recruiter is never told the screening result is already determined — the session simply continues asking further questions one by one.

This plan closes both gaps: Claude assists with interpreting the answer against the criterion text (closing gap 1), and the backend surfaces its own already-existing deterministic disqualification rule as an explicit, audited recommendation (closing gap 2) — without letting Claude become the decision engine, per `CLAUDE.md`.

## 2. Scope

**In scope**
- AI-assisted interpretation of a single answer against its linked criterion, for questions that can actually disqualify a patient (`CanTriggerEarlyStop` questions — those linked to an Exclusion or Required-Inclusion criterion).
- A fixed, backend-enforced disqualification rule that runs after every answer (AI-assisted or not) and produces an explicit `earlyStopRecommended`/`earlyStopReason` signal.
- Surfacing that signal through the API and a recruiter-facing UI banner with an explicit choice to end the session early (generate summary now) or continue answering.
- Audit trail entries for every AI evaluation call (success or fallback), per `CLAUDE.md`.
- Demo scenario, docs, and smoke-test updates covering the age-range disqualification example from the requirement.

**Out of scope**
- Dynamic re-ranking of which question to ask next. The pre-generated static ordered question list (from `QuestionBankService`) stays exactly as-is; this plan only decides *whether to keep going*, not *what order to ask in*.
- Replacing the existing yes/no heuristic for questions that are **not** linked to a disqualifying criterion (non-`CanTriggerEarlyStop` questions keep their current deterministic classification, unchanged).
- Forcing/blocking the session. The backend never auto-completes a session or blocks `next-question` on its own — ending early is always an explicit recruiter action.
- Any change to summary generation logic beyond correctly handling a session ended early with unanswered questions remaining.

**Hackathon simplifications** (to be documented in README/CODESETUP alongside the existing list)
- The extra Claude evaluation call fires only for `CanTriggerEarlyStop`-linked answers, not every answer, to bound added latency/token cost.
- Fallback mode for this feature reuses the existing crude yes/no heuristic (`DetermineMappedStatus`) — it will not catch the numeric-range case (age 16 vs 18–75) when Claude is unavailable; this is an accepted, documented limitation of fallback mode, consistent with the project's existing "degrade gracefully, never crash" rule rather than "always catch every case."

## 3. Architecture Summary / Integration Points

- **No new services beyond one**: a new `AnswerEvaluationService` sits alongside `AdaptiveQuestionService`, called only from `ScreeningSessionService.RecordAnswerAsync`. It follows the exact same `ClaudeService.SendAsync` → defensive JSON parse → rule-based fallback pattern already used by `CriteriaExtractionService`, `QuestionBankService`, and the existing summary-narrative code — no new integration pattern is introduced.
- **Backend remains the decision engine**: Claude's output only ever produces a per-criterion `mappedStatus` + `reasoning`. Whether that status is *disqualifying* is a fixed backend rule (`EligibilityImpact == Exclusionary|Required` and `status == failed`) evaluated in `ScreeningSessionService`, independent of what Claude says about it — matching `CLAUDE.md`'s "Claude assists ... backend enforces" rule.
- **Session never auto-completes**: `session.Status` stays `InProgress` and `next-question` keeps returning questions exactly as before; only new `EarlyStopRecommended`/`EarlyStopReason` fields are added, and a new explicit `POST /{sessionId}/end` action lets the recruiter close the session on demand.
- **Fallback behavior**: identical shape to every other Claude call site in the project — `ClaudeService.SendAsync` returns `null` on any failure, `AiStatusService` is updated, and `AnswerEvaluationService` falls back to the existing `DetermineMappedStatus` heuristic. No new failure mode is introduced.

## 4. Key Design Decisions

- **Hybrid AI-assist, backend-enforced** (per `CLAUDE.md`): Claude interprets the free-text/numeric answer against the criterion text and suggests `satisfied|failed|needs_review` + reasoning; the backend validates this against its own fixed disqualification rule and is the sole source of truth for whether screening should stop. Deterministic rule-based fallback (existing heuristic) applies whenever Claude is unavailable or returns unparseable output.
- **Static question order preserved**: this plan does not touch `AdaptiveQuestionService`'s next-question selection logic (still the pre-built ordered list from `QuestionBankService`). Only the "should we recommend stopping" signal is new.
- **Targeted AI calls only**: the evaluation call fires only when `question.CanTriggerEarlyStop == true` (i.e., the question is linked to an Exclusion or Required-Inclusion criterion) — this reuses metadata already generated by `QuestionBankService` and avoids an AI call on every single answer.
- **Recommend, don't force**: disqualification never blocks or auto-completes the session. It surfaces a clear, audited, human-readable recommendation with reasoning; the recruiter explicitly chooses to end (new `POST /{sessionId}/end`) or continue, honoring the "human review required" product guardrail.
- **Audit trail for every evaluation**: every `AnswerEvaluationService` call — whether it used Claude or fell back — writes an `AuditEvent`, satisfying `CLAUDE.md`'s requirement to audit screening answers.
- **No schema migrations**: new columns on `ScreeningAnswer`/`ScreeningSession` are picked up via the existing `EnsureCreatedAsync` approach; a fresh `docker compose down -v && up --build` is required to apply them to an existing local dev database (documented, not automated — consistent with the project's no-migrations simplification).

## 5. Implementation Phases

### Phase 13: Data model and audit trail additions

#### Objective
Add the persistence fields and audit event type needed for AI-assisted per-answer evaluation and early-stop recommendation, without changing any existing behavior.

#### Tasks
- Extend `ScreeningAnswer`: add `EvaluationSource` (`"ai"` | `"fallback-rule"`), `AiReasoning` (string, nullable), `PromptVersion` (string, nullable), `ModelName` (string, nullable) — mirroring the tagging pattern already used on `ScreeningQuestion`.
- Extend `ScreeningSession`: add `EarlyStopRecommended` (bool, default false), `EarlyStopReason` (string, nullable), `DisqualifyingCriterionId` (string, nullable), `EarlyStopDetectedAt` (DateTime, nullable).
- Add a new audit event type constant (e.g. `screening_answer_ai_evaluation`) to `AuditService`/`AuditEvent` usage, capturing `sessionId`, `questionId`, `criterionId`, whether Claude or fallback was used, and a short reasoning excerpt.
- Confirm `AppDbContext` picks up the new columns automatically via `EnsureCreatedAsync` on a fresh database.

#### Files / Areas Likely Touched
`Models/ScreeningAnswer.cs`, `Models/ScreeningSession.cs`, `Services/AuditService.cs`, `Data/AppDbContext.cs` (if explicit configuration is needed).

#### Verification Steps
- `docker compose down -v && docker compose up --build`; confirm via SSMS that `ScreeningAnswers` and `ScreeningSessions` tables contain the new columns.
- Confirm all existing screening endpoints still build and behave exactly as before (no functional change yet in this phase).

#### Expected Result
New columns and audit event type exist and are ready to be populated by later phases; zero behavior change for existing flows.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 13 only: data model and audit trail additions for AI-assisted early-stop screening.

Scope for this phase:
- Add EvaluationSource, AiReasoning, PromptVersion, ModelName fields to ScreeningAnswer.
- Add EarlyStopRecommended, EarlyStopReason, DisqualifyingCriterionId, EarlyStopDetectedAt fields to ScreeningSession.
- Add a new audit event type for AI-assisted answer evaluation (used in a later phase) to AuditService.
- Do not change any existing service/controller logic or behavior in this phase — this is schema/plumbing only.

Do not implement future phases yet (no AnswerEvaluationService, no disqualification enforcement, no API/UI changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (fresh docker compose down -v && up --build, confirm new columns via SSMS, confirm existing endpoints unaffected).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 14: AI-assisted per-answer criterion evaluation service

#### Objective
Build a focused Claude call that interprets a single answer against its linked disqualifying criterion (Exclusion or Required-Inclusion) to produce `satisfied|failed|needs_review` + reasoning, using the same defensive-parse + deterministic-fallback pattern as every other Claude call site in the project, invoked only for `CanTriggerEarlyStop` questions.

#### Tasks
- Implement `AnswerEvaluationService.EvaluateAsync(ScreeningQuestion question, EligibilityCriterion criterion, string answerText)` returning a result with `MappedStatus`, `Reasoning`, `EvaluationSource`, `PromptVersion`, `ModelName`.
- Build system + user prompts per `CLAUDE.md` guardrails (pre-screening only, no final decision, use only the provided criterion text/answer, flag missing/uncertain info, return valid JSON, simple wording). User prompt includes the criterion's `originalText`/`simpleMeaning`, its `answerType`/`options`, and the patient's exact answer text — kept minimal (not the full session history) to control token usage.
- Call `ClaudeService.SendAsync`; parse defensively (`TryGetProperty`, outer-JSON extraction consistent with existing services); on any null/parse failure, fall back to the existing `AdaptiveQuestionService.DetermineMappedStatus` heuristic and set `EvaluationSource = "fallback-rule"`.
- Only invoke this service when `question.CanTriggerEarlyStop == true`; leave all other questions on the existing unchanged heuristic path.
- Write an `AuditEvent` (Phase 13's new type) for every call, success or fallback.

#### Files / Areas Likely Touched
`Services/AnswerEvaluationService.cs` (new), `Services/AdaptiveQuestionService.cs` (expose `DetermineMappedStatus` for reuse rather than duplicating it), `Services/AuditService.cs`, `Prompts/` (new prompt template if the project keeps prompts in a dedicated folder).

#### Verification Steps
- With a valid Claude key: submit an answer of `16` to a demographic/criteria question linked to an "age 18–75" criterion; confirm the service returns `failed` with reasoning that references the age range.
- With Claude unavailable (missing/invalid key): confirm the service falls back to the existing heuristic without throwing, and the audit event records `EvaluationSource = "fallback-rule"`.
- Confirm the service is never called for questions where `CanTriggerEarlyStop == false` (existing behavior for those questions is provably unchanged).

#### Expected Result
A working, defensively-parsed, fallback-safe AI evaluation call limited to disqualifying-question answers, fully audited, with no change to how any other answer is classified.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 14 only: AI-assisted per-answer criterion evaluation service.

Scope for this phase:
- Implement AnswerEvaluationService.EvaluateAsync(question, criterion, answerText) calling ClaudeService.SendAsync with a guardrail-compliant prompt (pre-screening only, no final decisions, use only provided text, flag uncertainty, return valid JSON).
- Defensively parse the response (TryGetProperty, outer-JSON extraction) consistent with existing services (CriteriaExtractionService/QuestionBankService pattern); on any failure, fall back to the existing AdaptiveQuestionService.DetermineMappedStatus heuristic.
- Only call this service for questions where CanTriggerEarlyStop is true; leave all other question classification unchanged.
- Write an audit event (using the type added in Phase 13) for every call, whether it used Claude or the fallback.

Do not implement future phases yet (no disqualification-rule enforcement in ScreeningSessionService, no API/UI changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (age-16-vs-18-75 example with a valid key, and with Claude unavailable to confirm fallback).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 15: Deterministic disqualification enforcement

#### Objective
Enforce the actual early-stop decision as a fixed backend rule over the (AI-assisted or fallback) criterion status computed in Phase 14 — Claude's output feeds classification only; the disqualification decision itself is always backend-computed, per `CLAUDE.md`.

#### Tasks
- In `ScreeningSessionService.RecordAnswerAsync`, after the answer's `MappedEligibilityStatus` is set (via Phase 14's service for `CanTriggerEarlyStop` questions, or the existing heuristic otherwise), apply the fixed rule: if the linked criterion's `EligibilityImpact` is `Exclusionary` or `Required` and the resulting status is `failed`, set `session.EarlyStopRecommended = true`, `session.EarlyStopReason` (prefer the AI's reasoning text when available, else a deterministic templated message), `session.DisqualifyingCriterionId`, `session.EarlyStopDetectedAt`.
- Reuse/expose the existing `earlyStopTriggered` computation already present in `AdaptiveQuestionService.ComputeState` (today discarded before reaching the API) rather than reimplementing the rule twice.
- Explicitly confirm `session.Status` and `next-question` availability are unaffected by this flag — the session stays `InProgress` and continues offering questions exactly as before.
- Persist the new session fields as part of the existing recompute-and-save flow in `RecordAnswerAsync`.

#### Files / Areas Likely Touched
`Services/ScreeningSessionService.cs`, `Services/AdaptiveQuestionService.cs`.

#### Verification Steps
- Answer a question that fails a Required-Inclusion or Exclusion criterion; confirm the persisted `ScreeningSession` row shows `EarlyStopRecommended = true` with a non-empty reason and the correct `DisqualifyingCriterionId`.
- Confirm `next-question` still returns a question afterward if any remain unanswered (not blocked).
- Confirm answering a `satisfied` or `needs_review` result on any criterion never sets `EarlyStopRecommended`.

#### Expected Result
The session record accurately and deterministically reflects "screening result is already determined" the moment it becomes true, while continuing to behave exactly as before for every other aspect of the flow.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 15 only: deterministic disqualification enforcement.

Scope for this phase:
- In ScreeningSessionService.RecordAnswerAsync, after the answer's mapped status is determined (via Phase 14's AnswerEvaluationService for CanTriggerEarlyStop questions, or the existing heuristic otherwise), apply a fixed rule: if the linked criterion is Exclusionary or Required and the status is failed, set EarlyStopRecommended/EarlyStopReason/DisqualifyingCriterionId/EarlyStopDetectedAt on the session.
- Reuse the existing earlyStopTriggered computation in AdaptiveQuestionService.ComputeState instead of duplicating the rule.
- Do not change session.Status transitions or next-question availability — the session must remain InProgress and keep offering questions as before.

Do not implement future phases yet (no API DTO changes, no new endpoints, no frontend changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (answer a disqualifying question, check session row fields; confirm next-question still works; confirm non-disqualifying answers never set the flag).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 16: API surface for early-stop recommendation

#### Objective
Expose the early-stop recommendation through the screening API and add an explicit recruiter-initiated action to end a session early and generate its summary.

#### Tasks
- Extend the `AnswerResponse` DTO with `EarlyStopRecommended`, `EarlyStopReason`, `DisqualifyingCriterionId`.
- Extend the `GET /{sessionId}` session-status response and `NextQuestionResponse` with the same fields, so polling/refreshing the page also reflects current recommendation state.
- Add `POST /api/screening-sessions/{sessionId}/end`: marks the session `Completed` immediately regardless of remaining unanswered questions, and is usable at any time (not only when `EarlyStopRecommended` is true) so the recruiter always has the option to stop.
- Confirm `GET /{sessionId}/summary` correctly reports any remaining unanswered questions as `unanswered`/`missingInformation` when invoked after an early `end` call, without crashing.

#### Files / Areas Likely Touched
`Controllers/ScreeningSessionsController.cs`, DTOs for `AnswerResponse`/`NextQuestionResponse`/session-status.

#### Verification Steps
- Submit a disqualifying answer; confirm the `answers` response includes the new fields with correct values.
- Call `POST /{sessionId}/end`; confirm the session becomes `Completed`.
- Call `GET /{sessionId}/summary` after an early end; confirm it returns a valid summary referencing the disqualifying criterion and listing unanswered questions correctly.

#### Expected Result
The API fully surfaces the early-stop signal and gives the recruiter an explicit, working way to end a session before all questions are answered.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 16 only: API surface for early-stop recommendation.

Scope for this phase:
- Add EarlyStopRecommended, EarlyStopReason, DisqualifyingCriterionId to the AnswerResponse, NextQuestionResponse, and session-status response DTOs.
- Add POST /api/screening-sessions/{sessionId}/end that marks the session Completed immediately (regardless of remaining unanswered questions) and is callable at any time.
- Confirm GET /{sessionId}/summary handles a session ended early gracefully, reporting unanswered questions correctly.

Do not implement future phases yet (no frontend changes, no docs/demo updates).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (submit disqualifying answer, check response fields; call end endpoint; call summary endpoint after early end).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 17: Frontend early-stop recommendation UI

#### Objective
Surface the early-stop recommendation to the recruiter as a clear banner with an explicit choice to end or continue, honoring the "recommend, don't force" and "human review required" guardrails.

#### Tasks
- Update the screening session page: after each answer submission, check `earlyStopRecommended`; if true, show a banner/dialog with the reason (referencing the specific question/criterion) and two actions — "End & View Summary" (calls `POST /{sessionId}/end`, then navigates to the Summary page) and "Continue Screening" (dismiss and proceed to the next question as normal).
- Keep the required disclaimer text visible alongside the banner: "This tool provides AI-assisted pre-screening guidance only. Final eligibility must be confirmed by qualified clinical staff."
- Refresh the AI-fallback status banner (`GET /api/health/ai`) after each answer submission, consistent with the existing refresh points (after criteria extraction/question-bank/summary generation), since this feature adds a new Claude call site.
- Update `src/api/` client functions for the new response fields and the new `end` endpoint.

#### Files / Areas Likely Touched
`frontend/src/pages/ScreeningSession*` (or equivalent), `frontend/src/api/*`, relevant styles.

#### Verification Steps
- Manually run the age-16-vs-18-75 scenario end-to-end in the browser: answer the age question with `16`, confirm the recommendation banner appears with correct reasoning.
- Confirm "Continue Screening" dismisses the banner and lets the recruiter keep answering.
- Confirm "End & View Summary" navigates to a correct, complete summary page.
- Confirm the AI-fallback banner still behaves correctly when Claude is unavailable during this flow.

#### Expected Result
A recruiter sees a clear, actionable recommendation the moment a disqualifying answer is given, with full discretion to continue or end.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 17 only: frontend early-stop recommendation UI.

Scope for this phase:
- Update the screening session page to show a banner/dialog when earlyStopRecommended is true, with the reason and two actions: End & View Summary (calls POST /{sessionId}/end then navigates to Summary) and Continue Screening (dismiss, proceed normally).
- Keep the required disclaimer text visible alongside the banner.
- Refresh the AI-fallback status banner after each answer submission.
- Update the API client for the new response fields and the new end endpoint.

Do not implement future phases yet (docs/demo/smoke-test updates are Phase 18).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (manual browser walkthrough of the age-16 scenario, both banner actions, AI-fallback banner behavior).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 18: Testing, demo scenarios, and documentation

#### Objective
Validate the feature against concrete demo scenarios — including the exact age-range example from the requirement — in both live-Claude and fallback modes, and update docs/smoke tests accordingly.

#### Tasks
- Add an explicit "early ineligibility via numeric range" demo scenario (age 16 vs required 18–75) to `sample-data/sample-patient-scenarios.json` and/or `DEMO.md`.
- Extend `smoke-test.sh` with a check that answering a disqualifying question returns `earlyStopRecommended: true` with non-empty reasoning, and that `POST /{sessionId}/end` followed by `GET /{sessionId}/summary` succeeds.
- Update `DEMO.md`'s existing "Likely Ineligible" walkthrough to reference the new early-stop banner and the continue-vs-end choice.
- Update `CODESETUP.md`/`README.md`'s hackathon-simplifications section to note the new targeted Claude call site and its fallback limitation (numeric-range disqualification is not caught by the fallback heuristic).
- Verify fallback mode end-to-end: with Claude unavailable, confirm the disqualification rule still triggers correctly for cases the existing yes/no heuristic can classify (e.g., "no" to a required-inclusion question), confirm the AI-evaluation audit events show `fallback-rule`, and confirm nothing crashes.

#### Files / Areas Likely Touched
`sample-data/sample-patient-scenarios.json`, `DEMO.md`, `CODESETUP.md`, `README.md`, `smoke-test.sh`.

#### Verification Steps
- Run `./smoke-test.sh` and confirm all checks pass, including the new early-stop checks.
- Walk the age-16-vs-18-75 scenario plus the existing 3 demo scenarios end-to-end.
- Confirm `AuditEvents` contains the new AI-evaluation event type for both the live-Claude and fallback runs.

#### Expected Result
The feature is demonstrable, documented, covered by the smoke test, and behaves correctly (with a clearly documented fallback limitation) in both live and fallback modes.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, and plan2.md for project rules and context. Implement Phase 18 only: testing, demo scenarios, and documentation for AI-assisted early-stop screening.

Scope for this phase:
- Add an "early ineligibility via numeric range" (age 16 vs 18-75) demo scenario to sample-data/sample-patient-scenarios.json and/or DEMO.md.
- Extend smoke-test.sh with checks for earlyStopRecommended on a disqualifying answer and for the end/summary flow.
- Update DEMO.md's Likely Ineligible walkthrough and CODESETUP.md/README.md's hackathon-simplifications section to document this feature and its fallback-mode limitation.
- Verify fallback-mode behavior end-to-end without crashing, and confirm audit events are recorded for both live and fallback evaluation paths.

Do not add new application features beyond what phases 13-17 implemented.
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (smoke-test.sh, full scenario walkthroughs, AuditEvents check).
- List changed files.
- List assumptions.
- List blockers, if any.
```

## 6. Verification Checklist
- Fresh `docker compose down -v && docker compose up --build` picks up new `ScreeningAnswer`/`ScreeningSession` columns.
- `POST /api/screening-sessions/{id}/answers` on a disqualifying answer returns `earlyStopRecommended: true` with a reason.
- `GET /api/screening-sessions/{id}` and `GET /{id}/next-question` reflect the same recommendation state.
- `POST /api/screening-sessions/{id}/end` completes the session at any point; `GET /{id}/summary` succeeds afterward.
- `AuditEvents` table contains the new AI-evaluation event type for both AI-driven and fallback-driven evaluations.
- `GET /api/health/ai` reflects fallback state correctly when the new evaluation call fails.

## 7. Test Scenarios

**Early ineligibility via numeric range (new — the requirement's example)**
- Input pattern: patient answers `16` to an age question linked to a criterion requiring age `18–75` (a Required-Inclusion or Exclusion criterion).
- Expected behavior: `AnswerEvaluationService` (Claude-assisted) classifies the criterion as `failed`; backend's fixed rule sets `EarlyStopRecommended = true` with reasoning referencing the age range; session remains `InProgress` and `next-question` still works if the recruiter chooses "Continue Screening."
- Expected recommendation: banner recommends ending; if ended, summary shows `Likely Ineligible` with the age criterion listed as failed and any never-answered questions listed as unanswered/missing information.
- UI verification: banner appears immediately after the age answer is submitted, with clear reasoning and both actions working.

**Early ineligibility via existing yes/no heuristic (fallback mode)**
- Input pattern: Claude is unavailable (missing/invalid key); patient answers "no" to a required-inclusion criterion question.
- Expected behavior: `AnswerEvaluationService` falls back to `DetermineMappedStatus`, which still correctly classifies this as `failed` (the existing heuristic already handles simple yes/no cases); backend rule still sets `EarlyStopRecommended = true`.
- Expected recommendation: same banner and behavior as the AI-assisted path, confirming graceful degradation.
- UI verification: AI-fallback status banner is visible; early-stop banner still appears correctly.

**Non-disqualifying needs-review answer (regression check)**
- Input pattern: an uncertain/ambiguous answer on a criterion requiring clinical judgment (as in `plan.md`'s existing "Needs Clinical Review" scenario).
- Expected behavior: criterion marked `needs_review`; `EarlyStopRecommended` stays `false`; session proceeds exactly as before this plan's changes.
- Expected recommendation: no early-stop banner shown; session completes normally with `Needs Clinical Review`.
- UI verification: confirms this plan introduced no regression to the existing needs-review flow from `plan.md`.

**Cross-cutting checks**
- Non-`CanTriggerEarlyStop` questions never trigger the new Claude call (verifiable via absence of the new audit event type for those answers).
- The session never auto-completes or blocks `next-question` on its own — only an explicit `POST /{sessionId}/end` call changes `session.Status`.
- Every AI evaluation call (success or fallback) produces exactly one new audit event.

## 8. Risks and Mitigations
- **Claude insufficient credits / unavailable during evaluation** → `AnswerEvaluationService` falls back to the existing `DetermineMappedStatus` heuristic; `AiStatusService` flips to fallback; audit event still recorded with `fallback-rule`; documented limitation that numeric-range cases won't be caught in fallback mode.
- **Invalid/unparseable Claude response** → defensive JSON parsing (`TryGetProperty`) consistent with existing services; any parse failure treated identically to a Claude outage (fallback path).
- **Over-triggering AI calls (cost/latency)** → scoped strictly to `CanTriggerEarlyStop` questions via existing question-bank metadata; no call on demographic-only or non-disqualifying criteria questions.
- **Recruiter confusion about forced completion** → explicitly "recommend, don't force": session status and next-question behavior are unchanged by the recommendation; only an explicit recruiter action (`POST /.../end`) changes session state.
- **Schema drift on existing local dev databases** → new columns only apply via a fresh `EnsureCreatedAsync` run; documented requirement to `docker compose down -v` when picking up this plan's changes, consistent with the project's no-migrations simplification.
- **Summary generation on an early-ended session with unanswered questions** → `GET /{sessionId}/summary` must be verified (Phase 16) to report unanswered questions as `missingInformation` rather than failing.

## 9. Definition of Done
- [ ] `ScreeningAnswer`/`ScreeningSession` carry the new AI-evaluation and early-stop fields, auto-created via `EnsureCreatedAsync`.
- [ ] `AnswerEvaluationService` correctly classifies the age-16-vs-18-75 example as `failed` when Claude is available, with reasoning.
- [ ] The AI evaluation call fires only for `CanTriggerEarlyStop`-linked answers; all other answers are unaffected.
- [ ] The backend's fixed disqualification rule sets `EarlyStopRecommended`/`EarlyStopReason`/`DisqualifyingCriterionId` correctly and independently of Claude's own judgment about whether to stop.
- [ ] The session never auto-completes or blocks further questions on its own; `POST /{sessionId}/end` is the only way to end early.
- [ ] `AnswerResponse`, `NextQuestionResponse`, and session-status responses all surface the recommendation consistently.
- [ ] The frontend shows a clear, actionable banner with reasoning and both "End & View Summary" / "Continue Screening" actions, alongside the required disclaimer.
- [ ] Fallback mode (Claude unavailable) degrades gracefully without crashing, correctly handling at least the cases the existing yes/no heuristic already supports.
- [ ] Every AI evaluation call (success or fallback) is recorded in `AuditEvents`.
- [ ] `smoke-test.sh`, `DEMO.md`, `CODESETUP.md`/`README.md` are updated to cover this feature and its documented fallback limitation.
- [ ] All three original `plan.md` demo scenarios (Likely Eligible, Likely Ineligible, Needs Clinical Review) still pass unchanged, confirming no regression.
