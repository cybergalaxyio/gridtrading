import type { EntryHold } from '../types'

export function EntryHolds({ holds, now, formatDate }: {
  holds: EntryHold[]; now: number; formatDate: (value?: string) => string
}) {
  const active = holds.filter(hold => hold.side === 'SELL' && hold.reason === 'FILL_LIMIT' &&
    hold.resumeAt !== null && Date.parse(hold.resumeAt) > now)
  if (active.length === 0) return null
  return <div className="entry-holds" aria-label="Sell entry fill restriction">
    {active.map(hold => <div className="entry-hold" key={hold.side}>
      <strong>HOLD · SELL</strong>
      <span className="mono" title="Local time when the sell entry-fill restriction expires.">
        Resume: <time dateTime={hold.resumeAt!}>{formatDate(hold.resumeAt!)}</time>
      </span>
    </div>)}
  </div>
}
