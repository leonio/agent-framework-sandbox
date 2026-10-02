// Phase-specific panels for the workflow screen. Each panel shows what the agents produced for the
// current phase and the decision the human has to make.
import { useState } from "react";
import { api, type Artifact, type Epic, type Finding, type RunDetail } from "../lib/api";
import { useAction } from "../lib/hooks";
import { Button, Card, ErrorText, Markdown, Pill, inputClass } from "./ui";

export const latest = (artifacts: Artifact[], kind: string) =>
  artifacts.filter((a) => a.kind === kind).sort((a, b) => b.version - a.version)[0];

/** Approve / reject with a reason. Reasons are stored with the AI output and fed back to the agents. */
export function DecisionBar({ runId, approveLabel, rejectLabel = "Reject with feedback", onDone }: {
  runId: string; approveLabel: string; rejectLabel?: string; onDone: () => void;
}) {
  const [reason, setReason] = useState("");
  const { busy, error, run } = useAction();
  const act = (fn: () => Promise<unknown>) => run(fn).then((ok) => ok && (setReason(""), onDone()));

  return (
    <div className="space-y-2 rounded-md border border-amber-200 bg-amber-50 p-3">
      <label className="block text-sm font-medium text-amber-900">
        Why? <span className="font-normal text-amber-800">(optional to approve, required to reject; saved for the audit trail)</span>
        <textarea className={inputClass} rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>
      <ErrorText>{error}</ErrorText>
      <div className="flex gap-2">
        <Button disabled={busy} onClick={() => act(() => api.approve(runId, reason))}>{approveLabel}</Button>
        <Button variant="secondary" disabled={busy || !reason.trim()} onClick={() => act(() => api.reject(runId, reason))}>{rejectLabel}</Button>
      </div>
    </div>
  );
}

/** Requirements phase: a chat with the "grill me" interviewer agent. */
export function InterviewPanel({ detail, onDone }: { detail: RunDetail; onDone: () => void }) {
  const [answer, setAnswer] = useState("");
  const { busy, error, run } = useAction();
  const waiting = detail.run.status === "AwaitingInput" || detail.run.status === "AwaitingApproval";
  const send = (text: string) => run(() => api.answer(detail.run.id, text)).then((ok) => ok && (setAnswer(""), onDone()));
  const requirements = latest(detail.artifacts, "requirements");

  return (
    <div className="space-y-4">
      <div className="max-h-[28rem] space-y-3 overflow-y-auto rounded-md bg-slate-50 p-3">
        {detail.messages.map((m) => (
          <div key={m.id} className={`flex ${m.role === "user" ? "justify-end" : ""}`}>
            <div className={`max-w-[80%] rounded-lg px-3 py-2 text-sm shadow-sm ${m.role === "user" ? "bg-indigo-600 text-white" : "bg-white"}`}>
              {m.role === "user" ? m.content : <Markdown>{m.content}</Markdown>}
            </div>
          </div>
        ))}
        {detail.run.status === "Running" && <p className="text-xs text-slate-500">Interviewer is thinking...</p>}
      </div>

      {detail.run.status === "AwaitingInput" && (
        <div className="space-y-2">
          <textarea className={inputClass} rows={3} value={answer} onChange={(e) => setAnswer(e.target.value)}
            placeholder='Your answer (or just "yes" to accept the recommendation)'
            onKeyDown={(e) => e.key === "Enter" && (e.ctrlKey || e.metaKey) && answer.trim() && send(answer)} />
          <ErrorText>{error}</ErrorText>
          <div className="flex gap-2">
            <Button disabled={busy || !answer.trim()} onClick={() => send(answer)}>Send (Ctrl+Enter)</Button>
            <Button variant="secondary" disabled={busy} onClick={() => send("yes")}>Accept recommendation</Button>
            <Button variant="ghost" disabled={busy || !waiting} onClick={() => send("That's enough, please summarise the requirements now.")}>
              Wrap up now
            </Button>
          </div>
        </div>
      )}

      {detail.run.status === "AwaitingApproval" && requirements && (
        <>
          <Card title="Agreed requirements"><Markdown>{requirements.content}</Markdown></Card>
          <DecisionBar runId={detail.run.id} approveLabel="Approve requirements and write specs" rejectLabel="Not complete: keep interviewing" onDone={onDone} />
        </>
      )}
    </div>
  );
}

/** Review phase: per-finding accept/reject with reasons, then revise or approve. */
export function ReviewPanel({ detail, onDone }: { detail: RunDetail; onDone: () => void }) {
  const round = detail.findings.filter((f) => f.round === detail.reviewRound);
  const pending = round.filter((f) => f.status === "Pending").length;
  const { busy, error, run } = useAction();
  const spec = latest(detail.artifacts, "functional-spec");
  const design = latest(detail.artifacts, "technical-design");
  const [tab, setTab] = useState<"findings" | "spec" | "design">("findings");

  return (
    <div className="space-y-3">
      <div className="flex gap-1 border-b border-slate-200">
        {(["findings", "spec", "design"] as const).map((t) => (
          <button key={t} onClick={() => setTab(t)}
            className={`px-3 py-1.5 text-sm ${tab === t ? "border-b-2 border-indigo-600 font-medium text-indigo-700" : "text-slate-500"}`}>
            {t === "findings" ? `Findings (round ${detail.reviewRound})` : t === "spec" ? `Functional spec v${spec?.version ?? 0}` : `Technical design v${design?.version ?? 0}`}
          </button>
        ))}
      </div>

      {tab === "spec" && spec && <Markdown>{spec.content}</Markdown>}
      {tab === "design" && design && <Markdown>{design.content}</Markdown>}
      {tab === "findings" && (
        <div className="space-y-2">
          {round.length === 0 && <p className="text-sm text-slate-500">No findings this round.</p>}
          {round.map((f) => <FindingRow key={f.id} runId={detail.run.id} finding={f} onDone={onDone} />)}
        </div>
      )}

      {detail.run.status === "AwaitingApproval" && (
        <div className="space-y-2 rounded-md border border-amber-200 bg-amber-50 p-3">
          <p className="text-sm text-amber-900">
            {pending > 0
              ? `Decide on ${pending} more finding(s), then let the reviser agent apply the accepted ones.`
              : "All findings decided. Revise the spec with your decisions, or approve it as is."}
          </p>
          <ErrorText>{error}</ErrorText>
          <div className="flex gap-2">
            <Button disabled={busy || pending > 0 || round.length === 0} onClick={() => run(() => api.revise(detail.run.id)).then((ok) => ok && onDone())}>
              Revise spec with decisions
            </Button>
            <Button variant="secondary" disabled={busy} onClick={() => run(() => api.approve(detail.run.id, "Specs approved")).then((ok) => ok && onDone())}>
              Approve specs and plan
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

function FindingRow({ runId, finding, onDone }: { runId: string; finding: Finding; onDone: () => void }) {
  const [reason, setReason] = useState(finding.decisionReason ?? "");
  const { busy, run } = useAction();
  const decide = (accepted: boolean) => run(() => api.decideFinding(runId, finding.id, accepted, reason)).then((ok) => ok && onDone());
  const tone = finding.severity === "High" ? "rose" : finding.severity === "Medium" ? "amber" : "slate";

  return (
    <div className={`rounded-md border p-3 ${finding.status === "Accepted" ? "border-emerald-200 bg-emerald-50/50" : finding.status === "Rejected" ? "border-slate-200 bg-slate-50 opacity-75" : "border-slate-200"}`}>
      <div className="flex flex-wrap items-center gap-2">
        <Pill tone={tone}>{finding.severity}</Pill>
        <Pill>{finding.reviewer.replace("reviewer-", "")}</Pill>
        <span className="text-sm font-medium">{finding.title}</span>
        {finding.status !== "Pending" && <Pill tone={finding.status === "Accepted" ? "emerald" : "slate"}>{finding.status}</Pill>}
      </div>
      <p className="mt-1 text-sm text-slate-600">{finding.detail}</p>
      <div className="mt-2 flex gap-2">
        <input className={inputClass} placeholder="Reason (stored and given to the reviser agent)" value={reason} onChange={(e) => setReason(e.target.value)} />
        <Button variant="secondary" disabled={busy} onClick={() => decide(true)}>Accept</Button>
        <Button variant="secondary" disabled={busy} onClick={() => decide(false)}>Reject</Button>
      </div>
    </div>
  );
}

/** Agile plan and dev plan: epics, sprints, stories and (when present) tasks. */
export function Backlog({ epics, showTasks }: { epics: Epic[]; showTasks: boolean }) {
  if (epics.length === 0) return <p className="text-sm text-slate-500">No plan yet.</p>;
  return (
    <div className="space-y-4">
      {epics.map((e) => (
        <div key={e.id}>
          <h3 className="text-sm font-semibold">
            {e.key} {e.title} {e.externalId && <Pill tone="indigo">Jira {e.externalId}</Pill>}
          </h3>
          <p className="mb-2 text-xs text-slate-500">{e.description}</p>
          <div className="space-y-2">
            {e.stories.map((s) => (
              <div key={s.id} className="rounded-md border border-slate-200 p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-medium">{s.key} {s.title}</span>
                  <Pill>Sprint {s.sprint}</Pill>
                  <Pill>{s.points} pts</Pill>
                  {s.externalId && <Pill tone="indigo">{s.externalId}</Pill>}
                  <Pill tone={s.status === "Done" ? "emerald" : s.status === "InProgress" ? "amber" : "slate"}>{s.status}</Pill>
                </div>
                <p className="mt-1 text-sm text-slate-600">{s.userStory}</p>
                <ul className="mt-1 list-disc pl-5 text-xs text-slate-600">
                  {s.acceptanceCriteria.map((ac, i) => <li key={i}>{ac}</li>)}
                </ul>
                {showTasks && s.tasks.length > 0 && (
                  <ol className="mt-2 space-y-1 border-t border-slate-100 pt-2 text-xs">
                    {s.tasks.map((t) => (
                      <li key={t.id} className="flex gap-2">
                        <span className={t.status === "Done" ? "text-emerald-600" : "text-slate-400"}>{t.status === "Done" ? "✓" : "○"}</span>
                        <span><b>{t.title}</b>: {t.detail} {t.commitSha && <code className="text-indigo-700">{t.commitSha}</code>}</span>
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

/** Develop phase: the latest merge request checkpoint and its commits. */
export function MergeRequestPanel({ detail }: { detail: RunDetail }) {
  const mrs = detail.checkpoints.filter((c) => c.kind === "MergeRequest");
  const current = mrs[mrs.length - 1];
  if (!current) return <p className="text-sm text-slate-500">The developer and reviewer agents are working through the first story...</p>;
  // Commits recorded since the previous checkpoint belong to this MR (a rejected story is re-developed).
  const previousId = mrs.length > 1 ? mrs[mrs.length - 2].id : 0;
  const commits = detail.checkpoints.filter((c) => c.kind === "Commit" && c.id > previousId && c.id < current.id);
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2">
        {commits.map((c) => (
          <span key={c.id} className="rounded bg-slate-100 px-2 py-1 font-mono text-xs" title={c.detail}>{c.sha} {c.title}</span>
        ))}
      </div>
      <Markdown>{current.detail}</Markdown>
    </div>
  );
}
