import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Criterion,
  approveCriteria,
  extractCriteria,
  generateQuestionBank,
  getCriteria,
  updateCriterion,
  uploadProtocol,
  useSampleProtocol
} from "../api/client";
import { useAiStatus } from "../api/AiStatusContext";
import { useWorkflow } from "../api/WorkflowContext";

export default function ProtocolWorkspace() {
  const { protocolId, setProtocolId, setQuestionBankMeta } = useWorkflow();
  const { refresh: refreshAiStatus } = useAiStatus();
  const navigate = useNavigate();

  const [fileName, setFileName] = useState<string | null>(null);
  const [criteria, setCriteria] = useState<Criterion[]>([]);
  const [status, setStatus] = useState<string>("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [fallbackNotice, setFallbackNotice] = useState<string | null>(null);

  useEffect(() => {
    if (protocolId) {
      getCriteria(protocolId)
        .then(setCriteria)
        .catch(() => {});
    }
  }, [protocolId]);

  async function handleUseSample() {
    setBusy(true);
    setError(null);
    try {
      const protocol = await useSampleProtocol();
      setProtocolId(protocol.protocolId);
      setFileName(protocol.fileName);
      setStatus("Sample protocol loaded.");
      setCriteria([]);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleUpload(file: File) {
    setBusy(true);
    setError(null);
    try {
      const protocol = await uploadProtocol(file);
      setProtocolId(protocol.protocolId);
      setFileName(protocol.fileName);
      setStatus(
        protocol.usedFallback
          ? `Uploaded, but PDF extraction fell back to the sample protocol: ${protocol.fallbackReason}`
          : "Protocol uploaded and text extracted."
      );
      setCriteria([]);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleExtractCriteria() {
    if (!protocolId) return;
    setBusy(true);
    setError(null);
    try {
      const result = await extractCriteria(protocolId);
      setCriteria(result.criteria);
      setFallbackNotice(result.usedFallback ? result.fallbackReason : null);
      await refreshAiStatus();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleApproveOne(criterionId: string) {
    if (!protocolId) return;
    setError(null);
    try {
      const result = await approveCriteria(protocolId, [criterionId]);
      setCriteria((prev) => prev.map((c) => result.criteria.find((rc) => rc.criterionId === c.criterionId) ?? c));
    } catch (err) {
      setError((err as Error).message);
    }
  }

  async function handleApproveAll() {
    if (!protocolId) return;
    setBusy(true);
    setError(null);
    try {
      const result = await approveCriteria(protocolId);
      setCriteria(result.criteria);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleCriterionSave(criterionId: string, patch: Partial<Criterion>) {
    const updated = await updateCriterion(criterionId, patch);
    setCriteria((prev) => prev.map((c) => (c.criterionId === criterionId ? updated : c)));
  }

  async function handleGenerateQuestionBank() {
    if (!protocolId) return;
    setBusy(true);
    setError(null);
    try {
      const result = await generateQuestionBank(protocolId);
      setQuestionBankMeta({
        sequencingRationale: result.sequencingRationale,
        suppressedDuplicateCriteria: result.suppressedDuplicateCriteria
      });
      await refreshAiStatus();
      navigate("/questions");
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const approvedCount = criteria.filter((c) => c.isApproved).length;

  return (
    <>
      <div className="page-header">
        <h1>Protocol Upload &amp; Criteria Review</h1>
        <p>Upload a sample clinical trial protocol, extract eligibility criteria, then review and approve them.</p>
      </div>

      <div className="card">
        <h2>1. Protocol Source</h2>
        <div className="button-row">
          <button className="button button-primary" disabled={busy} onClick={handleUseSample}>
            Use Sample Protocol
          </button>
          <label className="button button-secondary" style={{ cursor: "pointer" }}>
            Upload PDF
            <input
              type="file"
              accept="application/pdf"
              style={{ display: "none" }}
              disabled={busy}
              onChange={(e) => e.target.files?.[0] && handleUpload(e.target.files[0])}
            />
          </label>
        </div>
        {fileName && (
          <p>
            Loaded: <strong>{fileName}</strong>
          </p>
        )}
        {status && <p>{status}</p>}
        {error && <div className="error-banner">{error}</div>}
      </div>

      {protocolId && (
        <div className="card">
          <h2>2. Extract Eligibility Criteria</h2>
          <div className="button-row">
            <button className="button button-primary" disabled={busy} onClick={handleExtractCriteria}>
              Extract Criteria
            </button>
            <button className="button button-secondary" disabled={busy || criteria.length === 0} onClick={handleApproveAll}>
              Approve All Criteria
            </button>
          </div>
          {fallbackNotice && <div className="warning">Fallback criteria used: {fallbackNotice}</div>}

          {criteria.length === 0 ? (
            <p className="empty-state">No criteria extracted yet.</p>
          ) : (
            <>
              <p>
                {approvedCount} of {criteria.length} criteria approved.
              </p>
              {criteria.map((c) => (
                <CriterionRow key={c.criterionId} criterion={c} onApprove={handleApproveOne} onSave={handleCriterionSave} />
              ))}
            </>
          )}
        </div>
      )}

      {protocolId && criteria.some((c) => c.isApproved) && (
        <div className="card">
          <h2>3. Generate Question Bank</h2>
          <p>Generates exactly two sections: Demographic Information and Eligibility Criteria Questions.</p>
          <button className="button button-primary" disabled={busy} onClick={handleGenerateQuestionBank}>
            Generate Question Bank
          </button>
        </div>
      )}
    </>
  );
}

function CriterionRow({
  criterion,
  onApprove,
  onSave
}: {
  criterion: Criterion;
  onApprove: (id: string) => void;
  onSave: (id: string, patch: Partial<Criterion>) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [simpleMeaning, setSimpleMeaning] = useState(criterion.simpleMeaning ?? "");
  const [patientQuestion, setPatientQuestion] = useState(criterion.patientQuestion ?? "");
  const [priority, setPriority] = useState(criterion.priority);

  function save() {
    onSave(criterion.criterionId, { simpleMeaning, patientQuestion, priority });
    setEditing(false);
  }

  return (
    <div className="criterion-row">
      <div className="criterion-head">
        <span className="criterion-code">{criterion.criterionId}</span>
        <span className={`badge ${criterion.type === "Inclusion" ? "badge-inclusion" : "badge-exclusion"}`}>
          {criterion.type}
        </span>
        <span className={`badge badge-${criterion.priority.toLowerCase()}`}>{criterion.priority} priority</span>
        {criterion.canBeCoveredByDemographics && <span className="badge badge-low">Demographics-coverable</span>}
        {criterion.appliesToSex && <span className="badge badge-needs_review">Applies to: {criterion.appliesToSex}</span>}
        {criterion.isApproved && <span className="badge badge-satisfied">Approved</span>}
      </div>
      <p>
        <strong>Original:</strong> {criterion.originalText}
      </p>

      {editing ? (
        <>
          <div className="field">
            <label>Simple meaning</label>
            <input type="text" value={simpleMeaning} onChange={(e) => setSimpleMeaning(e.target.value)} />
          </div>
          <div className="field">
            <label>Patient question</label>
            <input type="text" value={patientQuestion} onChange={(e) => setPatientQuestion(e.target.value)} />
          </div>
          <div className="field">
            <label>Priority</label>
            <select value={priority} onChange={(e) => setPriority(e.target.value)}>
              <option value="High">High</option>
              <option value="Medium">Medium</option>
              <option value="Low">Low</option>
            </select>
          </div>
          <div className="button-row">
            <button className="button button-primary" onClick={save}>
              Save
            </button>
            <button className="button button-secondary" onClick={() => setEditing(false)}>
              Cancel
            </button>
          </div>
        </>
      ) : (
        <>
          <p>
            <strong>Simple meaning:</strong> {criterion.simpleMeaning}
          </p>
          <p>
            <strong>Patient question:</strong> {criterion.patientQuestion}
          </p>
          <div className="button-row">
            <button className="button button-secondary" onClick={() => setEditing(true)}>
              Edit
            </button>
            {!criterion.isApproved && (
              <button className="button button-primary" onClick={() => onApprove(criterion.criterionId)}>
                Approve
              </button>
            )}
          </div>
        </>
      )}
    </div>
  );
}
