import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ScreeningQuestion, createScreeningSession, getQuestionSections } from "../api/client";
import { useWorkflow } from "../api/WorkflowContext";

export default function QuestionBankPreview() {
  const { protocolId, setSessionId, questionBankMeta } = useWorkflow();
  const navigate = useNavigate();

  const [demographics, setDemographics] = useState<ScreeningQuestion[]>([]);
  const [criteria, setCriteria] = useState<ScreeningQuestion[]>([]);
  const [patientAlias, setPatientAlias] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!protocolId) return;
    getQuestionSections(protocolId)
      .then((sections) => {
        setDemographics(sections.demographics);
        setCriteria(sections.criteria);
      })
      .catch((err) => setError(err.message));
  }, [protocolId]);

  async function handleStartSession() {
    if (!protocolId || !patientAlias.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const session = await createScreeningSession(protocolId, patientAlias.trim());
      setSessionId(session.sessionId);
      navigate("/screening");
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (!protocolId) {
    return (
      <>
        <div className="page-header">
          <h1>Question Bank Preview</h1>
        </div>
        <p className="empty-state">Upload or select a protocol first, then generate a question bank.</p>
      </>
    );
  }

  return (
    <>
      <div className="page-header">
        <h1>Question Bank Preview</h1>
        <p>Exactly two sections: Demographic Information, then Eligibility Criteria Questions in mixed order.</p>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {questionBankMeta ? (
        <div className="card">
          <h2>AI Sequencing Rationale</h2>
          <p>{questionBankMeta.sequencingRationale}</p>
          {questionBankMeta.suppressedDuplicateCriteria.length > 0 && (
            <>
              <h3>Suppressed Duplicate Criteria</h3>
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Criterion</th>
                    <th>Covered by</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {questionBankMeta.suppressedDuplicateCriteria.map((s) => (
                    <tr key={s.criterionId}>
                      <td>{s.criterionId}</td>
                      <td>{s.coveredByQuestionId}</td>
                      <td>{s.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </div>
      ) : (
        <div className="card">
          <p className="empty-state">
            Regenerate the question bank from the Protocol page to see the sequencing rationale and de-duplication details.
          </p>
        </div>
      )}

      <div className="card">
        <span className="section-tag">Section 1 — Demographic Information</span>
        {demographics.length === 0 ? (
          <p className="empty-state">No demographic questions yet. Generate the question bank from the Protocol page.</p>
        ) : (
          demographics.map((q) => <QuestionCard key={q.questionId} question={q} />)
        )}
      </div>

      <div className="card">
        <span className="section-tag">Section 2 — Eligibility Criteria Questions (mixed order)</span>
        {criteria.length === 0 ? (
          <p className="empty-state">No criteria questions yet.</p>
        ) : (
          criteria.map((q) => <QuestionCard key={q.questionId} question={q} />)
        )}
      </div>

      {demographics.length + criteria.length > 0 && (
        <div className="card">
          <h2>Start Screening Session</h2>
          <div className="field">
            <label>Patient alias (not real identity)</label>
            <input
              type="text"
              value={patientAlias}
              onChange={(e) => setPatientAlias(e.target.value)}
              placeholder="e.g. Patient-Alpha"
            />
          </div>
          <button className="button button-primary" disabled={busy || !patientAlias.trim()} onClick={handleStartSession}>
            Start Screening Session
          </button>
        </div>
      )}
    </>
  );
}

function QuestionCard({ question }: { question: ScreeningQuestion }) {
  return (
    <div className="criterion-row">
      <div className="criterion-head">
        <span className="criterion-code">{question.questionId}</span>
        <span className={`badge badge-${question.priority.toLowerCase()}`}>{question.priority} priority</span>
        {question.canTriggerEarlyStop && <span className="badge badge-failed">Can trigger early stop</span>}
        {question.appliesToSex && <span className="badge badge-needs_review">Applies to: {question.appliesToSex}</span>}
      </div>
      <p>
        <strong>{question.questionText}</strong>
      </p>
      {question.linkedCriteria.length > 0 && <p>Linked criteria: {question.linkedCriteria.join(", ")}</p>}
      {question.coveredCriteria.length > 0 && (
        <p>Also covers eligibility criteria: {question.coveredCriteria.join(", ")} (not asked again in Criteria section)</p>
      )}
      {question.whyAsked && <div className="why-asked">Why is this asked? {question.whyAsked}</div>}
    </div>
  );
}
