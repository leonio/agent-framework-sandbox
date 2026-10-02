import { Link, useParams } from 'react-router';
import { useAgent } from '../api/queries';
import { ScorecardView } from '../components/ScorecardView';
import { shortHash } from '../components/status';
import { Badge, Card, ErrorNote, Spinner, timeAgo } from '../components/ui';

/** One agent: its instructions, every version that has run, and a scorecard per version, split by who gave feedback. */
export function AgentPage() {
  const { name = '' } = useParams();
  const agent = useAgent(name);
  if (agent.isPending) return <Spinner />;
  if (agent.error) return <ErrorNote error={agent.error} />;
  const { agent: a, instructions, versions, scorecards } = agent.data;

  return (
    <div className="space-y-6">
      <div>
        <Link to="/agents" className="text-sm text-indigo-600 hover:underline">
          ← Agents
        </Link>
        <h1 className="mt-1 text-2xl font-semibold tracking-tight">{a.name}</h1>
        <p className="text-sm text-slate-500">{a.description}</p>
      </div>

      {/* Full width, stacked: the instructions are wrapped at about 120 characters in their AGENT.md, and a narrower
          card wraps them a second time into ragged lines. */}
      <div className="space-y-6">
        <Card title="Instructions (current version)">
          <pre className="max-h-[28rem] overflow-auto whitespace-pre-wrap font-mono text-xs">{instructions}</pre>
          <p className="mt-3 text-xs text-slate-500">Skills appended at run time: {a.skills.join(', ') || 'none'}.</p>
        </Card>
        <Card title="Versions">
          <ul className="space-y-2 text-sm">
            {versions.map(v => (
              <li key={v.hash} className="flex items-center gap-2">
                <span className="font-mono text-xs">{shortHash(v.hash)}</span>
                <span>v{v.versionLabel}</span>
                {v.current && <Badge tone="emerald">current</Badge>}
                <span className="ml-auto text-xs text-slate-500">{timeAgo(v.firstSeenAt)}</span>
              </li>
            ))}
            {versions.length === 0 && <li className="text-slate-500">No version has run yet.</li>}
          </ul>
        </Card>
      </div>

      {scorecards.map(card => (
        <Card key={card.hash} title={`Scorecard · v${card.versionLabel} · ${shortHash(card.hash)}`}>
          <ScorecardView card={card} detailed />
        </Card>
      ))}
    </div>
  );
}
