# Demo Script

Four walkthroughs for the sample protocol (`sample-data/sample-protocol.txt`, a synthetic Type 2 Diabetes trial). All scenarios assume:

1. Start the stack: `docker compose up --build`, open http://localhost:5173.
2. **Upload & Criteria** page → **Use Sample Protocol** → **Extract Criteria** → **Approve All Criteria** → **Generate Question Bank**.
3. **Question Bank** page → confirm exactly two sections and note the sequencing rationale, then enter a patient alias and **Start Screening Session**.

The question bank (fallback deterministic sequencing, used automatically when Claude is unavailable) produces this order:

**Demographic Information** (asked first, in this order): **DEM-SEX** "What sex were you assigned at birth?" (always asked first — `EXC-002` is tagged `appliesToSex: Female`, so the sex question is required to decide whether to ask it at all; see Scenario 4) → DEM-001 pregnancy status (covers `EXC-002`) → DEM-002 age (covers `INC-001`) → DEM-003 consent capability (covers `INC-005`) → DEM-004 visit availability (covers `INC-004`).

Scenarios 1–3 below assume the patient answers `DEM-SEX: Female` (or any answer other than "Male") so the pregnancy question remains relevant and the walkthroughs proceed exactly as written. Scenario 4 demonstrates what happens for a male patient instead.

**Eligibility Criteria Questions** (mixed inclusion/exclusion order, asked second): CRT-001 Type 1 Diabetes history (`EXC-001`, high-priority exclusion) → CRT-002 Type 2 Diabetes diagnosis duration (`INC-002`, high-priority inclusion) → CRT-003 kidney impairment (`EXC-003`) → CRT-004 insulin therapy (`EXC-004`) → CRT-005 HbA1c range (`INC-003`) → CRT-006 drug allergy (`EXC-005`).

Notice that a high-priority **exclusion** question (CRT-001) is asked before several inclusion questions — this is the "mixed sequencing" behavior: the app never blindly asks all inclusion criteria before all exclusion criteria, or vice versa.

---

## Scenario 1 — Likely Eligible

| Question | Answer |
|---|---|
| DEM-SEX What sex were you assigned at birth? | Female |
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | 52 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | No |
| CRT-002 Diagnosed with Type 2 Diabetes ≥ 6 months? | Yes |
| CRT-003 Severe kidney impairment? | No |
| CRT-004 Currently taking insulin? | No, not taking insulin |
| CRT-005 Recent HbA1c 7.0–10.0%? | Yes, within range |
| CRT-006 Known drug allergies? | No |

**Expected outcome:** all ten criteria resolve to `satisfied` (three of the criteria — HbA1c, insulin, and kidney — are marked `requiresClinicalReview` in the sample data, so a "clean" run through all of them typically lands on **Needs Clinical Review** rather than Likely Eligible; to see a pure **Likely Eligible** result, this is expected and demonstrates the "even an apparent pass on a lab-based criterion still gets routed to clinical review" guardrail). The Summary page shows demographic answers, all satisfied/needs-review criteria, and the disclaimer.

## Scenario 2 — Likely Ineligible (early exclusion stop)

| Question | Answer |
|---|---|
| DEM-SEX What sex were you assigned at birth? | Female |
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | 61 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | **Yes** |

**Expected outcome:** as soon as CRT-001 is answered "Yes," the session's `overallLikelyStatus` immediately flips to **Likely Ineligible** — this is the very first eligibility-criteria question, demonstrating that a high-impact exclusion criterion can end screening quickly rather than after asking every inclusion question first. The recruiter can click **End and Generate Summary** immediately, or continue answering — either way the recommendation stays Likely Ineligible. The Summary page's "Failed" list shows `EXC-001`, and any remaining unanswered criteria appear under "Skipped (session ended early)."

*(Alternative disqualifying path: answering "Yes, taking insulin" at CRT-004 has the same effect via `EXC-004`.)*

### Scenario 2b — AI-assisted early stop via numeric range (age)

The age question (`DEM-002`, linked to `INC-001`, a Required criterion requiring "18 to 75 years") is marked `canTriggerEarlyStop`. Unlike a plain yes/no exclusion answer, a numeric range can't be resolved by the deterministic yes/no heuristic — so this specific question's answer is sent to Claude to interpret against the criterion text, with the backend still deciding whether to recommend stopping.

| Question | Answer |
|---|---|
| DEM-SEX What sex were you assigned at birth? | Female |
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | **16** |

**Expected outcome:** the `answers` response returns `earlyStopRecommended: true` with a reason referencing `INC-001` and the 18–75 range (Claude-assisted interpretation of the numeric answer). A banner appears immediately on the Screening Session page: "Screening result may already be determined," with **End & View Summary** and **Continue Screening** actions — the session is never auto-completed or blocked. Ending early produces a **Likely Ineligible** summary with `INC-001` under "Failed" and every other criterion listed under "Skipped (session ended early)."

*Fallback-mode note: if Claude is unavailable, this specific numeric-range case is not caught by the deterministic yes/no fallback heuristic — a documented limitation (see README.md/CODESETUP.md hackathon simplifications). Fallback mode still correctly catches plain yes/no disqualifiers such as Scenario 2's Type 1 Diabetes / insulin answers.*

## Scenario 3 — Needs Clinical Review (uncertain medication history)

| Question | Answer |
|---|---|
| DEM-SEX What sex were you assigned at birth? | Female |
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | 45 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | No |
| CRT-002 Diagnosed with Type 2 Diabetes ≥ 6 months? | Yes |
| CRT-003 Severe kidney impairment? | No |
| CRT-004 Currently taking insulin? | **Not sure / unsure of current medications** |

**Expected outcome:** the uncertain answer to CRT-004 maps to `needs_review` rather than a clear pass/fail. Once no criterion has failed but at least one is uncertain, `overallLikelyStatus` becomes **Needs Clinical Review**. The Summary page lists `EXC-004` under "Needs review" and explains in the reasoning section that missing/uncertain medication history requires clinical confirmation before a final decision.

## Scenario 4 — Sex-Specific Question Filtering (male patient)

`EXC-002` (pregnancy/breastfeeding) is tagged `appliesToSex: Female` at criteria-extraction time. Because at least one approved criterion needs sex-based gating, `DEM-SEX` is generated and asked first; answering it "Male" makes `EXC-002` resolve to `not_applicable` — its dedicated pregnancy question is never shown, and it's never counted as missing information.

| Question | Answer |
|---|---|
| DEM-SEX What sex were you assigned at birth? | **Male** |
| DEM-002 Current age? | 52 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | No |
| CRT-002 Diagnosed with Type 2 Diabetes ≥ 6 months? | Yes |
| CRT-003 Severe kidney impairment? | No |
| CRT-004 Currently taking insulin? | No, not taking insulin |
| CRT-005 Recent HbA1c 7.0–10.0%? | Yes, within range |
| CRT-006 Known drug allergies? | No |

**Expected outcome:** notice `DEM-001` (the pregnancy question) never appears in this session at all — the question list goes straight from `DEM-SEX` to `DEM-002`. The Summary page's Eligibility Criteria Summary lists `EXC-002` under **"Not applicable to this patient"**, separate from "Skipped (session ended early)" and never mentioned under missing information. `AuditEvents` records a `CriterionNotApplicable` entry. As in Scenario 1, HbA1c/insulin/kidney are `requiresClinicalReview` criteria, so the overall recommendation lands on **Needs Clinical Review** rather than Likely Eligible — that part of the outcome is unrelated to sex filtering.

*Regression check: repeat this same walkthrough answering `DEM-SEX: Female` instead — `DEM-001` (pregnancy) should now appear normally, exactly as in Scenarios 1–3.*

---

## De-duplication example

Both scenarios show the de-duplication behavior directly: age (`INC-001`), pregnancy status (`EXC-002`), consent (`INC-005`), and visit availability (`INC-004`) are answered **once**, in the Demographic Information section, and never appear as separate questions in the Eligibility Criteria Questions section. The Question Bank Preview page's "Suppressed Duplicate Criteria" table shows all four, each with the covering demographic question ID and a plain-language reason.

## Export

From the Summary page, use **Export JSON** or **Export CSV** to download the full recommendation, reasoning, and criteria breakdown for the completed session.
