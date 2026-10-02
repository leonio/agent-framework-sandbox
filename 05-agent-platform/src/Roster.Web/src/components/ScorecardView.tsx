import type { Scorecard } from '../api/types';

/**
 * A scorecard: how one version of one agent has done. A row of stat tiles (calls, acceptance, findings, tokens, time),
 * a meter for the acceptance rate, the Good / Bad / Ugly counts from confirmed retro cards, and the same feedback split
 * by the giver's title.
 *
 * Visual choices (checked with the dataviz palette validator): the meter is one hue, the accent on a lighter track of
 * the same ramp, with the number written beside it, so it needs no legend. Sentiment dots use the 500 steps in light
 * mode and the 600 steps in dark, and always sit next to their word, so meaning never rests on colour alone.
 */

const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });

export function ScorecardView({ card, detailed = false }: { card: Scorecard; detailed?: boolean }) {
  const decided = card.accepted + card.rejected;

  return (
    <div className="space-y-4" data-testid="scorecard">
      <dl className="grid grid-cols-2 gap-3 sm:grid-cols-5">
        <Tile label="Calls" value={compact.format(card.calls)} note={card.repaired + card.failed > 0 ? `${card.repaired} repaired, ${card.failed} failed` : undefined} />
        <Tile label="Accepted" value={card.acceptanceRate === null ? '–' : `${Math.round(card.acceptanceRate * 100)}%`} note={decided > 0 ? `${card.accepted} of ${decided} decided` : 'nothing decided yet'} />
        <Tile label="Findings" value={compact.format(card.findings)} note={card.undecided > 0 ? `${card.undecided} undecided` : undefined} />
        <Tile label="Tokens" value={compact.format(card.inputTokens + card.outputTokens)} note={`${compact.format(card.inputTokens)} in, ${compact.format(card.outputTokens)} out`} />
        <Tile label="Average time" value={card.averageDurationMs === null ? '–' : `${(card.averageDurationMs / 1000).toFixed(1)} s`} />
      </dl>

      {card.acceptanceRate !== null && <AcceptanceMeter rate={card.acceptanceRate} accepted={card.accepted} decided={decided} />}

      <Sentiments good={card.good} bad={card.bad} ugly={card.ugly} />

      {detailed && card.byTitle.length > 0 && (
        <table className="w-full text-left text-sm">
          <caption className="mb-1 text-left text-xs font-semibold uppercase text-slate-500">By the title of who gave the feedback</caption>
          <thead className="text-xs text-slate-500">
            <tr>
              <th className="py-1 font-medium">Title</th>
              <th className="py-1 text-right font-medium">Accepted</th>
              <th className="py-1 text-right font-medium">Rejected</th>
              <th className="py-1 text-right font-medium">Good</th>
              <th className="py-1 text-right font-medium">Bad</th>
              <th className="py-1 text-right font-medium">Ugly</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 tabular-nums dark:divide-slate-800">
            {card.byTitle.map(t => (
              <tr key={t.title}>
                <td className="py-1">{t.title}</td>
                <td className="py-1 text-right">{t.accepted}</td>
                <td className="py-1 text-right">{t.rejected}</td>
                <td className="py-1 text-right">{t.good}</td>
                <td className="py-1 text-right">{t.bad}</td>
                <td className="py-1 text-right">{t.ugly}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function Tile({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/50">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="text-lg font-semibold">{value}</dd>
      {note && <dd className="text-xs text-slate-500">{note}</dd>}
    </div>
  );
}

function AcceptanceMeter({ rate, accepted, decided }: { rate: number; accepted: number; decided: number }) {
  const percent = Math.round(rate * 100);
  return (
    <div title={`${accepted} of ${decided} decided findings accepted`}>
      <div className="mb-1 flex justify-between text-xs text-slate-500">
        <span>Findings accepted</span>
        <span>{percent}%</span>
      </div>
      <div
        className="h-2 overflow-hidden rounded-full bg-indigo-100 dark:bg-indigo-950"
        role="meter"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        aria-label="Findings accepted"
      >
        <div className="h-full rounded-full bg-indigo-600 dark:bg-indigo-500" style={{ width: `${percent}%` }} />
      </div>
    </div>
  );
}

function Sentiments({ good, bad, ugly }: { good: number; bad: number; ugly: number }) {
  const items: [string, number, string][] = [
    ['Good', good, 'bg-emerald-500 dark:bg-emerald-600'],
    ['Bad', bad, 'bg-amber-500 dark:bg-amber-600'],
    ['Ugly', ugly, 'bg-rose-500 dark:bg-rose-600'],
  ];
  return (
    <p className="flex flex-wrap gap-4 text-sm text-slate-700 dark:text-slate-300">
      <span className="text-xs text-slate-500">Retro cards</span>
      {items.map(([label, count, dot]) => (
        <span key={label} className="flex items-center gap-1.5">
          <span className={`h-2 w-2 rounded-full ${dot}`} aria-hidden />
          {label} {count}
        </span>
      ))}
    </p>
  );
}
