import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type PostDto } from '../lib/api'
import { toast } from '../lib/store'
import { dateKeyInTz, formatInTz, timeInTz, zonedTimeToUtc } from '../lib/tz'
import { Button, Card, CardContent, Input, Separator, StatusChip, cn } from '../components/ui'

const DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

export function Calendar() {
  const qc = useQueryClient()
  const [cursor, setCursor] = useState(() => new Date())
  const [selected, setSelected] = useState<string | null>(null)

  const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: api.getSettings })
  const tz = settings?.targetTimezone ?? 'America/New_York'

  const { from, to } = useMemo(() => {
    const f = new Date(Date.UTC(cursor.getFullYear(), cursor.getMonth() - 1, 20))
    const t = new Date(Date.UTC(cursor.getFullYear(), cursor.getMonth() + 2, 10))
    return { from: f, to: t }
  }, [cursor])

  const { data: posts } = useQuery({
    queryKey: ['posts', from.toISOString(), to.toISOString()],
    queryFn: () => api.posts(from, to),
    refetchInterval: 15000,
  })

  // Bucket by the post's date in the *target* timezone, not the browser's.
  const byDay = useMemo(() => {
    const map = new Map<string, PostDto[]>()
    for (const p of posts ?? []) {
      const when = p.postedAt ?? p.scheduledAt
      if (!when) continue
      const key = dateKeyInTz(when, tz)
      const list = map.get(key) ?? []
      list.push(p)
      map.set(key, list)
    }
    return map
  }, [posts, tz])

  const cells = useMemo(() => buildGrid(cursor), [cursor])
  const monthLabel = cursor.toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
  const todayKey = dateKeyInTz(new Date(), tz)

  const reschedule = useMutation({
    mutationFn: ({ id, when }: { id: number; when: Date }) => api.reschedule(id, when),
    onSuccess: () => {
      toast.success('Rescheduled')
      void qc.invalidateQueries({ queryKey: ['posts'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const cancelPost = useMutation({
    mutationFn: api.cancelPost,
    onSuccess: () => {
      toast.info('Post cancelled')
      void qc.invalidateQueries({ queryKey: ['posts'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const selectedPosts = selected ? (byDay.get(selected) ?? []) : []

  return (
    <div className="space-y-6">
      <header className="flex items-end justify-between">
        <div>
          <h1 className="font-heading text-2xl font-bold tracking-tight">Calendar</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Scheduled and posted content, shown in {tz.replace('America/', '').replace('_', ' ')}.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" onClick={() => setCursor(shiftMonth(cursor, -1))}>
            ←
          </Button>
          <span className="min-w-40 text-center text-sm font-medium">{monthLabel}</span>
          <Button variant="ghost" size="sm" onClick={() => setCursor(shiftMonth(cursor, 1))}>
            →
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setCursor(new Date())}>
            Today
          </Button>
        </div>
      </header>

      <Card className="py-0">
        <div className="grid grid-cols-7 bg-muted/40">
          {DOW.map((d) => (
            <div
              key={d}
              className="px-2 py-2 text-center text-[11px] font-medium tracking-wide text-muted-foreground uppercase"
            >
              {d}
            </div>
          ))}
        </div>
        <Separator />

        <div className="grid grid-cols-7">
          {cells.map((cell) => {
            const items = byDay.get(cell.key) ?? []
            return (
              <button
                key={cell.key}
                onClick={() => setSelected(cell.key)}
                className={cn(
                  'min-h-26 border-r border-b border-border p-1.5 text-left transition-colors',
                  cell.inMonth ? 'hover:bg-muted/60' : 'bg-muted/20 hover:bg-muted/40',
                  selected === cell.key && 'ring-1 ring-primary ring-inset',
                )}
              >
                <div className="mb-1 flex items-center justify-between">
                  <span
                    className={cn(
                      'text-xs',
                      cell.key === todayKey
                        ? 'rounded bg-primary px-1.5 py-0.5 font-semibold text-primary-foreground'
                        : cell.inMonth
                          ? 'text-foreground'
                          : 'text-muted-foreground/50',
                    )}
                  >
                    {cell.day}
                  </span>
                  {items.length > 2 && (
                    <span className="text-[10px] text-muted-foreground">+{items.length - 2}</span>
                  )}
                </div>

                <div className="space-y-1">
                  {items.slice(0, 2).map((p) => (
                    <div key={p.id} className="flex items-center gap-1 rounded bg-background/70 p-1">
                      {p.thumbUrl ? (
                        <img src={p.thumbUrl} alt="" className="h-7 w-4 shrink-0 rounded-sm object-cover" />
                      ) : (
                        <span className="text-[10px]">🐱</span>
                      )}
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-[10px] text-foreground">
                          {timeInTz(p.postedAt ?? p.scheduledAt, tz)}
                        </div>
                        <div className="text-[9px] text-muted-foreground">{platformBadge(p.platform)}</div>
                      </div>
                      <span className={cn('size-1.5 shrink-0 rounded-full', dotFor(p.status))} />
                    </div>
                  ))}
                </div>
              </button>
            )
          })}
        </div>
      </Card>

      {selected && (
        <Card>
          <CardContent className="flex items-center justify-between pb-0">
            <h2 className="text-sm font-semibold">
              {new Date(`${selected}T12:00:00Z`).toLocaleDateString('en-US', {
                weekday: 'long',
                month: 'long',
                day: 'numeric',
              })}
            </h2>
            <Button variant="ghost" size="sm" onClick={() => setSelected(null)}>
              Close
            </Button>
          </CardContent>
          <Separator />

          {selectedPosts.length === 0 ? (
            <CardContent>
              <p className="text-sm text-muted-foreground">
                Nothing scheduled. Approve a video and it will be slotted automatically.
              </p>
            </CardContent>
          ) : (
            <CardContent className="p-0">
              <ul className="divide-y divide-border">
                {selectedPosts.map((p) => (
                  <li key={p.id} className="flex gap-4 px-4 py-4">
                    {p.thumbUrl && <img src={p.thumbUrl} alt="" className="h-28 w-16 rounded-lg object-cover" />}
                    <div className="min-w-0 flex-1 space-y-2">
                      <div className="flex items-center gap-2">
                        <StatusChip status={p.status} />
                        <span className="text-xs text-muted-foreground">{platformBadge(p.platform)}</span>
                        <span className="text-xs text-muted-foreground">
                          {formatInTz(p.postedAt ?? p.scheduledAt, tz)}
                        </span>
                      </div>
                      <div className="text-sm">{p.topic}</div>
                      <p className="text-xs leading-relaxed text-muted-foreground">{p.caption}</p>
                      {p.errorMessage && (
                        <p className="rounded bg-rose-950/40 px-2 py-1 text-xs text-rose-300">{p.errorMessage}</p>
                      )}

                      {p.status !== 'posted' && (
                        <RescheduleRow
                          tz={tz}
                          dateKey={selected}
                          onSubmit={(when) => reschedule.mutate({ id: p.id, when })}
                          onCancel={() => cancelPost.mutate(p.id)}
                        />
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </CardContent>
          )}
        </Card>
      )}
    </div>
  )
}

function RescheduleRow({
  tz,
  dateKey,
  onSubmit,
  onCancel,
}: {
  tz: string
  dateKey: string
  onSubmit: (when: Date) => void
  onCancel: () => void
}) {
  const [time, setTime] = useState('18:00')
  return (
    <div className="flex items-center gap-2 pt-1">
      <Input type="time" value={time} onChange={(e) => setTime(e.target.value)} className="w-32" />
      <Button variant="secondary" size="sm" onClick={() => onSubmit(zonedTimeToUtc(dateKey, time, tz))}>
        Reschedule
      </Button>
      <Button variant="destructive" size="sm" onClick={onCancel}>
        Cancel post
      </Button>
    </div>
  )
}

function platformBadge(p: string) {
  return p === 'both' ? 'FB + IG' : p === 'facebook' ? 'FB' : 'IG'
}

function dotFor(status: string) {
  if (status === 'posted') return 'bg-emerald-400'
  if (status === 'failed') return 'bg-rose-400'
  if (status === 'cancelled') return 'bg-muted-foreground'
  return 'bg-sky-400'
}

function shiftMonth(d: Date, delta: number) {
  return new Date(d.getFullYear(), d.getMonth() + delta, 1)
}

function buildGrid(cursor: Date) {
  const first = new Date(cursor.getFullYear(), cursor.getMonth(), 1)
  const start = new Date(first)
  start.setDate(1 - first.getDay())

  return Array.from({ length: 42 }, (_, i) => {
    const d = new Date(start)
    d.setDate(start.getDate() + i)
    const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
    return { key, day: d.getDate(), inMonth: d.getMonth() === cursor.getMonth() }
  })
}
