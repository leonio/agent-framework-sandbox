// Typed client for the SDLC Studio API. The types mirror Endpoints/Dtos.cs.
// ALTERNATIVE: generate this file from /openapi/v1.json (e.g. openapi-typescript or Kiota) so the
// contract can never drift. Hand-written here to keep the sample's moving parts visible.

export type Phase =
  | "Requirements" | "Import" | "Design" | "Review" | "AgilePlan" | "DevPlan" | "Develop" | "Testing" | "Done";
export type RunStatus = "Running" | "AwaitingInput" | "AwaitingApproval" | "Failed" | "Completed";
export type TemplateKind = "Instruction" | "Prompt" | "Skill";

export interface RunSummary {
  id: string; name: string; source: "NewIdea" | "ExistingApp"; phase: Phase; phaseName: string;
  status: RunStatus; statusMessage?: string; createdAt: string; updatedAt: string;
}
export interface PhaseStep { phase: Phase; name: string; state: "done" | "active" | "waiting" | "failed" | "upcoming" }
export interface Message { id: number; role: "user" | "assistant"; content: string; createdAt: string }
export interface Artifact { id: number; phase: Phase; kind: string; title: string; content: string; relativePath?: string; version: number; createdAt: string }
export interface Finding {
  id: number; round: number; reviewer: string; severity: string; title: string; detail: string;
  status: "Pending" | "Accepted" | "Rejected"; decisionReason?: string;
}
export interface Task { id: number; order: number; title: string; detail: string; files: string; status: string; commitSha?: string }
export interface Story {
  id: number; key: string; title: string; userStory: string; acceptanceCriteria: string[]; points: number; sprint: number;
  status: "Todo" | "InProgress" | "Done"; externalId?: string; tasks: Task[];
}
export interface Epic { id: number; key: string; title: string; description: string; externalId?: string; stories: Story[] }
export interface Checkpoint { id: number; storyId?: number; kind: "Commit" | "MergeRequest"; title: string; detail: string; branch?: string; sha?: string; createdAt: string }
export interface Decision { id: number; phase: Phase; approved: boolean; reason: string; createdAt: string }
export interface RunEvent { id: number; phase: Phase; level: string; message: string; createdAt: string }

export interface RunDetail {
  run: RunSummary; workspacePath: string; sourcePath?: string; reviewRound: number; pipeline: PhaseStep[];
  messages: Message[]; artifacts: Artifact[]; findings: Finding[]; epics: Epic[]; checkpoints: Checkpoint[];
  decisions: Decision[]; events: RunEvent[];
}
export interface Template {
  id: number; key: string; kind: TemplateKind; title: string; description: string; content: string; skills: string;
  version: number; updatedAt: string;
}
export interface Meta { llmProvider: string; model: string; integrationsMode: string; database: string }

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: init?.body instanceof FormData ? init.headers : { "content-type": "application/json", ...init?.headers },
  });
  if (!res.ok) {
    // The API returns RFC 9457 ProblemDetails; surface the most useful message.
    const problem = await res.json().catch(() => null);
    const validation = problem?.errors ? Object.values(problem.errors).flat().join(" ") : null;
    throw new Error(validation || problem?.detail || problem?.title || `${res.status} ${res.statusText}`);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

const post = <T>(path: string, body?: unknown) =>
  request<T>(path, { method: "POST", body: body instanceof FormData ? body : JSON.stringify(body ?? {}) });

export const api = {
  meta: () => request<Meta>("/api/meta"),
  runs: () => request<RunSummary[]>("/api/runs"),
  run: (id: string) => request<RunDetail>(`/api/runs/${id}`),
  deleteRun: (id: string) => request<void>(`/api/runs/${id}`, { method: "DELETE" }),
  start: (name: string, idea: string) => post<RunSummary>("/api/runs", { name, idea }),
  importPath: (name: string, path: string) => post<RunSummary>("/api/runs/import", { name, path }),
  importZip: (name: string, file: File) => {
    const form = new FormData();
    form.append("name", name);
    form.append("file", file);
    return post<RunSummary>("/api/runs/import/upload", form);
  },
  answer: (id: string, answer: string) => post<RunSummary>(`/api/runs/${id}/answer`, { answer }),
  approve: (id: string, reason: string) => post<RunSummary>(`/api/runs/${id}/approve`, { reason }),
  reject: (id: string, reason: string) => post<RunSummary>(`/api/runs/${id}/reject`, { reason }),
  revise: (id: string) => post<RunSummary>(`/api/runs/${id}/revise`),
  retry: (id: string) => post<RunSummary>(`/api/runs/${id}/retry`),
  decideFinding: (id: string, findingId: number, accepted: boolean, reason: string) =>
    post<Finding>(`/api/runs/${id}/findings/${findingId}`, { accepted, reason }),

  templates: () => request<Template[]>("/api/admin/templates"),
  saveTemplate: (t: Partial<Template> & Pick<Template, "key" | "kind" | "title" | "content">) =>
    t.id
      ? request<Template>(`/api/admin/templates/${t.id}`, { method: "PUT", body: JSON.stringify(t) })
      : post<Template>("/api/admin/templates", t),
  deleteTemplate: (id: number) => request<void>(`/api/admin/templates/${id}`, { method: "DELETE" }),
  resetTemplates: () => post<{ updated: number }>("/api/admin/templates/reset"),
};
