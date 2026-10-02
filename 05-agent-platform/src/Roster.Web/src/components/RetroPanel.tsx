import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { useRetro } from '../api/queries';
import type { Retro, RetroCard, RetroMessage, Sentiment } from '../api/types';
import { nameAndTitle } from '../auth';
import { Badge, Button, Card, ErrorNote, field, Spinner, type Tone } from './ui';

/**
 * The retro tab (design doc section 11): a short conversation with the retro facilitator, and the cards it turns into.
 *
 * - The facilitator speaks first, starting from something that actually happened (a rejected finding, a retry).
 * - Its replies stream in: the API writes the growing reply into the database and the event stream says so, so the
 *   text appears as it is written.
 * - When you say "wrap up" it proposes Good, Bad and Ugly cards. They are drafts: edit the wording, the sentiment or the
 *   agent, then confirm or discard each one. Only confirmed cards count, and they carry your name and title.
 */

const sentimentTone: Record<Sentiment, Tone> = { good: 'emerald', bad: 'amber', ugly: 'rose' };

export function RetroPanel({ assignmentId, isOwner, agents }: { assignmentId: string; isOwner: boolean; agents: string[] }) {
  const queryClient = useQueryClient();
  const retro = useRetro(assignmentId);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['retro', assignmentId] });

  const open = useMutation({ mutationFn: () => api<Retro>(`/api/assignments/${assignmentId}/retro`, { method: 'POST' }), onSuccess: refresh });

  if (!isOwner) return <p className="text-sm text-slate-500">A retro is the assignment owner's conversation.</p>;
  if (retro.isPending) return <Spinner />;
  if (retro.error) return <ErrorNote error={retro.error} />;

  if (retro.data === null) {
    return (
      <Card>
        <h3 className="font-medium">How did the team do?</h3>
        <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
          A few minutes with the retro facilitator. It has read what happened here, asks a handful of questions, and drafts
          Good, Bad and Ugly cards for you to confirm. Your cards feed each agent's scorecard.
        </p>
        <div className="mt-4">
          <Button onClick={() => open.mutate()} disabled={open.isPending}>
            Start the retro
          </Button>
        </div>
        <ErrorNote error={open.error} />
      </Card>
    );
  }

  return (
    <div className="grid gap-6 lg:grid-cols-[1fr_22rem]">
      <Conversation retro={retro.data} onChange={refresh} />
      <Cards retro={retro.data} agents={agents} onChange={refresh} />
    </div>
  );
}

function Conversation({ retro, onChange }: { retro: Retro; onChange: () => void }) {
  const [text, setText] = useState('');
  const bottom = useRef<HTMLDivElement>(null);
  const last = retro.messages.at(-1);
  const waiting = !last || last.role === 'user' || last.inProgress;

  const send = useMutation({
    mutationFn: (message: string) => api(`/api/retros/${retro.sessionId}/messages`, { method: 'POST', body: { text: message } }),
    onSuccess: () => {
      setText('');
      onChange();
    },
  });

  // Keep the newest message in view as replies stream in.
  useEffect(() => bottom.current?.scrollIntoView({ block: 'nearest' }), [retro.messages.length, last?.content]);

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    if (text.trim() && !waiting) send.mutate(text.trim());
  };

  const onKey = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      submit();
    }
  };

  return (
    <Card title="Conversation">
      <div className="flex max-h-[32rem] min-h-64 flex-col gap-3 overflow-y-auto pr-1" data-testid="retro-messages">
        {retro.messages.map(m => (
          <Message key={m.id} message={m} />
        ))}
        {waiting && last?.role === 'user' && <p className="text-xs text-slate-400">The facilitator is thinking…</p>}
        <div ref={bottom} />
      </div>
      <form onSubmit={submit} className="mt-4 space-y-2 border-t border-slate-100 pt-4 dark:border-slate-800">
        <textarea
          className={field}
          rows={2}
          placeholder={waiting ? 'The facilitator is answering…' : 'Your answer (Enter to send, Shift+Enter for a new line)'}
          value={text}
          onChange={e => setText(e.target.value)}
          onKeyDown={onKey}
          disabled={waiting}
          data-testid="retro-input"
        />
        <div className="flex justify-between gap-2">
          <Button type="button" variant="ghost" disabled={waiting || send.isPending} onClick={() => send.mutate("Let's wrap up and draft the cards.")}>
            Wrap up
          </Button>
          <Button type="submit" disabled={waiting || !text.trim() || send.isPending}>
            Send
          </Button>
        </div>
        <ErrorNote error={send.error} />
      </form>
    </Card>
  );
}

function Message({ message }: { message: RetroMessage }) {
  // The first message stands for the person opening the retro; it is shown as a note, not as something they said.
  if (message.sequence === 0 && message.role === 'user') {
    return <p className="text-center text-xs text-slate-400">Retro opened</p>;
  }

  const mine = message.role === 'user';
  return (
    <div className={`flex ${mine ? 'justify-end' : 'justify-start'}`}>
      <div
        className={`max-w-[85%] whitespace-pre-wrap rounded-2xl px-4 py-2 text-sm ${mine ? 'bg-indigo-600 text-white' : 'bg-slate-100 text-slate-800 dark:bg-slate-800 dark:text-slate-100'}`}
        data-role={message.role}
        data-in-progress={message.inProgress}
      >
        {message.content || (message.inProgress ? '…' : '')}
        {message.inProgress && <span className="ml-0.5 inline-block w-1.5 animate-pulse">▍</span>}
      </div>
    </div>
  );
}

function Cards({ retro, agents, onChange }: { retro: Retro; agents: string[]; onChange: () => void }) {
  const drafts = retro.cards.filter(c => c.state === 'draft');
  const confirmed = retro.cards.filter(c => c.state === 'confirmed');

  return (
    <div className="space-y-6">
      {drafts.length > 0 && (
        <Card title={`Drafts to confirm (${drafts.length})`}>
          <div className="space-y-4">
            {drafts.map(c => (
              <DraftCard key={c.id} card={c} agents={agents} onChange={onChange} />
            ))}
          </div>
        </Card>
      )}
      <Card title={`Your cards (${confirmed.length})`}>
        {confirmed.length === 0 && <p className="text-sm text-slate-500">None yet. Confirm a draft, or write one below.</p>}
        <ul className="space-y-3">
          {confirmed.map(c => (
            <li key={c.id} className="text-sm" data-testid="confirmed-card">
              <div className="flex items-center gap-2">
                <Badge tone={sentimentTone[c.sentiment]}>{c.sentiment}</Badge>
                {c.agent && <span className="text-xs text-slate-500">{c.agent}</span>}
              </div>
              <p className="mt-1">{c.text}</p>
              <p className="text-xs text-slate-500">
                {nameAndTitle(c.authorName ?? 'You', c.authorTitle)}
                {c.assistedBy && ' · drafted with the facilitator'}
              </p>
            </li>
          ))}
        </ul>
        <NewCard sessionId={retro.sessionId} agents={agents} onChange={onChange} />
      </Card>
    </div>
  );
}

function CardFields({ sentiment, text, agent, agents, onChange }: {
  sentiment: Sentiment;
  text: string;
  agent: string;
  agents: string[];
  onChange: (next: { sentiment: Sentiment; text: string; agent: string }) => void;
}) {
  return (
    <div className="space-y-2">
      <div className="flex gap-2">
        <select className={field} value={sentiment} onChange={e => onChange({ sentiment: e.target.value as Sentiment, text, agent })}>
          <option value="good">Good</option>
          <option value="bad">Bad</option>
          <option value="ugly">Ugly</option>
        </select>
        <select className={field} value={agent} onChange={e => onChange({ sentiment, text, agent: e.target.value })}>
          <option value="">The team</option>
          {agents.map(a => (
            <option key={a} value={a}>
              {a}
            </option>
          ))}
        </select>
      </div>
      <textarea className={field} rows={3} value={text} onChange={e => onChange({ sentiment, text: e.target.value, agent })} />
    </div>
  );
}

function DraftCard({ card, agents, onChange }: { card: RetroCard; agents: string[]; onChange: () => void }) {
  const [draft, setDraft] = useState({ sentiment: card.sentiment, text: card.text, agent: card.agent ?? '' });

  const confirm = useMutation({
    mutationFn: () =>
      api(`/api/retro-cards/${card.id}/confirm`, { method: 'POST', body: { sentiment: draft.sentiment, text: draft.text, agent: draft.agent || null } }),
    onSuccess: onChange,
  });
  const discard = useMutation({ mutationFn: () => api(`/api/retro-cards/${card.id}/discard`, { method: 'POST' }), onSuccess: onChange });

  return (
    <div className="rounded-lg border border-dashed border-slate-300 p-3 dark:border-slate-700" data-testid="draft-card">
      <CardFields {...draft} agents={agents} onChange={setDraft} />
      <div className="mt-2 flex gap-2">
        <Button onClick={() => confirm.mutate()} disabled={!draft.text.trim() || confirm.isPending}>
          Confirm
        </Button>
        <Button variant="ghost" onClick={() => discard.mutate()} disabled={discard.isPending}>
          Discard
        </Button>
      </div>
      <ErrorNote error={confirm.error ?? discard.error} />
    </div>
  );
}

function NewCard({ sessionId, agents, onChange }: { sessionId: string; agents: string[]; onChange: () => void }) {
  const [card, setCard] = useState<{ sentiment: Sentiment; text: string; agent: string }>({ sentiment: 'good', text: '', agent: '' });
  const add = useMutation({
    mutationFn: () => api(`/api/retros/${sessionId}/cards`, { method: 'POST', body: { sentiment: card.sentiment, text: card.text, agent: card.agent || null } }),
    onSuccess: () => {
      setCard({ sentiment: 'good', text: '', agent: '' });
      onChange();
    },
  });

  return (
    <details className="mt-4 border-t border-slate-100 pt-3 dark:border-slate-800">
      <summary className="cursor-pointer text-sm font-medium text-indigo-600">Write a card yourself</summary>
      <div className="mt-3 space-y-2">
        <CardFields {...card} agents={agents} onChange={setCard} />
        <Button onClick={() => add.mutate()} disabled={!card.text.trim() || add.isPending}>
          Add card
        </Button>
        <ErrorNote error={add.error} />
      </div>
    </details>
  );
}
