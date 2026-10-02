import { useState, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router";
import { api, type RunDetail } from "../lib/api";
import { useAction, useLiveData } from "../lib/hooks";
import { Stepper } from "../components/Stepper";
import { Backlog, DecisionBar, InterviewPanel, MergeRequestPanel, ReviewPanel, latest } from "../components/RunPanels";
import { Button, Card, ErrorText, Markdown, Pill, StatusBadge, timeAgo } from "../components/ui";

/**
 * The workflow screen. Top: a stepper showing the phases. Middle: the "what now?" card for the current
 * phase (the agents' output plus the decision you need to make). Bottom: documents, backlog, checkpoints,
 * the audit trail of human decisions, and the live activity log from the workflow executors.
 */
export function RunPage() {
  const { id = "" } = useParams();
  const { data, error, reload } = useLiveData(() => api.run(id), `/api/runs/${id}/events`, [id]);

  if (error && !data) return <ErrorText>{error}</ErrorText>;
  if (!data) return <p className="text-sm text-slate-500">Loading...</p>;

  return (
    <div className="mx-auto max-w-6xl space-y-4">
      <Header detail={data} />
      <Card><Stepper steps={data.pipeline} /></Card>
      <CurrentPhase detail={data} onDone={reload} />
      <Details detail={data} />
    </div>
  );
}

function Header({ detail }: { detail: RunDetail }) {
  const navigate = useNavigate();
  const { busy, error, run } = useAction();
  const r = detail.run;
  return (
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div>
        <div className="flex items-center gap-2">
          <h1 className="text-xl font-semibold">{r.name}</h1>
          <StatusBadge status={r.status} />
          <Pill tone={r.source === "ExistingApp" ? "amber" : "indigo"}>{r.source === "ExistingApp" ? "Existing app" : "New idea"}</Pill>
        </div>
        <p className="mt-1 text-xs text-slate-500">
          Workspace: <code>{detail.workspacePath}</code>
          {detail.sourcePath && <> | Source: <code>{detail.sourcePath}</code></>}
        </p>
        <ErrorText>{error}</ErrorText>
      </div>
      <Button variant="ghost" disabled={busy || r.status === "Running"}
        onClick={() => confirm(`Delete "${r.name}"?`) && run(() => api.deleteRun(r.id)).then((ok) => ok && navigate("/"))}>
        Delete
      </Button>
    </div>
  );
}

function CurrentPhase({ detail, onDone }: { detail: RunDetail; onDone: () => void }) {
  const r = detail.run;
  const { busy, error, run } = useAction();
  const waiting = r.status === "AwaitingApproval";
  const lastEvent = detail.events[0];

  const banner = (
    <div className={`mb-3 rounded-md px-3 py-2 text-sm ${r.status === "Failed" ? "bg-rose-50 text-rose-800" : r.status === "Running" ? "bg-sky-50 text-sky-800" : r.status === "Completed" ? "bg-emerald-50 text-emerald-800" : "bg-amber-50 text-amber-900"}`}>
      <b>{r.phaseName}:</b> {r.statusMessage}
      {r.status === "Running" && lastEvent && <div className="mt-1 text-xs opacity-80">Latest: {lastEvent.message}</div>}
      {r.status === "Failed" && (
        <div className="mt-2">
          <Button variant="danger" disabled={busy} onClick={() => run(() => api.retry(r.id)).then((ok) => ok && onDone())}>Retry this phase</Button>
          <ErrorText>{error}</ErrorText>
        </div>
      )}
    </div>
  );

  let body: ReactNode = null;
  switch (r.phase) {
    case "Requirements":
      body = <InterviewPanel detail={detail} onDone={onDone} />;
      break;
    case "Review":
      body = <ReviewPanel detail={detail} onDone={onDone} />;
      break;
    case "AgilePlan":
      body = waiting && (
        <>
          <Backlog epics={detail.epics} showTasks={false} />
          <div className="mt-3"><DecisionBar runId={r.id} approveLabel="Approve plan and break into tasks" onDone={onDone} /></div>
        </>
      );
      break;
    case "DevPlan":
      body = waiting && (
        <>
          <Backlog epics={detail.epics} showTasks />
          <div className="mt-3"><DecisionBar runId={r.id} approveLabel="Approve tasks and start developing" onDone={onDone} /></div>
        </>
      );
      break;
    case "Develop":
      body = (
        <>
          <MergeRequestPanel detail={detail} />
          {waiting && <div className="mt-3"><DecisionBar runId={r.id} approveLabel="Approve MR: merge and continue" rejectLabel="Reject: redo story with feedback" onDone={onDone} /></div>}
        </>
      );
      break;
    case "Testing": {
      const prompts = latest(detail.artifacts, "test-prompts");
      body = waiting && prompts && (
        <>
          <Markdown>{prompts.content}</Markdown>
          <div className="mt-3"><DecisionBar runId={r.id} approveLabel="Approve and finish" onDone={onDone} /></div>
        </>
      );
      break;
    }
    case "Done":
      body = <p className="text-sm text-slate-600">All phases are complete. The generated specs, plan, code (as a git repository) and tester prompts are in the workspace folder above.</p>;
      break;
  }

  return <Card title="What happens now">{banner}{body}</Card>;
}

function Details({ detail }: { detail: RunDetail }) {
  const tabs = ["Documents", "Backlog", "Checkpoints", "Decisions", "Activity"] as const;
  const [tab, setTab] = useState<(typeof tabs)[number]>("Documents");
  const kinds = [...new Set(detail.artifacts.map((a) => a.kind))];
  const [kind, setKind] = useState<string>();
  const doc = latest(detail.artifacts, kind ?? kinds[0] ?? "");

  return (
    <Card>
      <div className="mb-3 flex gap-1 border-b border-slate-200">
        {tabs.map((t) => (
          <button key={t} onClick={() => setTab(t)}
            className={`px-3 py-1.5 text-sm ${tab === t ? "border-b-2 border-indigo-600 font-medium text-indigo-700" : "text-slate-500"}`}>{t}</button>
        ))}
      </div>

      {tab === "Documents" && (kinds.length === 0 ? <p className="text-sm text-slate-500">No documents yet.</p> : (
        <div className="flex gap-4">
          <ul className="w-48 shrink-0 space-y-1 text-sm">
            {kinds.map((k) => {
              const a = latest(detail.artifacts, k);
              return (
                <li key={k}>
                  <button onClick={() => setKind(k)} className={`w-full rounded px-2 py-1 text-left ${doc?.kind === k ? "bg-indigo-50 text-indigo-700" : "hover:bg-slate-50"}`}>
                    {a.title} <span className="text-xs text-slate-400">v{a.version}</span>
                  </button>
                </li>
              );
            })}
          </ul>
          <div className="min-w-0 flex-1">
            {doc && <p className="mb-2 text-xs text-slate-500">{doc.relativePath} | {doc.phase} | {timeAgo(doc.createdAt)}</p>}
            {doc && <Markdown>{doc.content}</Markdown>}
          </div>
        </div>
      ))}

      {tab === "Backlog" && <Backlog epics={detail.epics} showTasks />}

      {tab === "Checkpoints" && (
        detail.checkpoints.length === 0 ? <p className="text-sm text-slate-500">No commits yet.</p> : (
          <ul className="space-y-1 text-sm">
            {detail.checkpoints.map((c) => (
              <li key={c.id} className="flex gap-2">
                <Pill tone={c.kind === "MergeRequest" ? "indigo" : "slate"}>{c.kind === "MergeRequest" ? "MR" : "commit"}</Pill>
                {c.sha && <code className="text-xs">{c.sha}</code>}
                <span>{c.title}</span>
                <span className="text-xs text-slate-400">{c.branch}</span>
              </li>
            ))}
          </ul>
        ))}

      {tab === "Decisions" && (
        <div className="space-y-4 text-sm">
          <p className="text-xs text-slate-500">Every human decision is stored next to the AI output it was about. This is your audit trail, and a data set for improving the prompts.</p>
          <ul className="space-y-1">
            {detail.decisions.map((d) => (
              <li key={d.id} className="flex gap-2">
                <Pill tone={d.approved ? "emerald" : "rose"}>{d.approved ? "Approved" : "Rejected"}</Pill>
                <span className="font-medium">{d.phase}</span>
                <span className="text-slate-600">{d.reason || <i>no reason given</i>}</span>
                <span className="text-xs text-slate-400">{timeAgo(d.createdAt)}</span>
              </li>
            ))}
            {detail.findings.filter((f) => f.status !== "Pending").map((f) => (
              <li key={`f${f.id}`} className="flex gap-2">
                <Pill tone={f.status === "Accepted" ? "emerald" : "rose"}>{f.status}</Pill>
                <span className="font-medium">Finding (round {f.round})</span>
                <span>{f.title}</span>
                <span className="text-slate-600">{f.decisionReason}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {tab === "Activity" && (
        <ul className="max-h-96 space-y-0.5 overflow-y-auto font-mono text-xs">
          {detail.events.map((e) => (
            <li key={e.id} className={e.level === "error" ? "text-rose-600" : e.level === "tool" ? "text-indigo-700" : e.level === "step" ? "text-slate-400" : "text-slate-700"}>
              <span className="text-slate-400">{new Date(e.createdAt).toLocaleTimeString()}</span> [{e.phase}] {e.message}
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
