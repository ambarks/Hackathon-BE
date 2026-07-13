# Demo Script

Three walkthroughs for the sample protocol (`sample-data/sample-protocol.txt`, a synthetic Type 2 Diabetes trial). All scenarios assume:

1. Start the stack: `docker compose up --build`, open http://localhost:5173.
2. **Upload & Criteria** page → **Use Sample Protocol** → **Extract Criteria** → **Approve All Criteria** → **Generate Question Bank**.
3. **Question Bank** page → confirm exactly two sections and note the sequencing rationale, then enter a patient alias and **Start Screening Session**.

The question bank (fallback deterministic sequencing, used automatically when Claude is unavailable) produces this order:

**Demographic Information** (asked first, in this order): DEM-001 pregnancy status (covers `EXC-002`) → DEM-002 age (covers `INC-001`) → DEM-003 consent capability (covers `INC-005`) → DEM-004 visit availability (covers `INC-004`).

**Eligibility Criteria Questions** (mixed inclusion/exclusion order, asked second): CRT-001 Type 1 Diabetes history (`EXC-001`, high-priority exclusion) → CRT-002 Type 2 Diabetes diagnosis duration (`INC-002`, high-priority inclusion) → CRT-003 kidney impairment (`EXC-003`) → CRT-004 insulin therapy (`EXC-004`) → CRT-005 HbA1c range (`INC-003`) → CRT-006 drug allergy (`EXC-005`).

Notice that a high-priority **exclusion** question (CRT-001) is asked before several inclusion questions — this is the "mixed sequencing" behavior: the app never blindly asks all inclusion criteria before all exclusion criteria, or vice versa.

---

## Scenario 1 — Likely Eligible

| Question | Answer |
|---|---|
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
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | 61 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | **Yes** |

**Expected outcome:** as soon as CRT-001 is answered "Yes," the session's `overallLikelyStatus` immediately flips to **Likely Ineligible** — this is the very first eligibility-criteria question, demonstrating that a high-impact exclusion criterion can end screening quickly rather than after asking every inclusion question first. The recruiter can click **End and Generate Summary** immediately, or continue answering — either way the recommendation stays Likely Ineligible. The Summary page's "Failed" list shows `EXC-001`, and any remaining unanswered criteria appear under "Skipped (session ended early)."

*(Alternative disqualifying path: answering "Yes, taking insulin" at CRT-004 has the same effect via `EXC-004`.)*

## Scenario 3 — Needs Clinical Review (uncertain medication history)

| Question | Answer |
|---|---|
| DEM-001 Pregnant/breastfeeding/planning pregnancy? | No |
| DEM-002 Current age? | 45 |
| DEM-003 Able to provide informed consent? | Yes |
| DEM-004 Able to attend visits for 24 weeks? | Yes |
| CRT-001 History of Type 1 Diabetes? | No |
| CRT-002 Diagnosed with Type 2 Diabetes ≥ 6 months? | Yes |
| CRT-003 Severe kidney impairment? | No |
| CRT-004 Currently taking insulin? | **Not sure / unsure of current medications** |

**Expected outcome:** the uncertain answer to CRT-004 maps to `needs_review` rather than a clear pass/fail. Once no criterion has failed but at least one is uncertain, `overallLikelyStatus` becomes **Needs Clinical Review**. The Summary page lists `EXC-004` under "Needs review" and explains in the reasoning section that missing/uncertain medication history requires clinical confirmation before a final decision.

---

## De-duplication example

Both scenarios show the de-duplication behavior directly: age (`INC-001`), pregnancy status (`EXC-002`), consent (`INC-005`), and visit availability (`INC-004`) are answered **once**, in the Demographic Information section, and never appear as separate questions in the Eligibility Criteria Questions section. The Question Bank Preview page's "Suppressed Duplicate Criteria" table shows all four, each with the covering demographic question ID and a plain-language reason.

## Export

From the Summary page, use **Export JSON** or **Export CSV** to download the full recommendation, reasoning, and criteria breakdown for the completed session.
