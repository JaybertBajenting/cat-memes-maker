import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type VideoDto } from '../lib/api'
import { toast } from '../lib/store'
import { formatDuration } from '../lib/tz'
import { Button, Card, CardContent, EmptyState, Pager } from '../components/ui'

/** Nine players per page: enough to work through, few enough to stay responsive. */
const PAGE_SIZE = 9

export function Review() {
  const qc = useQueryClient()
  const [page, setPage] = useState(1)

  const { data } = useQuery({
    queryKey: ['queue', 'awaiting_approval', page],
    queryFn: () => api.queue('awaiting_approval', page, PAGE_SIZE),
    refetchInterval: 5000,
    placeholderData: (prev) => prev,
  })

  const videos = data?.items ?? []

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['queue'] })
    void qc.invalidateQueries({ queryKey: ['stats'] })
  }

  const approve = useMutation({
    mutationFn: api.approve,
    onSuccess: () => {
      toast.success('Approved — queued for posting')
      refresh()
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const reject = useMutation({
    mutationFn: api.reject,
    onSuccess: () => {
      toast.info('Rejected — output file removed')
      refresh()
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const busy = approve.isPending || reject.isPending

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-heading text-2xl font-bold tracking-tight">Review Queue</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Videos waiting for approval. This queue only fills when Auto Upload is off.
        </p>
      </header>

      {!videos.length ? (
        <Card>
          <EmptyState
            icon="✅"
            title="Nothing to review"
            body="Rendered videos awaiting approval will show up here with a preview and approve/reject buttons."
          />
        </Card>
      ) : (
        <>
          <div className="grid gap-5 sm:grid-cols-2 xl:grid-cols-3">
            {videos.map((v) => (
              <ReviewCard
                key={v.id}
                video={v}
                busy={busy}
                onApprove={() => approve.mutate(v.id)}
                onReject={() => reject.mutate(v.id)}
              />
            ))}
          </div>
          <Pager
            page={data?.page ?? 1}
            pageSize={data?.pageSize ?? PAGE_SIZE}
            total={data?.total ?? 0}
            onPage={setPage}
          />
        </>
      )}
    </div>
  )
}

function ReviewCard({
  video,
  onApprove,
  onReject,
  busy,
}: {
  video: VideoDto
  onApprove: () => void
  onReject: () => void
  busy: boolean
}) {
  return (
    <Card className="pt-0">
      <div className="aspect-9/16 bg-black">
        {video.videoUrl ? (
          <video
            src={video.videoUrl}
            controls
            loop
            playsInline
            preload="metadata"
            poster={video.thumbUrl ?? undefined}
            className="h-full w-full object-contain"
          />
        ) : (
          <div className="flex h-full items-center justify-center text-sm text-muted-foreground">
            No preview
          </div>
        )}
      </div>

      <CardContent className="space-y-3">
        <div>
          <div className="text-sm font-medium">{video.topic || `Video #${video.id}`}</div>
          <div className="mt-1 text-xs text-muted-foreground">
            #{video.id} · {video.mood} · {formatDuration(video.durationMs)}
          </div>
        </div>

        {video.error && (
          <p className="rounded bg-amber-950/40 px-2 py-1 text-xs text-amber-300">⚠ {video.error}</p>
        )}

        {video.caption && (
          <p className="rounded-lg bg-muted px-3 py-2 text-xs leading-relaxed text-muted-foreground">
            {video.caption}
          </p>
        )}

        <div className="flex gap-2">
          <Button
            className="flex-1 bg-emerald-600 text-white hover:bg-emerald-500"
            onClick={onApprove}
            disabled={busy}
          >
            ✅ Approve
          </Button>
          <Button variant="destructive" className="flex-1" onClick={onReject} disabled={busy}>
            ❌ Reject
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}
