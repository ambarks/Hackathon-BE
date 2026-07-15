# Safety Note — PHI/PII Handling, Guardrails & Human Review (No-PII POC Scope)

## 1. Data Handled
This system is a clinical trial **pre-screening** assistant used by a recruiter for this **POC/demo**. Contacting the patient (obtaining and dialing their name/phone) is **out of scope** here and will be handled by the customer's existing systems/process in production — this POC does not need or store real patient identity data.

| Data | Collected? | Classification | Notes |
|---|---|---|---|
| Patient full name | ❌ No | — | Out of scope for this POC; only a non-identifying **patient alias/session ID** is used to track a screening session. |
| Patient phone number | ❌ No | — | Not needed — outreach/contact is a downstream, customer-owned process outside this POC. |
| Protocol / eligibility criteria answers (age range, symptoms, yes/no health responses) | ✅ Yes | PHI (minimal, criterion-level only) | Answers are limited to what the approved criteria ask, tied to an alias/session ID — not a name. |
| National ID, SSN, insurance ID, DOB, home address, diagnosis codes, free-text medical notes | ❌ No | — | Not collected anywhere in the current data model or UI. |
| Real patient PDFs/protocols uploaded for testing | ⚠️ Caution | May contain PHI if a real, non-de-identified protocol/document is uploaded | Use de-identified or synthetic protocols for demo/test; do not upload real patient records. |

**This POC collects no patient name, phone number, or other direct identifier. Screening sessions are tracked only by an alias/session ID, and answers are limited to criterion-level yes/no/choice responses.**

## 2. Risks
| Risk | Description |
|---|---|
| Indirect re-identification | Even without name/phone, a combination of criterion answers (e.g., rare condition + narrow age range) could narrow down a specific individual in small cohorts. |
| Alias collision or reuse | If an alias/session ID is reused or predictable, sessions could be mismatched to the wrong person once the customer joins the two systems downstream. |
| Scope creep at integration | When this POC is integrated with the customer's system (which does hold name/phone), the boundary between "criterion answers" and "identity" must be re-drawn carefully so PHI/PII doesn't merge without proper controls. |
| AI misinterpretation | Claude (LLM) assists with extraction/question generation and could misclassify a criterion or answer. |
| Data export / at-rest exposure | JSON/CSV exports and the SQL database contain the alias/session ID and criterion answers in plaintext. |
| Third-party transmission | Protocol text is sent to the Claude API (Anthropic) for extraction/question generation; no patient identity data is included in that payload. |

## 3. Guardrails in Place
- **No identity collection by design**: the POC's data model uses a patient **alias/session ID** only — no name or phone field is captured, stored, or displayed.
- **Purpose limitation**: only criterion-scoped screening answers are collected — no diagnoses, no free-text medical history, no national/insurance IDs.
- **No autonomous medical/eligibility decisions**: system prompts explicitly instruct Claude *"Do not make final medical, diagnostic, or recruitment decisions"* and *"the final decision remains with the recruiter/clinical reviewer."* The app outputs a **recommendation** (e.g., "Likely Eligible / Likely Ineligible / Needs Review") only.
- **Human-in-the-loop is mandatory**: every session result is decision support for the recruiter — not an automated accept/reject, and not a trigger to auto-contact the patient.
- **Clear handoff boundary**: actual patient contact (matching alias/session → real name/phone → outreach) is explicitly a downstream, customer-owned step outside this POC's scope, so this system never needs to hold that identity data.
- **Clinical-review flagging**: criteria can be marked `requiresClinicalReview = true`, forcing explicit reviewer sign-off before treating that criterion as resolved.
- **Uncertainty is surfaced, not hidden**: prompts require Claude to say so when information is missing/uncertain rather than guessing.
- **Fallback safety net**: if the Claude API is unavailable, the system falls back to a deterministic rules engine or sample data and clearly labels the result as a fallback — it never silently fabricates a clinical judgment.
- **No secrets/PHI in logs**: API keys, DB passwords, and connection strings are never logged; only exception type/path is logged on errors.
- **Audit trail**: an `AuditService` records session actions (who/when/what) by alias/session ID to support traceability and later review.
- **Transport security**: outbound API calls (including to Claude) are made over TLS.

## 4. Human Review Checkpoints
1. **Criteria approval** — a human approves/edits AI-extracted eligibility criteria before they are used to generate patient questions.
2. **Recruiter conducts screening** — the recruiter runs the alias/session-based screening; no patient identity is entered into this system.
3. **Recruiter reviews final recommendation** — the "Likely Eligible/Ineligible/Needs Review" output is a suggestion; the recruiter (or clinical reviewer for flagged criteria) makes the actual enrollment decision.
4. **Production handoff (outside this POC)** — the customer's own system is responsible for linking a screened alias/session to a real patient and performing outreach (name/phone), under whatever consent and PHI controls that production system already has in place.

## 5. Acceptance Check
- ✅ **No real PHI/PII is collected by this POC** — no patient name or phone number is captured; only an alias/session ID plus criterion-scoped screening answers.
- ✅ **Risks are stated** (Section 2) and **controls/guardrails are stated and implemented in code** (Section 3), with **explicit human review checkpoints** (Section 4), including a clearly marked boundary for where production patient-contact data would be introduced by the customer's own systems.
