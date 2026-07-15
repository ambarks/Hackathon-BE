# Safety Note — PHI/PII Handling, Guardrails & Human Review

## 1. Data Handled
This system is a clinical trial **pre-screening** assistant used by a **recruiter**, not a patient self-service tool.

| Data | Collected? | Classification | Notes |
|---|---|---|---|
| Patient full name | ✅ Yes | PII | Needed so the recruiter can identify who is being screened and follow up. |
| Patient phone number | ✅ Yes | PII | Needed for the recruiter to contact the patient about next steps. |
| Protocol / eligibility criteria answers (age range, symptoms, yes/no health responses) | ✅ Yes | PHI (minimal, criterion-level only) | Answers are limited to what the approved criteria ask — not a full medical history or chart. |
| National ID, SSN, insurance ID, full DOB, home address, diagnosis codes, free-text medical notes | ❌ No | — | Not collected anywhere in the current data model or UI. |
| Real patient PDFs/protocols uploaded for testing | ⚠️ Caution | May contain PHI if a real, non-de-identified protocol/document is uploaded | Test/demo data should use de-identified or synthetic protocols; do not upload real patient records as "protocol" files. |

**No real, identifiable patient PHI (medical records, diagnoses, chart data) is required or requested by this application beyond the minimal name/phone identifiers and criterion-level yes/no responses above.**

## 2. Risks
| Risk | Description |
|---|---|
| Re-identification | Name + phone + health answers together are enough to re-identify a person if the database or exports leak. |
| Over-collection | Future changes could add free-text medical fields, expanding PHI exposure beyond what's needed. |
| AI misinterpretation | Claude (LLM) assists with extraction/question generation and could misclassify a criterion or answer. |
| Data export / at-rest exposure | JSON/CSV exports and the SQL database contain patient name, phone, and criterion answers in plaintext. |
| Third-party transmission | Protocol text and (indirectly) criteria are sent to the Claude API (Anthropic) for extraction/question generation. |

## 3. Guardrails in Place
- **Purpose limitation**: only name, phone, and criterion-scoped answers are collected — no diagnoses, no free-text medical history, no national/insurance IDs.
- **No autonomous medical/eligibility decisions**: system prompts explicitly instruct Claude *"Do not make final medical, diagnostic, or recruitment decisions"* and *"the final decision remains with the recruiter/clinical reviewer."* The app outputs a **recommendation** (e.g., "Likely Eligible / Likely Ineligible / Needs Review") only.
- **Human-in-the-loop is mandatory**: every session result is a decision-support recommendation for the recruiter to review and act on — not an automated accept/reject.
- **Clinical-review flagging**: criteria can be marked `requiresClinicalReview = true`, forcing explicit reviewer sign-off before treating that criterion as resolved.
- **Uncertainty is surfaced, not hidden**: prompts require Claude to say so when information is missing/uncertain rather than guessing.
- **Fallback safety net**: if the Claude API is unavailable, the system falls back to a deterministic rules engine or sample data and clearly labels the result as a fallback — it never silently fabricates a clinical judgment.
- **No secrets/PHI in logs**: API keys, DB passwords, and connection strings are never logged; only exception type/path is logged on errors.
- **Audit trail**: an `AuditService` records session actions (who/when/what) to support traceability and later review.
- **Transport security**: outbound API calls (including to Claude) are made over TLS.

## 4. Human Review Checkpoints
1. **Criteria approval** — a human approves/edits AI-extracted eligibility criteria before they are used to generate patient questions.
2. **Recruiter conducts screening** — the recruiter, not the patient, asks the questions and enters responses.
3. **Recruiter reviews final recommendation** — the system's "Likely Eligible/Ineligible/Needs Review" output is a suggestion; the recruiter (or clinical reviewer for flagged criteria) makes the actual enrollment decision.

## 5. Acceptance Check
- ✅ **No real PHI is required by the application** beyond minimal identifiers (patient name, phone) and criterion-scoped screening answers necessary for the recruiter to contact and screen the patient.
- ✅ **Risks are stated** (Section 2) and **controls/guardrails are stated and implemented in code** (Section 3), with **explicit human review checkpoints** (Section 4) before any eligibility decision is finalized.
