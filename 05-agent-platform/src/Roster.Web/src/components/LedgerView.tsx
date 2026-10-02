import { useState } from 'react';
import { useStep } from '../api/queries';
import type { Step } from '../api/types';
import { OutcomeBadge, shortHash } from './status';
import { Empty, ErrorNote, Spinner } from './ui';

/**
 * The ledger: every agent call in this assignment, in order, with the exact agent version (its hash), the endpoint and
 * model that served it, the output strategy, the outcome, tokens and time. Open a row to see exactly what the model
 * was given (untrusted text still inside its fences), what it returned, the tools it called and its reasoning, when the
 * endpoint returned any.
 */
export function LedgerView({ assignmentId, steps }: { assignmentId: string; steps: Step[] }) {
  const [open, setOpen] = useState<string | null>(null);
  if (steps.length === 0) return <Empty>No agent calls yet.</Empty>;

  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-800">
      <table className="w-full text-left text-sm">
        <thead className="bg-slate-50 text-xs uppercase text-slate-500 dark:bg-slate-900">
          <tr>
            <th className="px-3 py-2">Phase</th>
            <th className="px-3 py-2">Agent</th>
            <th className="px-3 py-2">Model</th>
            <th className="px-3 py-2">Output</th>
            <th className="px-3 py-2">Outcome</th>
            <th className="px-3 py-2 text-right">Tokens</th>
            <th className="px-3 py-2 text-right">Time</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {steps.map(s => (
            <LedgerRow key={s.id} step={s} assignmentId={assignmentId} open={open === s.id} onToggle={() => setOpen(open === s.id ? null : s.id)} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function LedgerRow({ step, assignmentId, open, onToggle }: { step: Step; assignmentId: string; open: boolean; onToggle: () => void }) {
  return (
    <>
      <tr className="cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" onClick={onToggle} data-testid="ledger-row">
        <td className="px-3 py-2 text-slate-500">
          {step.phase}
          {step.attempt > 1 && <span className="text-xs"> #{step.attempt}</span>}
        </td>
        <td className="px-3 py-2">
          {step.agent} <span className="font-mono text-xs text-slate-400">{shortHash(step.agentHash)}</span>
        </td>
        <td className="px-3 py-2 text-slate-600 dark:text-slate-400">
          {step.endpoint}/{step.model}
        </td>
        <td className="px-3 py-2 text-slate-600 dark:text-slate-400">{step.strategy}</td>
        <td className="px-3 py-2">
          <OutcomeBadge outcome={step.outcome} />
        </td>
        <td className="px-3 py-2 text-right tabular-nums text-slate-600 dark:text-slate-400">
          {step.inputTokens ?? '–'} / {step.outputTokens ?? '–'}
        </td>
        <td className="px-3 py-2 text-right tabular-nums text-slate-600 dark:text-slate-400">
          {step.durationMs === null ? '–' : `${(step.durationMs / 1000).toFixed(1)} s`}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} className="bg-slate-50 px-3 py-4 dark:bg-slate-900/60">
            <StepDetailView assignmentId={assignmentId} stepId={step.id} />
          </td>
        </tr>
      )}
    </>
  );
}

function StepDetailView({ assignmentId, stepId }: { assignmentId: string; stepId: string }) {
  const detail = useStep(assignmentId, stepId);
  if (detail.isPending) return <Spinner />;
  if (detail.error) return <ErrorNote error={detail.error} />;
  const d = detail.data;

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Block title="Input, as the model saw it" text={d.input} />
      <Block title="Output" text={d.output ? JSON.stringify(d.output, null, 2) : d.rawText ?? '(none)'} />
      {d.reasoning && <Block title="Reasoning" text={d.reasoning} />}
      {d.toolCalls && <Block title="Tool calls" text={JSON.stringify(d.toolCalls, null, 2)} />}
      {d.error && <Block title="Error" text={d.error} />}
      {d.traceId && <p className="text-xs text-slate-500">Trace {d.traceId} (in the Aspire dashboard)</p>}
    </div>
  );
}

function Block({ title, text }: { title: string; text: string }) {
  return (
    <div className="min-w-0">
      <p className="mb-1 text-xs font-semibold uppercase text-slate-500">{title}</p>
      <pre className="max-h-80 overflow-auto whitespace-pre-wrap rounded-lg bg-white p-3 font-mono text-xs dark:bg-slate-950">{text}</pre>
    </div>
  );
}
