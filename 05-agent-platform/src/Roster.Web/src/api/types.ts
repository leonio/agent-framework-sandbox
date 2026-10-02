/**
 * The shapes the API returns, written by hand to match the records in Roster.Api/Endpoints. The API also publishes
 * /openapi/v1.json, from which these could be generated once they settle.
 */

export type Role = 'admin' | 'member';

export interface Me {
  id: string;
  name: string;
  email: string | null;
  title: string | null;
  defaultEndpointId: string | null;
  roles: Role[];
}

export interface Credential {
  id: string;
  kind: 'api-key' | 'github-token';
  label: string;
  hint: string;
  createdAt: string;
  lastUsedAt: string | null;
}

export type Tier = 'fast' | 'balanced' | 'reasoning';

export interface ModelEndpoint {
  id: string;
  name: string;
  kind: 'openai' | 'fake' | 'copilot';
  baseUrl: string | null;
  defaultModel: string;
  tierModels: Partial<Record<Tier, string>>;
  nativeStructuredOutput: boolean;
  supportsTools: boolean;
  supportsStreaming: boolean;
  reasoningModel: boolean;
  maxConcurrency: number;
  requestsPerMinute: number | null;
  credentialId: string | null;
  credentialHint: string | null;
  shared: boolean;
  mine: boolean;
}

export interface Scorecard {
  agentName: string;
  hash: string;
  versionLabel: string;
  calls: number;
  succeeded: number;
  repaired: number;
  failed: number;
  inputTokens: number;
  outputTokens: number;
  averageDurationMs: number | null;
  findings: number;
  accepted: number;
  rejected: number;
  undecided: number;
  acceptanceRate: number | null;
  good: number;
  bad: number;
  ugly: number;
  byTitle: { title: string; accepted: number; rejected: number; good: number; bad: number; ugly: number }[];
}

export interface AgentSummary {
  name: string;
  description: string;
  archetype: string;
  versionLabel: string;
  hash: string;
  tier: Tier;
  runtime: string;
  placement: string;
  capabilities: { id: string; risk: string }[];
  skills: string[];
  input: string | null;
  output: string | null;
  scorecard: Scorecard | null;
}

export interface AgentDetail {
  agent: AgentSummary;
  instructions: string;
  versions: { hash: string; versionLabel: string; source: string; firstSeenAt: string; current: boolean }[];
  scorecards: Scorecard[];
}

export type AssignmentState = 'queued' | 'running' | 'awaitingTriage' | 'completed' | 'failed' | 'cancelled';
export type PhaseState = 'pending' | 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
export type Decision = 'pending' | 'accepted' | 'rejected';

export interface AssignmentSummary {
  id: string;
  title: string;
  scenario: string;
  source: string;
  state: AssignmentState;
  ownerId: string;
  createdAt: string;
  completedAt: string | null;
  findings: number;
  undecided: number;
}

export interface Finding {
  id: string;
  agent: string;
  agentHash: string;
  rank: number;
  title: string;
  detail: string;
  recommendation: string;
  severity: 'info' | 'low' | 'medium' | 'high';
  filePath: string | null;
  confidence: number;
  decision: Decision;
  reason: string | null;
  decidedBy: string | null;
  decidedAt: string | null;
  invocationId: string;
}

export interface Step {
  id: string;
  phase: string;
  step: string;
  attempt: number;
  agent: string;
  agentHash: string;
  endpoint: string;
  model: string;
  strategy: string;
  outcome: string | null;
  inputTokens: number | null;
  outputTokens: number | null;
  durationMs: number | null;
  startedAt: string;
}

export interface AssignmentDetail {
  assignment: AssignmentSummary;
  endpointId: string | null;
  model: string | null;
  overrides: unknown;
  error: string | null;
  cancelRequested: boolean;
  phases: { key: string; order: number; state: PhaseState; attempt: number; startedAt: string | null; completedAt: string | null; error: string | null }[];
  findings: Finding[];
  steps: Step[];
  retroSessionId: string | null;
}

export interface StepDetail {
  step: Step;
  input: string;
  output: unknown;
  rawText: string | null;
  error: string | null;
  toolCalls: { name: string; arguments: unknown; result: unknown }[] | null;
  reasoning: string | null;
  traceId: string | null;
}

export type Sentiment = 'good' | 'bad' | 'ugly';

export interface RetroMessage {
  id: string;
  sequence: number;
  role: 'user' | 'assistant';
  content: string;
  inProgress: boolean;
  createdAt: string;
}

export interface RetroCard {
  id: string;
  sentiment: Sentiment;
  text: string;
  agent: string | null;
  state: 'draft' | 'confirmed' | 'discarded';
  assistedBy: string | null;
  authorName: string | null;
  authorTitle: string | null;
  createdAt: string;
  confirmedAt: string | null;
}

export interface Retro {
  sessionId: string;
  assignmentId: string;
  state: 'open' | 'closed';
  messages: RetroMessage[];
  cards: RetroCard[];
}
