import type { ModelEndpoint } from '../api/types';
import { field } from './ui';

/**
 * The endpoint picker (design doc section 8): shared endpoints and your own, in one select. The first option is
 * "inherit", whose label says what that means at this level.
 */
export function EndpointSelect({
  endpoints,
  value,
  onChange,
  inheritLabel,
  id,
}: {
  endpoints: ModelEndpoint[];
  value: string;
  onChange: (value: string) => void;
  inheritLabel: string;
  id?: string;
}) {
  const shared = endpoints.filter(e => e.shared);
  const mine = endpoints.filter(e => e.mine);
  const option = (e: ModelEndpoint) => (
    <option key={e.id} value={e.id}>
      {e.name} ({e.kind}, {e.defaultModel})
    </option>
  );

  return (
    <select id={id} className={field} value={value} onChange={event => onChange(event.target.value)}>
      <option value="">{inheritLabel}</option>
      {shared.length > 0 && <optgroup label="Shared">{shared.map(option)}</optgroup>}
      {mine.length > 0 && <optgroup label="Yours">{mine.map(option)}</optgroup>}
    </select>
  );
}

/** The warning the picker shows for an endpoint without structured output. */
export function StructuredOutputNote({ endpoint }: { endpoint: ModelEndpoint | undefined }) {
  if (!endpoint || endpoint.nativeStructuredOutput) return null;
  return (
    <p className="text-xs text-amber-700 dark:text-amber-300">
      {endpoint.name} has no native structured output: the reviewers will be asked for JSON in the prompt, with one repair
      attempt if a reply does not fit.
    </p>
  );
}
