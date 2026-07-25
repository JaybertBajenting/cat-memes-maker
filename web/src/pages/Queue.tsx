import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type VideoDto } from '../lib/api'
import { toast } from '../lib/store'
import { formatDuration } from '../lib/tz'
import { Button, Card, CardContent, EmptyState, Pager, Spinner, StatusChip, cn } from '../components/ui'

const FILTERS = [
  { label: 'All', value: '' },
  { label: 'Rendering', value: 'rendering' },
  { label: 'Review', value: 'awaiting_approval' },
  { label: 'Ready', value: 'ready' },
  { label: 'Posted', value: 'posted' },
  { label: 'Failed', value: 'failed,cancelled,rejected' },
]

export function Queue() {
  const qc = useQueryClient()
  const [filter, setFilter] = useState('')
  const [page, setPage] = useState(1)

  const { data, isLoading } = useQuery({
    queryKey: ['queue', filter, page],
    queryFn: () => api.queue(filter || undefined, page),
    refetchInterval: 3000,
    // Without this the list blanks to the empty state on every page change, because a
    // new queryKey has no cached data while the request is in flight.
    placeholderData: (prev) => prev,
  })

  const videos = data?.items ?? []

  // Changing the filter re-pages from the start; page 3 of "all" is meaningless in "failed".
  const selectFilter = (value: string) => {
    setFilter(value)
    setPage(1)
  }

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['queue'] })
    void qc.invalidateQueries({ queryKey: ['stats'] })
  }

  const cancel = useMutation({
    mutationFn: api.cancel,
    onSuccess: (r) => {
      toast.success(r.message)
      refresh()
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const retry = useMutation({
    mutationFn: api.retry,
    onSuccess: () => {
      toast.success('Re-queued for rendering')
      refresh()
    },
    onError: (e: Error) => toast.error(e.message),
  })

  return (
    <div className="space-y-6">
      <header className="flex items-end justify-between">
        <div>
          <h1 className="font-heading text-2xl font-bold tracking-tight">Queue</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Every video in the pipeline, newest first.
          </p>
        </div>
        {isLoading && <Spinner className="text-muted-foreground" />}
      </header>

      <div className="flex flex-wrap gap-2">
        {FILTERS.map((f) => (
          <Button
            key={f.label}
            size="sm"
            variant={filter === f.value ? 'secondary' : 'ghost'}
            onClick={() => selectFilter(f.value)}
            className={cn('rounded-full', filter === f.value && 'text-primary')}
          >
            {f.label}
          </Button>
        ))}
      </div>

      {!videos.length ? (
        <Card>
          <EmptyState icon="📋" title="Queue is empty" body="Generate a batch from the dashboard to fill it." />
        </Card>
      ) : (
        <>
          <div className="space-y-3">
            {videos.map((v) => (
              <QueueRow
                key={v.id}
                video={v}
                onCancel={() => cancel.mutate(v.id)}
                onRetry={() => retry.mutate(v.id)}
                busy={cancel.isPending || retry.isPending}
              />
            ))}
          </div>
          <Pager
            page={data?.page ?? 1}
            pageSize={data?.pageSize ?? 24}
            total={data?.total ?? 0}
            onPage={setPage}
          />
        </>
      )}
    </div>
  )
}

function QueueRow({
  video,
  onCancel,
  onRetry,
  busy,
}: {
  video: VideoDto
  onCancel: () => void
  onRetry: () => void
  busy: boolean
}) {
  const canCancel = video.status === 'rendering'
  const canRetry = ['failed', 'cancelled'].includes(video.status)
  // A finished video carrying a message is a soft verification warning, not a failure.
  const isWarning = video.error != null && video.status !== 'failed'

  return (
    <Card size="sm">
      <CardContent className="flex items-center gap-4">
        {video.thumbUrl ? (
          <img src={video.thumbUrl} alt="" className="h-20 w-11 rounded-md object-cover" />
        ) : (
          <div className="flex h-20 w-11 items-center justify-center rounded-md bg-muted text-lg">
            {video.status === 'rendering' ? <Spinner className="text-muted-foreground" /> : '🐱'}
          </div>
        )}

        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-medium">{video.topic || `Video #${video.id}`}</div>
          <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
            <span>#{video.id}</span>
            <span>{video.mood}</span>
            <span>{video.layout?.replace(/_/g, ' ')}</span>
            <span>{formatDuration(video.durationMs)}</span>
          </div>
          {video.error && (
            <div
              className={cn(
                'mt-1.5 line-clamp-2 rounded px-2 py-1 text-xs',
                isWarning ? 'bg-amber-950/40 text-amber-300' : 'bg-rose-950/40 text-rose-300',
              )}
            >
              {isWarning ? `⚠ ${video.error}` : video.error}
            </div>
          )}
        </div>

        <StatusChip status={video.status} />

        <div className="flex shrink-0 gap-2">
          {video.videoUrl && (
            <a href={video.videoUrl} target="_blank" rel="noreferrer">
              <Button variant="outline" size="sm">
                Preview
              </Button>
            </a>
          )}
          {canCancel && (
            <Button variant="destructive" size="sm" onClick={onCancel} disabled={busy}>
              Cancel
            </Button>
          )}
          {canRetry && (
            <Button variant="secondary" size="sm" onClick={onRetry} disabled={busy}>
              Retry
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  )
}
