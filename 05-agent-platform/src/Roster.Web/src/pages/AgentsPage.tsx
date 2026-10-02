import { Link } from 'react-router';
import { useAgents } from '../api/queries';
import { ScorecardView } from '../components/ScorecardView';
import { shortHash } from '../components/status';
import { Badge, Card, ErrorNote, Spinner } from '../components/ui';

/**
 * The agent library: every agent, what it may do (its capabilities and their risk), where it may run (the minimum
 * placement those imply), and the scorecard of the version running today.
 */
export function AgentsPage() {
  const agents = useAgents();
  if (agents.isPending) return <Spinner />;
  if (agents.error) return <ErrorNote error={agents.error} />;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Agents</h1>
        <p className="text-sm text-slate-500">
          Each agent is a package in the library. Its version is a hash of what it is told and what it may do, so every
          scorecard below belongs to exactly the behaviour you see.
        </p>
      </div>
      {agents.data.map(a => (
        <Card
          key={a.name}
          title={
            <Link to={`/agents/${a.name}`} className="hover:underline">
              {a.name}
            </Link>
          }
          actions={
            <span className="flex items-center gap-2 text-xs text-slate-500">
              v{a.versionLabel} <span className="font-mono">{shortHash(a.hash)}</span>
            </span>
          }
        >
          <p className="text-sm text-slate-600 dark:text-slate-400">{a.description}</p>
          <div className="mt-3 flex flex-wrap gap-2">
            <Badge tone="indigo">{a.archetype || 'agent'}</Badge>
            <Badge>tier: {a.tier}</Badge>
            <Badge>runs: {a.placement}</Badge>
            {a.capabilities.length === 0 ? <Badge>no tools</Badge> : a.capabilities.map(c => <Badge key={c.id} tone="sky">{c.id} ({c.risk})</Badge>)}
            {a.input && <Badge>{a.input} → {a.output}</Badge>}
          </div>
          <div className="mt-4">
            {a.scorecard ? <ScorecardView card={a.scorecard} /> : <p className="text-sm text-slate-500">This version has not run yet.</p>}
          </div>
        </Card>
      ))}
    </div>
  );
}
