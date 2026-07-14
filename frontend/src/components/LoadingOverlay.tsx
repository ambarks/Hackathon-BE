import { useEffect, useState } from "react";
import { subscribeLoading } from "../api/loadingStore";

// Full-screen overlay shown for the duration of any in-flight API call
// (extraction, question-bank generation, screening answers, summary — some of
// which call Claude and can take several seconds). It sits above the whole
// app shell, so it also blocks navigation/clicks elsewhere while a request is
// in progress, not just the button that triggered it.
export default function LoadingOverlay() {
  const [activeCount, setActiveCount] = useState(0);

  useEffect(() => subscribeLoading(setActiveCount), []);

  if (activeCount === 0) {
    return null;
  }

  return (
    <div className="loading-overlay" role="status" aria-live="polite">
      <div className="loading-spinner" aria-hidden="true" />
      <p>Working — please wait…</p>
    </div>
  );
}
