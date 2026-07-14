# Implementation Plan 3 — Sex/Gender-Relevant Question Filtering

Reference: `prompt.txt` (full requirements), `CLAUDE.md` (standing rules), `plan.md` (original 13-phase plan, phases 0–12), and `plan2.md` (AI-assisted early-stop addendum, phases 13–18). This is a second addendum that fixes a real gap: sex/gender-irrelevant questions (e.g. pregnancy) are currently asked to every patient regardless of sex. Phases here are numbered 19–24 to continue the sequence; each phase is independently verifiable and must not regress any earlier phase's behavior.

## 1. Problem Statement

Today there is **no sex/gender data captured or used anywhere in the running system**. Confirmed by direct inspection:

- `EligibilityCriterion` and `ScreeningQuestion` have no field expressing "this only applies to patients of sex X."
- The sample criterion `EXC-002` ("Are you currently pregnant, breastfeeding, or planning to become pregnant during the study period?") is asked to every patient, including male patients, because nothing distinguishes it from a sex-neutral criterion.
- The only trace of this concern anywhere in the codebase is a single prose hint inside `QuestionBankService`'s Claude prompt ("sex-at-birth when explicitly required") — it has no backing data model field, no fallback generation, and nothing downstream ever reads a sex/gender answer to skip anything.
- `AdaptiveQuestionService`'s real status enum is only `unanswered | satisfied | failed | needs_review`. `CLAUDE.md` already documents `skipped-by-dedup` and `skipped-by-adaptive-logic` as required per-criterion statuses, but **neither exists in code today** — this plan is the first thing to actually implement `skipped-by-adaptive-logic`, using it for exactly this purpose.
- If a sex-irrelevant question were simply left unanswered instead, `ScreeningSessionService.GenerateSummaryAsync` would misreport it under `missingInformation` with "not answered before the session ended" — actively misleading, since it's not missing, it's irrelevant.

## 2. Scope

**In scope**
- A new `AppliesToSex` classification on `EligibilityCriterion` ("Male" | "Female" | null/omitted = applies to everyone), set at criteria-extraction time by Claude, with a deterministic keyword-based backstop and an explicit fixture update for the sample protocol's pregnancy criterion.
- A synthesized "Sex assigned at birth" demographic question (Male / Female / Intersex or Other), generated **only when at least one approved criterion needs it** — both in the Claude-generated path and the deterministic fallback path.
- A new, real `not_applicable` criterion status (implementing `CLAUDE.md`'s previously-unimplemented `skipped-by-adaptive-logic`) that deterministically skips a sex-irrelevant criterion's dedicated question entirely — it is never shown to the recruiter, never counted as missing information, and never affects the eligibility rollup.
- Conservative gating: a criterion is only skipped on a **clear, confident mismatch** (e.g. patient answered "Male" and the criterion applies to "Female" only). Any ambiguous case (unanswered sex, "Intersex or Other") leaves the criterion's question in the normal flow rather than silently skipping it.
- Summary/export reporting of not-applicable criteria as their own distinct bucket, and audit trail coverage.
- Frontend surfacing (criteria review badges, question bank badges, summary section) and doc/demo/smoke-test updates.

**Out of scope**
- Protocol-level sex-restricted enrollment (e.g., a trial that only accepts female participants as a hard eligibility rule) is **not** a new mechanism this plan builds — it is incidentally handled already: once the sex/gender demographic question exists, a criterion that directly asserts a required sex resolves through it like any other linked criterion (normal satisfied/failed evaluation), no different from how the age question already resolves `INC-001` today. This plan's new gating mechanism (`AppliesToSex` + `not_applicable`) is specifically for criteria that are *irrelevant* to one sex (pregnancy, prostate, etc.), not criteria that *require* one sex.
- No new Claude call during the live screening session. Sex applicability is determined once, at criteria-extraction time (same pattern as `SimpleMeaning`/`EligibilityImpact`/etc.); runtime gating is a plain deterministic comparison, consistent with `CLAUDE.md`'s "Claude assists with extraction... backend enforces the actual flow" rule.
- Reworking the existing age/consent/visit-availability demographic questions or the de-duplication (`RemoveDuplicateCriteriaQuestions`) mechanism — those are unaffected; the new sex question is additive.
- Handling every conceivable sex-specific medical condition exhaustively via the keyword backstop — it is a defensive net for a curated, documented list of common terms (pregnancy, prostate, etc.), not a medical NLP system.

**Hackathon simplifications** (to be documented in README/CODESETUP alongside the existing lists)
- The keyword-based backstop covers a curated term list; an unusual sex-specific criterion phrased without any listed keyword and not tagged correctly by Claude will simply keep today's behavior (asked to everyone) — a safe, non-regressive default, not a crash.
- The synthesized sex/gender question is generated with a fixed, well-known `QuestionId` (`DEM-SEX`) in both the Claude-prompted and fallback paths so runtime gating can reliably find the patient's answer; this is a deliberate simplification over a more general "find whichever question answers sex" search.

## 3. Architecture Summary / Integration Points

- **Extraction-time tagging, not runtime AI**: `CriteriaExtractionService` gains one more Claude-assisted field (`appliesToSex`) on the same JSON contract it already produces (`simpleMeaning`, `eligibilityImpact`, etc.), following the exact same defensive-parse pattern. A new deterministic keyword-based backstop (mirroring the "never trust Claude's dedup claim alone" defensive re-check already used in `QuestionBankService.RemoveDuplicateCriteriaQuestions`) fills in or corrects the tag when Claude omits or misses it.
- **Question-bank generation gains a conditional synthetic question**: `QuestionBankService` synthesizes the `DEM-SEX` question — in both the Claude-prompted contract and `BuildDeterministicFallback` — only when `approvedCriteria.Any(c => c.AppliesToSex is "Male" or "Female")`. No approved criterion needs it → no question is added, and nothing else changes.
- **Runtime gating lives entirely in `AdaptiveQuestionService.ComputeState`**, the same deterministic, backend-controlled state machine that already handles demographic de-duplication and the plan2.md disqualification rule. It looks up the patient's `DEM-SEX` answer (if asked) and marks any criterion whose `AppliesToSex` clearly conflicts as `not_applicable`, before that criterion's own question is ever considered as a candidate "next question."
- **Reporting flows through the same summary/export/audit pipeline** already built in `plan.md` Phase 10 and extended in `plan2.md` Phase 15 — a new bucket is added alongside `satisfied/failed/needsReview/skipped/missingInformation`, not a parallel system.

## 4. Key Design Decisions

- **Conditional sex question, not universal** (per user decision): the `DEM-SEX` question is synthesized only when at least one approved criterion is tagged `AppliesToSex: Male|Female`. Protocols with no sex-specific criteria are entirely unaffected — no extra question, no behavior change.
- **Three-option answer, conservative gating** (per user decision): `Male | Female | Intersex or Other`. A criterion is skipped (`not_applicable`) only on a clear, confident mismatch between the patient's answer and the criterion's `AppliesToSex`. An ambiguous answer, or no answer yet, never triggers a skip — the criterion's question stays in the normal flow. This mirrors the project's existing "when uncertain, don't guess, ask/flag" guardrail (the same principle behind `RequiresClinicalReview` forcing `needs_review` even on an apparent pass).
- **`not_applicable` implements `CLAUDE.md`'s documented-but-missing `skipped-by-adaptive-logic` status** — this plan is the first to give that status a real implementation, rather than inventing a new, undocumented status name.
- **Two distinct concepts kept separate**: a criterion that *requires* a specific sex (protocol-level eligibility) is a normal criterion resolved by the `DEM-SEX` answer like any other demographic-covered criterion (unchanged mechanism). A criterion that is merely *irrelevant* to one sex (pregnancy, prostate) is the new `AppliesToSex` + `not_applicable` gating mechanism. Conflating them would incorrectly turn "irrelevant to you" into "you failed eligibility," or vice versa.
- **Defense in depth on tagging**: Claude tags `appliesToSex` at extraction time; a deterministic keyword backstop (curated term list: pregnan*, breastfeed*, lactat*, menstrua*, ovarian, uterus/uterine, prostate, testic*, semen, ejaculat*, erectile) fills gaps or corrects obvious misses, the same defensive posture already used for de-duplication claims.
- **No new session-time Claude call**: gating is a plain equality/mismatch check against a field set once at extraction time — fully backend-deterministic at runtime, consistent with the existing architecture rule that Claude never controls session flow directly.

## 5. Implementation Phases

### Phase 19: `AppliesToSex` field, extraction-time tagging, and keyword backstop

#### Objective
Add the data model field and extraction-time logic (Claude-assisted + deterministic backstop) needed to classify which criteria are only relevant to one sex, without changing any existing behavior for criteria that aren't sex-specific.

#### Tasks
- Add `AppliesToSex` (string?, "Male" | "Female" | null) to `EligibilityCriterion`.
- Extend `CriteriaExtractionService.BuildSystemPrompt`/`BuildUserPrompt`'s JSON contract with an `appliesToSex` field ("Male" | "Female" | "All"), with explicit instructions that this should reflect biological/reproductive relevance (pregnancy, prostate, etc.), not the protocol's overall eligible population.
- Update `MapCriterion` to read `appliesToSex` (default null/"All" → treated as applies-to-everyone; no change in behavior for anything not explicitly tagged).
- Add a deterministic keyword-based backstop (e.g. a small `SexApplicabilityHeuristics` helper) scanning `OriginalText`/`SimpleMeaning`/`PatientQuestion` for a curated term list, applied after both the Claude-parsed path and the sample-data fallback path, to fill in or correct `AppliesToSex` — mirroring the existing "never trust Claude's claim alone" defensive re-check pattern in `QuestionBankService.RemoveDuplicateCriteriaQuestions`.
- Update `sample-data/sample-criteria.json`'s `EXC-002` (pregnancy/breastfeeding) entry with `"appliesToSex": "Female"` explicitly.

#### Files / Areas Likely Touched
`Models/EligibilityCriterion.cs`, `Services/CriteriaExtractionService.cs`, new `Services/SexApplicabilityHeuristics.cs` (or similar), `sample-data/sample-criteria.json`.

#### Verification Steps
- Extract criteria (live Claude) from the sample protocol; confirm `EXC-002` comes back tagged `appliesToSex: "Female"`.
- Temporarily strip `appliesToSex` from a mocked Claude response containing pregnancy-related text; confirm the keyword backstop still tags it `"Female"`.
- Confirm every other criterion (age, consent, visit availability, Type 2 Diabetes duration, etc.) remains untagged (`null`/"All") and behaves exactly as before.
- Force Claude fallback (`sample-criteria.json` path) and confirm `EXC-002` still carries `appliesToSex: "Female"` from the fixture.

#### Expected Result
Every criterion has an accurate, defensively-verified `AppliesToSex` classification, with zero behavior change for non-sex-specific criteria.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 19 only: AppliesToSex field, extraction-time tagging, and keyword backstop.

Scope for this phase:
- Add AppliesToSex (string?, "Male" | "Female" | null) to EligibilityCriterion.
- Extend CriteriaExtractionService's Claude prompt/JSON contract with an appliesToSex field ("Male" | "Female" | "All"), and update MapCriterion to parse it (default null/"All" when absent).
- Add a deterministic keyword-based backstop that fills in or corrects AppliesToSex from a curated term list (pregnancy, breastfeeding, lactation, menstruation, ovarian, uterine, prostate, testicular, semen, ejaculatory, erectile, etc.) scanning originalText/simpleMeaning/patientQuestion, applied after both the Claude-parsed path and the sample-data fallback path.
- Update sample-data/sample-criteria.json's EXC-002 entry with "appliesToSex": "Female".

Do not implement future phases yet (no question-bank changes, no runtime gating, no summary/frontend changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (extract criteria with a valid key and confirm EXC-002 tagging; simulate a missing appliesToSex field and confirm the keyword backstop still tags it; confirm untagged criteria are unaffected).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 20: Conditional "Sex assigned at birth" demographic question

#### Objective
Synthesize a `DEM-SEX` demographic question — in both the Claude-prompted question bank and the deterministic fallback — but only when at least one approved criterion actually needs sex-based gating.

#### Tasks
- Extend `QuestionBankService.BuildSystemPrompt`/`BuildUserPrompt` to instruct Claude: if any approved criterion has `appliesToSex` of `"Male"` or `"Female"`, include exactly one Demographics question with a fixed `questionId` of `"DEM-SEX"`, `answerType: "single_choice"`, `options: ["Female", "Male", "Intersex or Other"]`, placed at the lowest `displayOrder` in Demographics (asked first, before any sex-gated question).
- Update `BuildDeterministicFallback` to synthesize the same `DEM-SEX` question deterministically (not derived from a single criterion's `CanBeCoveredByDemographics`, since no single criterion "is" the sex question) whenever `approvedCriteria.Any(c => c.AppliesToSex is "Male" or "Female")`; renumber other Demographics questions' `displayOrder` so `DEM-SEX` is always first.
- Confirm this question's `CoveredCriteriaJson`/`LinkedCriteriaJson` stay empty by default — it is a supporting data point for other criteria's gating, not a direct resolution of any criterion (see plan3.md §4 "two distinct concepts kept separate").
- Confirm zero behavior change (no `DEM-SEX` question generated at all) when no approved criterion needs it.

#### Files / Areas Likely Touched
`Services/QuestionBankService.cs`.

#### Verification Steps
- Generate the question bank for the sample protocol (which has `EXC-002` tagged `Female`); confirm `DEM-SEX` appears first in the Demographics section, with the three expected options, in both live-Claude and fallback modes.
- Temporarily approve only criteria with no `AppliesToSex` tag; confirm `DEM-SEX` is **not** generated.
- Confirm existing Demographics questions (age, consent, visit availability) are unaffected beyond `displayOrder` renumbering.

#### Expected Result
The sex/gender question appears exactly when needed, always first in Demographics, and is absent entirely when no criterion requires it.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 20 only: conditional "Sex assigned at birth" demographic question.

Scope for this phase:
- Extend QuestionBankService's Claude prompt/JSON contract: when any approved criterion has appliesToSex of "Male" or "Female", generate exactly one Demographics question with questionId "DEM-SEX", answerType "single_choice", options ["Female", "Male", "Intersex or Other"], at the lowest displayOrder in Demographics.
- Update BuildDeterministicFallback to synthesize the same DEM-SEX question deterministically under the same condition, renumbering other Demographics displayOrder values so DEM-SEX is first.
- Leave DEM-SEX's linkedCriteria/coveredCriteria empty — it supports gating for other criteria, it does not resolve any criterion itself.
- When no approved criterion needs sex-based gating, do not generate this question at all.

Do not implement future phases yet (no runtime gating logic, no summary/frontend changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (generate question bank for the sample protocol in both live-Claude and fallback modes, confirm DEM-SEX first in Demographics; confirm it's absent when no criterion needs it).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 21: Deterministic runtime relevance gating (`not_applicable` status)

#### Objective
Implement `CLAUDE.md`'s previously-undelivered `skipped-by-adaptive-logic` status: when the patient's `DEM-SEX` answer clearly conflicts with a criterion's `AppliesToSex`, that criterion resolves to `not_applicable` and its dedicated question is never offered — deterministically, in the same backend-controlled state machine that already handles de-duplication and the plan2.md disqualification rule.

#### Tasks
- Add `"not_applicable"` as a real criterion status value in `AdaptiveQuestionService` (`CriterionState.Status`), documented as implementing `CLAUDE.md`'s `skipped-by-adaptive-logic`.
- In `ComputeState`, look up the patient's answer to the fixed `DEM-SEX` question ID (if asked) before evaluating other criteria. For any criterion whose `AppliesToSex` is a **clear, confident mismatch** against that answer (patient answered "Male" and the criterion applies to "Female" only, or vice versa), mark that criterion `not_applicable` rather than evaluating its own linked question.
- Conservative default: if `DEM-SEX` hasn't been answered yet, or was answered `"Intersex or Other"`, no criterion is skipped — normal flow continues unchanged.
- Update the `remaining` (next-question candidate) filter to also exclude any question whose every linked criterion is already `not_applicable` — this is the actual mechanism that stops the pregnancy question from ever reaching a male patient.
- Update `DetermineOverallStatus` to treat `not_applicable` like `satisfied` for eligibility rollup and "all answered" completeness purposes — it must never contribute to `Likely Ineligible` or `Needs Clinical Review`, and must not keep a session artificially "Pending."

#### Files / Areas Likely Touched
`Services/AdaptiveQuestionService.cs`.

#### Verification Steps
- Start a session for the sample protocol; answer `DEM-SEX` with "Male"; confirm the pregnancy question (linked to `EXC-002`) never appears as `next-question`, and `EXC-002`'s resolved status is `not_applicable`.
- Repeat answering `DEM-SEX` with "Female"; confirm the pregnancy question **is** asked normally, resolved via the existing yes/no heuristic exactly as before this plan.
- Repeat answering `DEM-SEX` with "Intersex or Other"; confirm the pregnancy question is still asked (conservative default — no skip on ambiguous input).
- Confirm a session where `DEM-SEX` is never generated at all (no sex-specific criteria in the protocol) behaves identically to today.
- Confirm `not_applicable` never appears as `Likely Ineligible`/`Needs Clinical Review` trigger and a session with only `satisfied` + `not_applicable` criteria reaches `Likely Eligible`.

#### Expected Result
A male patient is never asked the pregnancy question; a female patient's screening is unaffected; ambiguous sex answers default to the safer "ask anyway" path; overall eligibility rollup is unaffected by not-applicable criteria.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 21 only: deterministic runtime relevance gating (not_applicable status).

Scope for this phase:
- Add "not_applicable" as a real status value in AdaptiveQuestionService's CriterionState, implementing CLAUDE.md's documented skipped-by-adaptive-logic concept.
- In ComputeState, look up the patient's answer to the fixed DEM-SEX question ID (if asked) and mark any criterion as not_applicable only on a clear, confident mismatch against its AppliesToSex tag (Male vs Female binary opposite). Leave criteria untouched (normal flow) if DEM-SEX is unanswered or answered "Intersex or Other".
- Update the remaining-questions filter so a question whose every linked criterion is already not_applicable is excluded from being offered as the next question.
- Update DetermineOverallStatus so not_applicable behaves like satisfied for eligibility rollup and completeness purposes.

Do not implement future phases yet (no summary/export/audit changes, no frontend changes).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (answer DEM-SEX as Male/Female/Intersex-or-Other and confirm the pregnancy question is skipped only in the Male case; confirm overall-status rollup treats not_applicable like satisfied; confirm protocols without DEM-SEX are unaffected).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 22: Summary, export, and audit trail for not-applicable criteria

#### Objective
Report `not_applicable` criteria as their own distinct, clearly-labeled bucket — never conflated with `missingInformation` (which means "the session ended before this was asked", not "this doesn't apply to you") — and record an audit trail entry when a criterion first resolves this way.

#### Tasks
- Add a `NotApplicableCriteria` list to `SummaryResult` (and the persisted `ScreeningSummary` row / `SummaryResponse` DTO), populated from criteria with status `not_applicable`, kept separate from `SkippedCriteria`/`MissingInformation`.
- Update `ScreeningSessionService.GenerateSummaryAsync`'s bucketing so a `not_applicable` criterion never lands in `missingInformation`'s "not answered before the session ended" wording.
- Update the Claude-assisted summary prompt (`BuildNarrativeAsync`) and the rule-based fallback narrative to mention not-applicable criteria appropriately (e.g. "EXC-002 was not applicable based on the patient's reported sex and was not asked.").
- Add an audit event (e.g. `"CriterionNotApplicable"`) via `AuditService` the first time a criterion resolves to `not_applicable` within a session, alongside the existing de-duplication/answer audit events.

#### Files / Areas Likely Touched
`Services/ScreeningSessionService.cs`, `Models/ScreeningSummary.cs`, `Controllers/ScreeningSessionsController.cs` (`SummaryResponse`), `Services/AuditService.cs` call sites.

#### Verification Steps
- Complete a male-patient session; confirm the summary's `notApplicableCriteria` lists `EXC-002` and `missingInformation`/`skippedCriteria` do **not** include it.
- Confirm the summary narrative (both live-Claude and rule-based fallback) references the not-applicable criterion in plain language rather than implying it was overlooked.
- Confirm `AuditEvents` contains a `CriterionNotApplicable` row for the session.
- Confirm `export/json` and `export/csv` include the new bucket.

#### Expected Result
A recruiter reading the summary can clearly distinguish "not asked because irrelevant to this patient" from "not asked because the session ended early," with full audit coverage.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 22 only: summary, export, and audit trail for not-applicable criteria.

Scope for this phase:
- Add a NotApplicableCriteria bucket to SummaryResult/ScreeningSummary/SummaryResponse, populated from not_applicable-status criteria, kept separate from SkippedCriteria/MissingInformation.
- Update GenerateSummaryAsync's bucketing so not_applicable criteria never appear in missingInformation.
- Update the Claude-assisted narrative prompt and rule-based fallback narrative to describe not-applicable criteria in plain language.
- Add a "CriterionNotApplicable" audit event the first time a criterion resolves to not_applicable in a session.

Do not implement future phases yet (no frontend changes, no docs/demo/smoke-test updates).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (complete a male-patient session, check summary buckets, check narrative wording, check AuditEvents, check exports).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 23: Frontend updates

#### Objective
Surface sex-applicability information to the recruiter during criteria review and question bank preview, and show the new not-applicable bucket on the Summary page.

#### Tasks
- `client.ts`: add `appliesToSex` to the `Criterion` and `ScreeningQuestion` interfaces; add `notApplicableCriteria` to `SessionSummary`.
- `ProtocolWorkspace.tsx` (`CriterionRow`): show an "Applies to: Female" / "Applies to: Male" badge when `appliesToSex` is set, alongside the existing Inclusion/Exclusion and priority badges.
- `QuestionBankPreview.tsx` (`QuestionCard`): show the same badge on sex-specific questions.
- `SummaryPage.tsx`: add a "Not Applicable to This Patient" card listing `notApplicableCriteria`, distinct from the existing Satisfied/Failed/Needs-Review/Covered-by-Demographics/Skipped lists.
- No changes needed to `ScreeningSessionPage.tsx` — the backend simply never returns a skipped question as `next-question`.

#### Files / Areas Likely Touched
`frontend/src/api/client.ts`, `frontend/src/pages/ProtocolWorkspace.tsx`, `frontend/src/pages/QuestionBankPreview.tsx`, `frontend/src/pages/SummaryPage.tsx`.

#### Verification Steps
- Extract criteria for the sample protocol; confirm the pregnancy criterion shows an "Applies to: Female" badge in the Criteria Review page.
- Generate the question bank; confirm the same badge appears on the corresponding question card, and `DEM-SEX` appears first in the Demographics list.
- Run a male-patient session to completion; confirm the Summary page's new "Not Applicable to This Patient" card lists the pregnancy criterion and the existing lists do not duplicate it.

#### Expected Result
Recruiters can see at a glance which criteria are sex-specific during review, and why a criterion wasn't asked about during summary review.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 23 only: frontend updates for sex/gender-relevant question filtering.

Scope for this phase:
- Add appliesToSex to the Criterion and ScreeningQuestion TypeScript interfaces in client.ts, and notApplicableCriteria to SessionSummary.
- Show an "Applies to: Female"/"Applies to: Male" badge on CriterionRow (ProtocolWorkspace.tsx) and QuestionCard (QuestionBankPreview.tsx) when appliesToSex is set.
- Add a "Not Applicable to This Patient" card to SummaryPage.tsx listing notApplicableCriteria, distinct from the existing satisfied/failed/needs-review/covered-by-demographics/skipped lists.

Do not implement future phases yet (docs/demo/smoke-test updates are Phase 24).
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (criteria review badge, question bank badge, male-patient summary walkthrough).
- List changed files.
- List assumptions.
- List blockers, if any.
```

---

### Phase 24: Testing, demo scenario, and documentation

#### Objective
Validate the feature end-to-end with a concrete male-patient demo scenario, extend the smoke test, and document the new behavior and its known limitations.

#### Tasks
- Add a new demo patient scenario ("Patient-Echo" or similar) to `sample-data/sample-patient-scenarios.json`: a male patient who answers `DEM-SEX: Male` and never sees the pregnancy question, reaching an otherwise-normal recommendation.
- Update `DEMO.md` with a walkthrough showing `DEM-SEX` asked first, the pregnancy question silently absent for a male patient, and the Summary page's "Not Applicable to This Patient" section.
- Extend `smoke-test.sh`: after generating the question bank, answer `DEM-SEX` with "Male" and confirm the pregnancy-linked question never appears in subsequent `next-question` responses; confirm the summary's `notApplicableCriteria` includes it.
- Update `README.md`/`CODESETUP.md`'s hackathon-simplifications sections with a note on the keyword backstop's curated-term-list limitation.

#### Files / Areas Likely Touched
`sample-data/sample-patient-scenarios.json`, `DEMO.md`, `smoke-test.sh`, `README.md`, `CODESETUP.md`.

#### Verification Steps
- Run `./smoke-test.sh` and confirm all checks pass, including the new sex-gating checks.
- Manually walk the new male-patient demo scenario end-to-end in the browser.
- Confirm all three original `plan.md` demo scenarios and the `plan2.md` early-stop scenario still pass unchanged, confirming no regression.

#### Expected Result
The feature is demonstrable, documented, covered by the smoke test, and does not regress any earlier scenario.

#### Claude Implementation Prompt
```text
Read prompt.txt, CLAUDE.md, plan.md, plan2.md, and plan3.md for project rules and context. Implement Phase 24 only: testing, demo scenario, and documentation for sex/gender-relevant question filtering.

Scope for this phase:
- Add a male-patient demo scenario to sample-data/sample-patient-scenarios.json exercising DEM-SEX: Male and confirming the pregnancy question is never asked.
- Update DEMO.md with a walkthrough of this scenario.
- Extend smoke-test.sh to answer DEM-SEX as Male and confirm the pregnancy-linked question never appears as next-question, and that the summary's notApplicableCriteria includes it.
- Update README.md/CODESETUP.md hackathon-simplifications sections with the keyword-backstop limitation.

Do not add new application features beyond what phases 19-23 implemented.
Do not rewrite unrelated files unless required.
Preserve existing behavior.
Keep token usage efficient.

After implementation:
- Run or describe verification steps (smoke-test.sh, male-patient scenario walkthrough, regression check on all prior demo scenarios).
- List changed files.
- List assumptions.
- List blockers, if any.
```

## 6. Verification Checklist
- Criteria extraction (live Claude) tags `EXC-002` as `appliesToSex: "Female"`; fallback mode does too (from the fixture).
- Question bank generation adds `DEM-SEX` first in Demographics only when a sex-specific criterion exists; omits it entirely otherwise — in both live-Claude and fallback modes.
- A male patient answering `DEM-SEX: Male` never sees the pregnancy question; a female patient does, unaffected.
- `DEM-SEX: Intersex or Other` (or unanswered) never skips any criterion.
- `not_applicable` criteria never appear under `missingInformation`/`skippedCriteria`, and never affect `overallLikelyStatus` rollup.
- `AuditEvents` records a `CriterionNotApplicable` row when applicable.
- Summary/export show a distinct not-applicable bucket; frontend shows sex-applicability badges during review.

## 7. Test Scenarios

**Male patient — pregnancy criterion silently skipped (new)**
- Input pattern: patient answers `DEM-SEX: Male`, then proceeds through the rest of the session normally (otherwise eligible answers).
- Expected behavior: the pregnancy question is never offered; `EXC-002` resolves to `not_applicable`; every other criterion is evaluated exactly as before.
- Expected recommendation: `Likely Eligible` (assuming all other criteria pass) — `not_applicable` never drags the result toward ineligible or needs-review.
- UI verification: Summary page's "Not Applicable to This Patient" card lists `EXC-002`; it does not appear under missing information or skipped-due-to-early-end.

**Female patient — pregnancy criterion asked normally (regression check)**
- Input pattern: patient answers `DEM-SEX: Female`, then answers the pregnancy question as in the existing `plan.md` scenarios.
- Expected behavior: identical to today's behavior before this plan — the question is asked, answered, and resolved via the existing yes/no heuristic.
- UI verification: confirms zero regression for the sex that the criterion does apply to.

**Ambiguous sex answer — conservative default (new)**
- Input pattern: patient answers `DEM-SEX: Intersex or Other`.
- Expected behavior: the pregnancy question is still asked (no skip on ambiguous input); resolved normally based on the patient's actual answer.
- UI verification: confirms the "only skip on a clear, confident mismatch" rule holds.

**Protocol with no sex-specific criteria (regression check)**
- Input pattern: an approved criteria set where no criterion has `appliesToSex` set.
- Expected behavior: `DEM-SEX` is never generated; the session flow is byte-for-byte identical to before this plan.
- UI verification: no sex-related question or badge appears anywhere.

**Cross-cutting checks**
- All three original `plan.md` demo scenarios (Likely Eligible, Likely Ineligible, Needs Clinical Review) and the `plan2.md` early-stop scenario still pass unchanged.
- `not_applicable` is never reachable for a criterion with no `AppliesToSex` tag.

## 8. Risks and Mitigations
- **Claude fails to tag `appliesToSex` on a real sex-specific criterion** → the deterministic keyword backstop catches common cases (pregnancy, prostate, etc.); anything outside the curated term list falls back to today's safe default (question always asked to everyone) — a missed optimization, never a crash or incorrect eligibility result.
- **Over-aggressive skipping hides a criterion the recruiter actually needed to see** → mitigated by the conservative "only skip on a clear, confident mismatch" rule, plus full visibility via the Summary page's dedicated "Not Applicable to This Patient" section and the `CriterionNotApplicable` audit trail — nothing is silently dropped without a visible trace.
- **Confusing "not applicable" with "missing information" in the summary** → explicitly kept as separate buckets end-to-end (state → summary → export → UI), per Phase 22.
- **`DEM-SEX` question ID collision or ordering issues in the fallback path** → fixed, well-known `QuestionId` (`DEM-SEX`) and explicit lowest-`displayOrder` placement, verified in Phase 20/21's tests.
- **Regression on protocols without sex-specific criteria** → the entire mechanism is gated behind `approvedCriteria.Any(c => c.AppliesToSex is "Male" or "Female")`; when false, nothing about the existing flow changes, verified explicitly in every phase's test steps.

## 9. Definition of Done
- [ ] `EligibilityCriterion.AppliesToSex` is populated correctly (Claude-tagged + keyword-backstopped) for sex-specific criteria, and left null for everything else.
- [ ] `DEM-SEX` is generated first in Demographics exactly when needed, in both live-Claude and fallback question-bank generation, and never otherwise.
- [ ] A male patient is never asked the pregnancy question; a female patient's flow is unchanged; an ambiguous sex answer never triggers a skip.
- [ ] `not_applicable` criteria never affect `overallLikelyStatus` and are never reported as missing/skipped-due-to-early-end.
- [ ] Summary, export, and audit trail all correctly distinguish not-applicable criteria from every other status.
- [ ] Frontend shows sex-applicability badges during criteria/question review and a dedicated not-applicable section in the Summary page.
- [ ] `smoke-test.sh`, `DEMO.md`, `README.md`/`CODESETUP.md` are updated to cover this feature and its keyword-backstop limitation.
- [ ] All prior demo scenarios (`plan.md`'s three plus `plan2.md`'s early-stop scenario) still pass unchanged, confirming no regression.
