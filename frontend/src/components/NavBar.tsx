import { NavLink } from "react-router-dom";

const STEPS = [
  { to: "/", label: "Dashboard" },
  { to: "/protocol", label: "Upload & Criteria" },
  { to: "/questions", label: "Question Bank" },
  { to: "/screening", label: "Screening Session" },
  { to: "/summary", label: "Summary" }
];

export default function NavBar() {
  return (
    <header className="top-nav">
      <span className="brand">Clinical Trial Pre-Screening Assistant</span>
      <nav>
        {STEPS.map((step) => (
          <NavLink
            key={step.to}
            to={step.to}
            end={step.to === "/"}
            className={({ isActive }) => "nav-link" + (isActive ? " active" : "")}
          >
            {step.label}
          </NavLink>
        ))}
      </nav>
    </header>
  );
}
