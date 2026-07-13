import { useAiStatus } from "../api/AiStatusContext";

export default function AiStatusBanner() {
  const { status } = useAiStatus();

  if (!status || !status.runningInFallbackMode) {
    return null;
  }

  return <div className="warning">{status.lastStatusMessage}</div>;
}
