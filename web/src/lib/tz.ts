export const US_TIMEZONES = [
  { label: 'Eastern Time', value: 'America/New_York', short: 'ET' },
  { label: 'Central Time', value: 'America/Chicago', short: 'CT' },
  { label: 'Mountain Time', value: 'America/Denver', short: 'MT' },
  { label: 'Pacific Time', value: 'America/Los_Angeles', short: 'PT' },
] as const

/** Renders a UTC instant in the chosen US zone. Storage stays UTC; only display converts. */
export function formatInTz(utc: string | Date | null, tz: string, opts?: Intl.DateTimeFormatOptions) {
  if (!utc) return '—'
  const d = typeof utc === 'string' ? new Date(utc) : utc
  if (Number.isNaN(d.getTime())) return '—'
  return new Intl.DateTimeFormat('en-US', {
    timeZone: tz,
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    ...opts,
  }).format(d)
}

export function timeInTz(utc: string | Date | null, tz: string) {
  return formatInTz(utc, tz, { month: undefined, day: undefined, hour: 'numeric', minute: '2-digit' })
}

/** The calendar buckets posts by their date *in the target zone*, not the browser's. */
export function dateKeyInTz(utc: string | Date, tz: string) {
  const d = typeof utc === 'string' ? new Date(utc) : utc
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: tz,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(d)
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? '00'
  return `${get('year')}-${get('month')}-${get('day')}`
}

/** Offset in minutes between UTC and the zone at a given instant (handles DST). */
function tzOffsetMinutes(tz: string, at: Date) {
  const dtf = new Intl.DateTimeFormat('en-US', {
    timeZone: tz,
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
  const p = dtf.formatToParts(at)
  const get = (t: string) => Number(p.find((x) => x.type === t)?.value ?? 0)
  const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'), get('second'))
  return (asUtc - at.getTime()) / 60000
}

/** Converts "18:00" wall-clock in `tz` on `date` into the matching UTC instant. */
export function zonedTimeToUtc(dateKey: string, hhmm: string, tz: string): Date {
  const [y, m, d] = dateKey.split('-').map(Number)
  const [hh, mm] = hhmm.split(':').map(Number)
  const guess = new Date(Date.UTC(y, m - 1, d, hh, mm))
  // Apply the offset twice-resolved so DST boundaries land correctly.
  const off1 = tzOffsetMinutes(tz, guess)
  const adjusted = new Date(guess.getTime() - off1 * 60000)
  const off2 = tzOffsetMinutes(tz, adjusted)
  return new Date(guess.getTime() - off2 * 60000)
}

/** Converts a stored UTC "HH:mm" slot into wall-clock "HH:mm" in the zone. */
export function utcSlotToLocal(hhmm: string, tz: string): string {
  if (!hhmm) return ''
  const [hh, mm] = hhmm.split(':').map(Number)
  const now = new Date()
  const utc = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), hh, mm))
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: tz,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(utc)
}

/** Inverse of utcSlotToLocal: local wall-clock "HH:mm" back to a UTC "HH:mm" slot. */
export function localSlotToUtc(hhmm: string, tz: string): string {
  if (!hhmm) return ''
  const now = new Date()
  const key = `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, '0')}-${String(
    now.getUTCDate(),
  ).padStart(2, '0')}`
  const utc = zonedTimeToUtc(key, hhmm, tz)
  return `${String(utc.getUTCHours()).padStart(2, '0')}:${String(utc.getUTCMinutes()).padStart(2, '0')}`
}

export function formatDuration(ms: number) {
  if (!ms) return '—'
  return `${(ms / 1000).toFixed(1)}s`
}
