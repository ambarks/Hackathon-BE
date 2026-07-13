import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  NextQuestion,
  SectionProgress,
  endScreeningSession,
  getNextQuestion,
  getSectionProgress,
  submitAnswer
} from "../api/client";
import { useAiStatus } from "../api/AiStatusContext";
import { useWorkflow } from "../api/WorkflowContext";

interface EarlyStopRecommendation {
  reason: string | null;
  criterionId: string | null;
}

export default function ScreeningSessionPage() {
  const { sessionId } = useWorkflow();
  const { refresh: refreshAiStatus } = useAiStatus();
  const navigate = useNavigate();

  const [question, setQuestion] = useState<NextQuestion | null>(null);
  const [progress, setProgress] = useState<SectionProgress | null>(null);
  const [textAnswer, setTextAnswer] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Authoritative, backend-computed recommendation (never a frontend guess) —
  // set from submitAnswer's response, or restored from the session's live
  // state on load (e.g. after a page refresh). Claude only assists in
  // classifying the answer; the backend decides whether to recommend stopping.
  const [earlyStop, setEarlyStop] = useState<EarlyStopRecommendation | null>(null);

  const load = useCallback(async () => {
    if (!sessionId) return;
    try {
      const [next, prog] = await Promise.all([getNextQuestion(sessionId), getSectionProgress(sessionId)]);
      setQuestion(next);
      setProgress(prog);
      setEarlyStop(
        next.sessionEarlyStopRecommended ? { reason: next.sessionEarlyStopReason, criterionId: next.sessionDisqualifyingCriterionId } : null
      );
    } catch (err) {
      setError((err as Error).message);
    }
  }, [sessionId]);

  useEffect(() => {
    load();
  }, [load]);

  async function handleAnswer(response: string) {
    if (!sessionId || !question?.questionId) return;
    setBusy(true);
    setError(null);
    try {
      const result = await submitAnswer(sessionId, question.questionId, response);
      setTextAnswer("");
      await refreshAiStatus();
      setEarlyStop(result.earlyStopRecommended ? { reason: result.earlyStopReason, criterionId: result.disqualifyingCriterionId } : null);
      await load();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleEndSession() {
    await refreshAiStatus();
    navigate("/summary");
  }

  async function handleEndEarly() {
    if (!sessionId) return;
    setBusy(true);
    setError(null);
    try {
      await endScreeningSession(sessionId);
      await handleEndSession();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  function handleContinueScreening() {
    setEarlyStop(null);
  }

  if (!sessionId) {
    return (
      <>
        <div className="page-header">
          <h1>Screening Session</h1>
        </div>
        <p className="empty-state">Start a screening session from the Question Bank Preview page first.</p>
      </>
    );
  }

  return (
    <>
      <div className="page-header">
        <h1>Screening Session</h1>
        <p>Questions are asked one at a time: Demographic Information first, then Eligibility Criteria Questions.</p>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {earlyStop && (
        <div className="early-stop-banner">
          <p className="early-stop-title">Screening result may already be determined</p>
          <p>
            {earlyStop.reason ?? "The recorded answers so far indicate this patient does not meet a required or exclusionary criterion."}
            {earlyStop.criterionId && ` (criterion ${earlyStop.criterionId})`}
          </p>
          <p className="progress-label">
            This is AI-assisted guidance only — the recruiter decides whether to end the session now or continue.
          </p>
          <div className="button-row">
            <button className="button button-primary" disabled={busy} onClick={handleEndEarly}>
              End &amp; View Summary
            </button>
            <button className="button button-secondary" disabled={busy} onClick={handleContinueScreening}>
              Continue Screening
            </button>
          </div>
        </div>
      )}

      {progress && (
        <div className="card">
          <h2>Section Progress</h2>
          <p className="section-tag">
            {progress.currentSection === "Completed" ? "Completed" : `Current section: ${progress.currentSection}`}
          </p>
          <ProgressBar label="Demographic Information" done={progress.demographics.answered} total={progress.demographics.total} />
          <ProgressBar
            label="Eligibility Criteria Questions"
            done={progress.criteria.answered}
            total={progress.criteria.total}
          />
        </div>
      )}

      <div className="card">
        {!question?.hasNextQuestion ? (
          <div className="empty-state">
            <p>All questions have been answered.</p>
            <button className="button button-primary" onClick={handleEndSession}>
              Generate Summary
            </button>
          </div>
        ) : (
          <div className="question-card">
            <span className="section-tag">
              {question.section === "Demographics" ? "Demographic Information" : "Eligibility Criteria Questions"}
            </span>
            <p className="question-text">{question.questionText}</p>

            {question.isDemographicQuestion && question.coveredCriteria && question.coveredCriteria.length > 0 && (
              <div className="why-asked">
                This demographic question also covers the following eligibility criteria: {question.coveredCriteria.join(", ")}
              </div>
            )}

            <AnswerInput question={question} textAnswer={textAnswer} setTextAnswer={setTextAnswer} onSubmit={handleAnswer} busy={busy} />

            {question.whyAsked && <div className="why-asked">Why is this asked? {question.whyAsked}</div>}
            {question.linkedCriteria && question.linkedCriteria.length > 0 && (
              <p className="progress-label">Mapped criterion/criteria: {question.linkedCriteria.join(", ")}</p>
            )}

            <div className="button-row" style={{ marginTop: 16 }}>
              <button className="button button-secondary" disabled={busy} onClick={handleEndSession}>
                End and Generate Summary
              </button>
            </div>
          </div>
        )}
      </div>
    </>
  );
}

function ProgressBar({ label, done, total }: { label: string; done: number; total: number }) {
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  return (
    <div style={{ marginBottom: 10 }}>
      <span className="progress-label">
        {label}: Question {Math.min(done + 1, total)} of {total}
      </span>
      <div className="progress-track">
        <div className="progress-fill" style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

function AnswerInput({
  question,
  textAnswer,
  setTextAnswer,
  onSubmit,
  busy
}: {
  question: NextQuestion;
  textAnswer: string;
  setTextAnswer: (v: string) => void;
  onSubmit: (response: string) => void;
  busy: boolean;
}) {
  if (question.answerType === "yes_no") {
    return (
      <div className="options-list">
        <button className="option-button" disabled={busy} onClick={() => onSubmit("Yes")}>
          Yes
        </button>
        <button className="option-button" disabled={busy} onClick={() => onSubmit("No")}>
          No
        </button>
      </div>
    );
  }

  if (question.answerType === "single_choice" && question.options && question.options.length > 0) {
    return (
      <div className="options-list">
        {question.options.map((option) => (
          <button key={option} className="option-button" disabled={busy} onClick={() => onSubmit(option)}>
            {option}
          </button>
        ))}
      </div>
    );
  }

  return (
    <div className="field" style={{ marginTop: 10 }}>
      <input
        type="text"
        value={textAnswer}
        onChange={(e) => setTextAnswer(e.target.value)}
        placeholder="Type the recruiter-recorded response"
      />
      <div className="button-row" style={{ marginTop: 8 }}>
        <button
          className="button button-primary"
          disabled={busy || !textAnswer.trim()}
          onClick={() => onSubmit(textAnswer.trim())}
        >
          Submit Answer
        </button>
      </div>
    </div>
  );
}
