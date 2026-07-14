import { useEffect, useState } from "react";
import { SessionSummary, getExportUrl, getSummary } from "../api/client";
import { useAiStatus } from "../api/AiStatusContext";
import { useWorkflow } from "../api/WorkflowContext";

export default function SummaryPage() {
  const { sessionId } = useWorkflow();
  const { refresh: refreshAiStatus } = useAiStatus();

  const [summary, setSummary] = useState<SessionSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!sessionId) return;
    setLoading(true);
    getSummary(sessionId)
      .then(async (result) => {
        setSummary(result);
        await refreshAiStatus();
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
    // Runs once per session; refreshAiStatus identity is stable via useCallback.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  if (!sessionId) {
    return (
      <>
        <div className="page-header">
          <h1>Summary</h1>
        </div>
        <p className="empty-state">Complete a screening session first to generate a summary.</p>
      </>
    );
  }

  return (
    <>
      <div className="page-header">
        <h1>Screening Summary</h1>
        <p>Final AI-assisted pre-screening recommendation for recruiter and clinical reviewer sign-off.</p>
      </div>

      {error && <div className="error-banner">{error}</div>}
      {loading && <p>Generating summary…</p>}

      {summary && (
        <>
          {summary.usedFallback && (
            <div className="warning">Summary generated using rule-based fallback: {summary.fallbackReason}</div>
          )}

          <div className="card">
            <h2>Overall Recommendation</h2>
            <span className={`badge badge-${recommendationBadgeClass(summary.recommendation)}`}>{summary.recommendation}</span>
            <p style={{ marginTop: 12 }}>{summary.summary}</p>
            <p>
              <strong>Recommended next action:</strong> {summary.recommendedNextAction}
            </p>
          </div>

          <div className="card">
            <h2>Demographic Information Summary</h2>
            {summary.demographicSummary.length === 0 ? (
              <p className="empty-state">No demographic answers recorded.</p>
            ) : (
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Question</th>
                    <th>Response</th>
                    <th>Covers Criteria</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.demographicSummary.map((d) => (
                    <tr key={d.questionId}>
                      <td>{d.questionText}</td>
                      <td>{d.responseText}</td>
                      <td>{d.coveredCriteria.join(", ") || "-"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <div className="card">
            <h2>Eligibility Criteria Summary</h2>
            <p>
              <strong>Satisfied:</strong> {summary.satisfiedCriteria.join(", ") || "None"}
            </p>
            <p>
              <strong>Failed:</strong> {summary.failedCriteria.join(", ") || "None"}
            </p>
            <p>
              <strong>Needs review:</strong> {summary.needsReviewCriteria.join(", ") || "None"}
            </p>
            <p>
              <strong>Covered by demographics:</strong> {summary.criteriaCoveredByDemographics.join(", ") || "None"}
            </p>
            <p>
              <strong>Skipped (session ended early):</strong> {summary.skippedCriteria.join(", ") || "None"}
            </p>
            <p>
              <strong>Not applicable to this patient:</strong> {summary.notApplicableCriteria.join(", ") || "None"}
            </p>
          </div>

          <div className="card">
            <h2>Reasoning</h2>
            {summary.reasoning.length === 0 ? (
              <p className="empty-state">No additional reasoning notes.</p>
            ) : (
              <ul>
                {summary.reasoning.map((r, i) => (
                  <li key={i}>{r}</li>
                ))}
              </ul>
            )}
          </div>

          {summary.missingInformation.length > 0 && (
            <div className="card">
              <h2>Missing / Uncertain Information</h2>
              <ul>
                {summary.missingInformation.map((m, i) => (
                  <li key={i}>{m}</li>
                ))}
              </ul>
            </div>
          )}

          <div className="card">
            <h2>Export</h2>
            <div className="button-row">
              <a className="button button-primary" href={getExportUrl(sessionId, "json")} target="_blank" rel="noreferrer">
                Export JSON
              </a>
              <a className="button button-secondary" href={getExportUrl(sessionId, "csv")} target="_blank" rel="noreferrer">
                Export CSV
              </a>
            </div>
          </div>

          <div className="disclaimer">{summary.disclaimer}</div>
        </>
      )}
    </>
  );
}

function recommendationBadgeClass(recommendation: string): string {
  if (recommendation === "Likely Eligible") return "satisfied";
  if (recommendation === "Likely Ineligible") return "failed";
  return "needs_review";
}
