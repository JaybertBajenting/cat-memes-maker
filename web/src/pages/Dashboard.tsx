import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { api } from '../lib/api'
import { toast, useGenerateStore } from '../lib/store'
import { formatDuration } from '../lib/tz'
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  EmptyState,
  Field,
  Input,
  LoadingButton,
  StatusChip,
  Textarea,
} from '../components/ui'

const MIN = 1
const MAX = 50

export function Dashboard() {
  const qc = useQueryClient()
  const { count, setCount } = useGenerateStore()
  const [raw, setRaw] = useState(String(count))

  const [topic, setTopic] = useState('')
  const [dialogue, setDialogue] = useState('')

  const parsed = Number(raw)
  const invalid =
    raw.trim() === '' || !Number.isInteger(parsed) || parsed < MIN || parsed > MAX
      ? `Enter a whole number between ${MIN} and ${MAX}.`
      : undefined

  // A topic switches the card from "invent N ideas" to "make this one".
  const custom = topic.trim().length > 0
  const topicError =
    custom && topic.trim().length < 8 ? 'Give the topic a few more words.' : undefined

  // Mirrors the server rule, so a typo is caught before a job is queued.
  const turns = dialogue
    .split('\n')
    .filter((l) => /^\s*(a|b|cat[ _]?a|cat[ _]?b|left|right|1|2)\s*:\s*\S/i.test(l)).length
  const dialogueError =
    dialogue.trim().length > 0 && turns < 2
      ? 'Write at least two turns, each starting with "a:" or "b:".'
      : undefined

  const blocked = custom
    ? !!topicError || !!dialogueError
    : !!invalid

  const { data: stats } = useQuery({ queryKey: ['stats'], queryFn: api.stats, refetchInterval: 4000 })
  // Only the newest few are shown, so only the newest few are fetched.
  const { data: recent } = useQuery({
    queryKey: ['queue', 'recent'],
    queryFn: () => api.queue(undefined, 1, 8),
    refetchInterval: 4000,
  })

  const generate = useMutation({
    mutationFn: (v: { count: number; topic: string; dialogue: string }) =>
      api.generate(v.count, v.topic, v.dialogue),
    onSuccess: (_res, v) => {
      const one = v.topic.trim().length > 0
      toast.success(
        one
          ? 'Building your topic — check the queue'
          : `Generating ${v.count} video${v.count === 1 ? '' : 's'} — check the queue`,
      )
      if (one) {
        setTopic('')
        setDialogue('')
      }
      void qc.invalidateQueries({ queryKey: ['queue'] })
      void qc.invalidateQueries({ queryKey: ['stats'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const noClips = stats !== undefined && stats.clips === 0

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-heading text-2xl font-bold tracking-tight">Dashboard</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Generate POV cat reels, then review and schedule them.
        </p>
      </header>

      {noClips && (
        <Card className="ring-amber-500/40">
          <CardContent className="flex items-center justify-between gap-4">
            <div>
              <div className="text-sm font-semibold text-amber-200">No cat clips imported yet</div>
              <p className="mt-0.5 text-xs text-amber-200/70">
                Rendering needs a green screen clip library. Import one to get started.
              </p>
            </div>
            <Link to="/clips">
              <Button>Import clips</Button>
            </Link>
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader
          title="Generate Videos"
          subtitle="Runs topic generation, background sourcing, and rendering."
        />
        <CardContent className="space-y-4">
          <Field
            label="Topic (optional)"
            hint={
              custom
                ? 'Renders one video from this exact line. Everything else is written to fit it.'
                : 'Leave blank to have topics generated for you.'
            }
            error={topicError}
            htmlFor="topic"
          >
            <Input
              id="topic"
              placeholder="POV: when you call your girlfriend by her real name"
              value={topic}
              onChange={(e) => setTopic(e.target.value)}
            />
          </Field>

          {/* Only meaningful with a topic, so it stays out of the way until there is one. */}
          {custom && (
            <Field
              label="Dialogue (optional)"
              hint='One turn per line, starting with "a:" or "b:". Blank lets the model decide.'
              error={dialogueError}
              htmlFor="dialogue"
            >
              <Textarea
                id="dialogue"
                rows={5}
                className="font-mono text-xs"
                placeholder={'a: is that my name?\nb: no\na: what should you call me?\nb: babe'}
                value={dialogue}
                onChange={(e) => setDialogue(e.target.value)}
              />
            </Field>
          )}

          {!custom && (
            <div className="max-w-xs">
              <Field label="How many videos?" error={invalid} htmlFor="count">
                <Input
                  id="count"
                  type="number"
                  min={MIN}
                  max={MAX}
                  step={1}
                  value={raw}
                  onChange={(e) => {
                    setRaw(e.target.value)
                    const n = Number(e.target.value)
                    if (Number.isInteger(n) && n >= MIN && n <= MAX) setCount(n)
                  }}
                />
              </Field>
            </div>
          )}

          <div className="flex items-center gap-4">
            <LoadingButton
              loading={generate.isPending}
              disabled={blocked}
              onClick={() =>
                generate.mutate({ count: parsed, topic, dialogue })
              }
            >
              {generate.isPending
                ? 'Starting…'
                : custom
                  ? '🎬 Generate This Topic'
                  : '🎬 Generate Videos'}
            </LoadingButton>
            {!blocked && (
              <span className="text-xs text-muted-foreground">
                Estimated time: ~{custom ? 2 : parsed * 2} minutes
              </span>
            )}
          </div>
        </CardContent>
      </Card>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
        <Stat label="Rendering" value={stats?.rendering} tone="text-sky-300" />
        <Stat label="Review" value={stats?.awaitingApproval} tone="text-amber-300" />
        <Stat label="Ready" value={stats?.ready} tone="text-emerald-300" />
        <Stat label="Posted" value={stats?.posted} tone="text-violet-300" />
        <Stat label="Failed" value={stats?.failed} tone="text-rose-300" />
        <Stat label="Clips" value={stats?.clips} tone="text-foreground" />
      </div>

      <Card>
        <CardHeader
          title="Recent activity"
          action={
            <Link to="/queue" className="text-xs text-primary hover:underline">
              View all →
            </Link>
          }
        />
        {!recent?.items.length ? (
          <EmptyState icon="🎞️" title="Nothing yet" body="Generated videos will appear here as they render." />
        ) : (
          <CardContent className="p-0">
            <ul className="divide-y divide-border">
              {recent.items.map((v) => (
                <li key={v.id} className="flex items-center gap-4 py-3">
                  {v.thumbUrl ? (
                    <img src={v.thumbUrl} alt="" className="h-14 w-8 rounded object-cover" />
                  ) : (
                    <div className="flex h-14 w-8 items-center justify-center rounded bg-muted text-xs">
                      🐱
                    </div>
                  )}
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm">{v.topic || `Video #${v.id}`}</div>
                    <div className="mt-0.5 text-xs text-muted-foreground">
                      {v.mood} · {formatDuration(v.durationMs)}
                    </div>
                  </div>
                  <StatusChip status={v.status} />
                </li>
              ))}
            </ul>
          </CardContent>
        )}
      </Card>
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value?: number; tone: string }) {
  return (
    <Card size="sm">
      <CardContent>
        <div className={`font-heading text-2xl font-bold ${tone}`}>{value ?? '—'}</div>
        <div className="mt-0.5 text-[11px] tracking-wide text-muted-foreground uppercase">{label}</div>
      </CardContent>
    </Card>
  )
}
