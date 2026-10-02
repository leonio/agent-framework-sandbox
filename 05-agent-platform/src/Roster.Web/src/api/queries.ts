import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import type { AgentDetail, AgentSummary, AssignmentDetail, AssignmentSummary, Credential, ModelEndpoint, Retro, Scorecard, StepDetail } from './types';

/**
 * Query hooks: one per thing the pages show, each with a stable query key so a change anywhere can invalidate it.
 * Keys: ['assignments'], ['assignment', id], ['retro', id], ['endpoints'], ['credentials'], ['agents'], ['agent', name],
 * ['scorecards'], ['step', assignmentId, stepId].
 */

export const useAssignments = () => useQuery({ queryKey: ['assignments'], queryFn: () => api<AssignmentSummary[]>('/api/assignments') });

export const useAssignment = (id: string) =>
  useQuery({ queryKey: ['assignment', id], queryFn: () => api<AssignmentDetail>(`/api/assignments/${id}`) });

export const useStep = (assignmentId: string, stepId: string | null) =>
  useQuery({
    queryKey: ['step', assignmentId, stepId],
    queryFn: () => api<StepDetail>(`/api/assignments/${assignmentId}/steps/${stepId}`),
    enabled: stepId !== null,
  });

/** The retro, or null when none has been opened yet (the API answers 404). */
export const useRetro = (assignmentId: string) =>
  useQuery({
    queryKey: ['retro', assignmentId],
    queryFn: async () => {
      try {
        return await api<Retro>(`/api/assignments/${assignmentId}/retro`);
      } catch (error) {
        if ((error as { status?: number }).status === 404) return null;
        throw error;
      }
    },
  });

export const useEndpoints = () => useQuery({ queryKey: ['endpoints'], queryFn: () => api<ModelEndpoint[]>('/api/endpoints') });
export const useCredentials = () => useQuery({ queryKey: ['credentials'], queryFn: () => api<Credential[]>('/api/credentials') });
export const useAgents = () => useQuery({ queryKey: ['agents'], queryFn: () => api<AgentSummary[]>('/api/agents') });
export const useAgent = (name: string) => useQuery({ queryKey: ['agent', name], queryFn: () => api<AgentDetail>(`/api/agents/${name}`) });
export const useScorecards = () => useQuery({ queryKey: ['scorecards'], queryFn: () => api<Scorecard[]>('/api/scorecards') });

/** Every event kind the platform publishes (Roster.Platform/Queue/EventBus.cs). */
const assignmentEvents = [
  'assignment.updated',
  'phase.started',
  'phase.completed',
  'phase.failed',
  'agent.started',
  'agent.completed',
  'findings.added',
  'finding.decided',
];
const retroEvents = ['retro.message', 'retro.cards'];

/**
 * Live updates for one assignment: "something changed, refetch", the pattern sample 02's useLiveData uses.
 *
 * An EventSource listens to the assignment's server-sent events (the session cookie authenticates it). Each event
 * marks the matching queries stale; a burst of events (three reviewers finishing together, a streaming retro reply)
 * becomes one refetch at most every 250 ms. If the connection drops, the browser reconnects by itself and the API
 * resumes after the last event id it saw. Returns whether the stream is currently connected, for the "Live" dot.
 */
export function useLiveEvents(assignmentId: string): boolean {
  const queryClient = useQueryClient();
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const source = new EventSource(`/api/assignments/${assignmentId}/events`);
    const changed = new Set<string>();
    let timer: number | undefined;

    const flush = () => {
      timer = undefined;
      if ([...changed].some(kind => retroEvents.includes(kind))) {
        queryClient.invalidateQueries({ queryKey: ['retro', assignmentId] });
      }
      if ([...changed].some(kind => assignmentEvents.includes(kind))) {
        queryClient.invalidateQueries({ queryKey: ['assignment', assignmentId] });
        queryClient.invalidateQueries({ queryKey: ['assignments'] });
      }
      changed.clear();
    };

    const onEvent = (event: MessageEvent) => {
      changed.add(event.type);
      timer ??= window.setTimeout(flush, 250);
    };

    for (const kind of [...assignmentEvents, ...retroEvents]) source.addEventListener(kind, onEvent);
    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);

    return () => {
      source.close();
      window.clearTimeout(timer);
    };
  }, [assignmentId, queryClient]);

  return connected;
}
