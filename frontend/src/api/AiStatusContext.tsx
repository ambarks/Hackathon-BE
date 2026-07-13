import { createContext, useCallback, useContext, useEffect, useState, ReactNode } from "react";
import { getAiHealth, AiHealthStatus } from "./client";

interface AiStatusContextValue {
  status: AiHealthStatus | null;
  refresh: () => Promise<void>;
}

const AiStatusContext = createContext<AiStatusContextValue>({ status: null, refresh: async () => {} });

export function AiStatusProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AiHealthStatus | null>(null);

  const refresh = useCallback(async () => {
    try {
      const result = await getAiHealth();
      setStatus(result);
    } catch {
      // Health check failures are surfaced elsewhere; never crash the banner.
    }
  }, []);

  useEffect(() => {
    refresh();
    const interval = setInterval(refresh, 15000);
    return () => clearInterval(interval);
  }, [refresh]);

  return <AiStatusContext.Provider value={{ status, refresh }}>{children}</AiStatusContext.Provider>;
}

export function useAiStatus() {
  return useContext(AiStatusContext);
}
