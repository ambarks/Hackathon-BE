import { Link } from "react-router-dom";
import { useWorkflow } from "../api/WorkflowContext";

const STEPS = [
  { title: "1. Upload Protocol", description: "Upload a PDF or use the sample protocol." },
  { title: "2. Extract & Review Criteria", description: "AI-assisted extraction, then recruiter review and approval." },
  { title: "3. Generate Question Bank", description: "Two sections: Demographic Information and Eligibility Criteria Questions." },
  { title: "4. Screening Session", description: "Ask adaptive questions one at a time." },
  { title: "5. Summary", description: "Recommendation, reasoning, and export." }
];

export default function Dashboard() {
  const { protocolId, sessionId } = useWorkflow();

  return (
    <>
      <div className="page-header">
        <h1>Workflow Dashboard</h1>
        <p>AI-Powered Clinical Trial Patient Pre-Screening Assistant — hackathon proof of concept.</p>
      </div>

      <div className="workflow-steps">
        {STEPS.map((step, index) => (
          <div className="workflow-step" key={step.title}>
            <span className="step-index">STEP {index + 1}</span>
            <h3>{step.title}</h3>
            <p>{step.description}</p>
          </div>
        ))}
      </div>

      <div className="card">
        <h2>Current Progress</h2>
        <p>Active protocol: {protocolId ? <code>{protocolId}</code> : "None yet"}</p>
        <p>Active screening session: {sessionId ? <code>{sessionId}</code> : "None yet"}</p>
        <div className="button-row">
          <Link className="button button-primary" to="/protocol">
            Start with Protocol Upload
          </Link>
          {protocolId && (
            <Link className="button button-secondary" to="/questions">
              Go to Question Bank
            </Link>
          )}
          {sessionId && (
            <Link className="button button-secondary" to="/screening">
              Resume Screening Session
            </Link>
          )}
        </div>
      </div>
    </>
  );
}
