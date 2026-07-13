import { createContext, useContext, useState, ReactNode } from "react";
import { SuppressedDuplicateCriterion } from "./client";

export interface QuestionBankMeta {
  sequencingRationale: string;
  suppressedDuplicateCriteria: SuppressedDuplicateCriterion[];
}

interface WorkflowContextValue {
  protocolId: string | null;
  setProtocolId: (id: string | null) => void;
  sessionId: string | null;
  setSessionId: (id: string | null) => void;
  questionBankMeta: QuestionBankMeta | null;
  setQuestionBankMeta: (meta: QuestionBankMeta | null) => void;
}

const WorkflowContext = createContext<WorkflowContextValue>({
  protocolId: null,
  setProtocolId: () => {},
  sessionId: null,
  setSessionId: () => {},
  questionBankMeta: null,
  setQuestionBankMeta: () => {}
});

export function WorkflowProvider({ children }: { children: ReactNode }) {
  const [protocolId, setProtocolIdState] = useState<string | null>(() => localStorage.getItem("protocolId"));
  const [sessionId, setSessionIdState] = useState<string | null>(() => localStorage.getItem("sessionId"));
  const [questionBankMeta, setQuestionBankMeta] = useState<QuestionBankMeta | null>(null);

  const setProtocolId = (id: string | null) => {
    setProtocolIdState(id);
    if (id) localStorage.setItem("protocolId", id);
    else localStorage.removeItem("protocolId");
  };

  const setSessionId = (id: string | null) => {
    setSessionIdState(id);
    if (id) localStorage.setItem("sessionId", id);
    else localStorage.removeItem("sessionId");
  };

  return (
    <WorkflowContext.Provider
      value={{ protocolId, setProtocolId, sessionId, setSessionId, questionBankMeta, setQuestionBankMeta }}
    >
      {children}
    </WorkflowContext.Provider>
  );
}

export function useWorkflow() {
  return useContext(WorkflowContext);
}
