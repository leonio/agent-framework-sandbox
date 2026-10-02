import type { AssignmentState, Decision, Finding, PhaseState } from '../api/types';
import { Badge, type Tone } from './ui';

/** How states read and look everywhere: words people use, and one colour per meaning. */

const assignmentStates: Record<AssignmentState, [string, Tone]> = {
  queued: ['Queued', 'slate'],
  running: ['Running', 'indigo'],
  awaitingTriage: ['Waiting for you', 'amber'],
  completed: ['Completed', 'emerald'],
  failed: ['Failed', 'rose'],
  cancelled: ['Cancelled', 'slate'],
};

export function AssignmentStateBadge({ state }: { state: AssignmentState }) {
  const [label, tone] = assignmentStates[state];
  return <Badge tone={tone}>{label}</Badge>;
}

export const phaseTone: Record<PhaseState, Tone> = {
  pending: 'slate',
  queued: 'slate',
  running: 'indigo',
  succeeded: 'emerald',
  failed: 'rose',
  cancelled: 'slate',
};

const severityTones: Record<Finding['severity'], Tone> = { info: 'slate', low: 'sky', medium: 'amber', high: 'rose' };

export function SeverityBadge({ severity }: { severity: Finding['severity'] }) {
  return <Badge tone={severityTones[severity]}>{severity}</Badge>;
}

export function DecisionBadge({ decision }: { decision: Decision }) {
  if (decision === 'pending') return <Badge tone="amber">To decide</Badge>;
  return decision === 'accepted' ? <Badge tone="emerald">Accepted</Badge> : <Badge tone="rose">Rejected</Badge>;
}

/** A ledger outcome: null means the call is still running (or was interrupted). */
export function OutcomeBadge({ outcome }: { outcome: string | null }) {
  switch (outcome) {
    case null:
      return <Badge tone="indigo">running</Badge>;
    case 'succeeded':
      return <Badge tone="emerald">succeeded</Badge>;
    case 'repaired':
      return <Badge tone="amber">repaired</Badge>;
    default:
      return <Badge tone="rose">{outcome}</Badge>;
  }
}

export const shortHash = (hash: string) => hash.slice(0, 8);
