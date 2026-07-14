import { beginRequest, endRequest } from "./loadingStore";

const BASE_URL = import.meta.env.VITE_API_BASE_URL || "http://localhost:8080";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  // Every call through here — from any page — counts toward the global
  // loading overlay (see Layout.tsx), so slow backend/Claude calls always
  // show a blocking indicator without every page having to wire up its own.
  beginRequest();
  try {
    const response = await fetch(`${BASE_URL}${path}`, {
      ...init,
      headers: {
        ...(init?.body && !(init.body instanceof FormData) ? { "Content-Type": "application/json" } : {}),
        ...init?.headers
      }
    });

    if (!response.ok) {
      let message = `Request failed (${response.status})`;
      try {
        const body = await response.json();
        if (body?.message) {
          message = body.message;
        }
      } catch {
        // Ignore non-JSON error bodies.
      }
      throw new Error(message);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  } finally {
    endRequest();
  }
}

// ---- Health ----

export interface AiHealthStatus {
  claudeConfigured: boolean;
  model: string;
  lastCallSucceeded: boolean;
  runningInFallbackMode: boolean;
  lastStatusMessage: string;
  lastCheckedAt: string;
}

export const getAiHealth = () => request<AiHealthStatus>("/api/health/ai");
export const getDatabaseHealth = () => request<{ status: string; message?: string }>("/api/health/database");

// ---- Protocols ----

export interface ProtocolSummary {
  protocolId: string;
  fileName: string | null;
  source: string;
  status: string;
  uploadedAt: string;
}

export interface ProtocolChunk {
  id: string;
  chunkIndex: number;
  sectionTitle: string | null;
  chunkText: string;
  pageNumber: number | null;
  criterionId: string | null;
}

export interface ProtocolDetail extends ProtocolSummary {
  usedFallback: boolean;
  fallbackReason: string | null;
  chunks: ProtocolChunk[] | null;
}

export const listProtocols = () => request<ProtocolSummary[]>("/api/protocols");
export const getProtocol = (protocolId: string) => request<ProtocolDetail>(`/api/protocols/${protocolId}`);
export const useSampleProtocol = () => request<ProtocolDetail>("/api/protocols/use-sample", { method: "POST" });

export async function uploadProtocol(file: File): Promise<ProtocolDetail> {
  const formData = new FormData();
  formData.append("file", file);
  return request<ProtocolDetail>("/api/protocols/upload", { method: "POST", body: formData });
}

// ---- Criteria ----

export interface Criterion {
  criterionId: string;
  protocolId: string;
  type: string;
  originalText: string;
  simpleMeaning: string | null;
  patientQuestion: string | null;
  answerType: string;
  options: string[];
  eligibilityImpact: string;
  priority: string;
  sourceSection: string | null;
  requiresClinicalReview: boolean;
  canBeCoveredByDemographics: boolean;
  isApproved: boolean;
  promptVersion: string | null;
  modelName: string | null;
}

export interface CriteriaExtractionResponse {
  protocolId: string;
  usedFallback: boolean;
  fallbackReason: string | null;
  criteria: Criterion[];
}

export const extractCriteria = (protocolId: string) =>
  request<CriteriaExtractionResponse>(`/api/protocols/${protocolId}/extract-criteria`, { method: "POST" });

export const getCriteria = (protocolId: string) => request<Criterion[]>(`/api/protocols/${protocolId}/criteria`);

export const updateCriterion = (criterionId: string, patch: Partial<Criterion>) =>
  request<Criterion>(`/api/criteria/${criterionId}`, { method: "PUT", body: JSON.stringify(patch) });

export const approveCriteria = (protocolId: string, criterionIds?: string[]) =>
  request<{ protocolId: string; approvedCount: number; criteria: Criterion[] }>(
    `/api/protocols/${protocolId}/criteria/approve`,
    { method: "POST", body: JSON.stringify({ criterionIds: criterionIds ?? null }) }
  );

// ---- Question bank ----

export interface ScreeningQuestion {
  questionId: string;
  section: string;
  questionText: string;
  answerType: string;
  options: string[];
  priority: string;
  displayOrder: number;
  linkedCriteria: string[];
  coveredCriteria: string[];
  sourceCriteriaText: string | null;
  whyAsked: string | null;
  isDemographicQuestion: boolean;
  isDuplicateSuppressed: boolean;
  canTriggerEarlyStop: boolean;
  earlyStopReason: string | null;
  promptVersion: string | null;
  modelName: string | null;
}

export interface SuppressedDuplicateCriterion {
  criterionId: string;
  coveredByQuestionId: string;
  reason: string;
}

export interface QuestionBankResponse {
  protocolId: string;
  usedFallback: boolean;
  fallbackReason: string | null;
  sequencingRationale: string;
  suppressedDuplicateCriteria: SuppressedDuplicateCriterion[];
  demographics: ScreeningQuestion[];
  criteria: ScreeningQuestion[];
}

export const generateQuestionBank = (protocolId: string) =>
  request<QuestionBankResponse>(`/api/protocols/${protocolId}/generate-question-bank`, { method: "POST" });

export const getQuestionSections = (protocolId: string) =>
  request<{ demographics: ScreeningQuestion[]; criteria: ScreeningQuestion[] }>(
    `/api/protocols/${protocolId}/questions/sections`
  );

// ---- Screening sessions ----

export interface ScreeningSession {
  sessionId: string;
  protocolId: string;
  patientAlias: string;
  currentSection: string;
  status: string;
  overallLikelyStatus: string | null;
  createdAt: string;
  completedAt: string | null;
  earlyStopRecommended: boolean;
  earlyStopReason: string | null;
  disqualifyingCriterionId: string | null;
}

export interface NextQuestion {
  hasNextQuestion: boolean;
  questionId: string | null;
  section: string | null;
  questionText: string | null;
  answerType: string | null;
  options: string[] | null;
  priority: string | null;
  linkedCriteria: string[] | null;
  coveredCriteria: string[] | null;
  whyAsked: string | null;
  isDemographicQuestion: boolean;
  canTriggerEarlyStop: boolean;
  earlyStopReason: string | null;
  // Live, backend-computed recommendation for the session as of the answers
  // recorded so far (distinct from the static per-question fields above).
  sessionEarlyStopRecommended: boolean;
  sessionEarlyStopReason: string | null;
  sessionDisqualifyingCriterionId: string | null;
}

export interface SectionProgress {
  demographics: { answered: number; total: number };
  criteria: { answered: number; total: number };
  currentSection: string;
  isComplete: boolean;
}

export interface AnswerResult {
  questionId: string;
  section: string;
  responseText: string;
  mappedEligibilityStatus: string | null;
  coveredMultipleCriteria: boolean;
  currentSection: string;
  overallLikelyStatus: string | null;
  sessionCompleted: boolean;
  earlyStopRecommended: boolean;
  earlyStopReason: string | null;
  disqualifyingCriterionId: string | null;
}

export const createScreeningSession = (protocolId: string, patientAlias: string) =>
  request<ScreeningSession>("/api/screening-sessions", {
    method: "POST",
    body: JSON.stringify({ protocolId, patientAlias })
  });

export const getScreeningSession = (sessionId: string) =>
  request<ScreeningSession>(`/api/screening-sessions/${sessionId}`);

export const getNextQuestion = (sessionId: string) =>
  request<NextQuestion>(`/api/screening-sessions/${sessionId}/next-question`);

export const getSectionProgress = (sessionId: string) =>
  request<SectionProgress>(`/api/screening-sessions/${sessionId}/section-progress`);

export const submitAnswer = (sessionId: string, questionId: string, response: string) =>
  request<AnswerResult>(`/api/screening-sessions/${sessionId}/answers`, {
    method: "POST",
    body: JSON.stringify({ questionId, response })
  });

// Recruiter-initiated: ends the session immediately regardless of how many
// questions remain unanswered. Never called automatically by the app itself —
// the early-stop signal above is always a recommendation, not a forced action.
export const endScreeningSession = (sessionId: string) =>
  request<ScreeningSession>(`/api/screening-sessions/${sessionId}/end`, { method: "POST" });

// ---- Summary ----

export interface DemographicAnswerSummary {
  questionId: string;
  questionText: string;
  responseText: string;
  coveredCriteria: string[];
}

export interface CriterionSummary {
  criterionId: string;
  type: string;
  status: string;
  resolvedByQuestionId: string | null;
}

export interface SessionSummary {
  recommendation: string;
  summary: string;
  demographicSummary: DemographicAnswerSummary[];
  criteriaSummary: CriterionSummary[];
  criteriaCoveredByDemographics: string[];
  satisfiedCriteria: string[];
  failedCriteria: string[];
  needsReviewCriteria: string[];
  skippedCriteria: string[];
  missingInformation: string[];
  reasoning: string[];
  recommendedNextAction: string;
  disclaimer: string;
  usedFallback: boolean;
  fallbackReason: string | null;
}

export const getSummary = (sessionId: string) => request<SessionSummary>(`/api/screening-sessions/${sessionId}/summary`);

export const getExportUrl = (sessionId: string, format: "json" | "csv") =>
  `${BASE_URL}/api/screening-sessions/${sessionId}/export/${format}`;
