import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import { toast } from '../lib/store'
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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '../components/ui'

export function Clips() {
  const qc = useQueryClient()
  const [path, setPath] = useState('')

  const { data } = useQuery({ queryKey: ['clips'], queryFn: api.clips })

  const importClips = useMutation({
    mutationFn: (p: string) => api.importClips(p || undefined),
    onSuccess: (r) => {
      toast.success(`Imported ${r.imported} clips (${r.skipped} skipped)`)
      void qc.invalidateQueries({ queryKey: ['clips'] })
      void qc.invalidateQueries({ queryKey: ['stats'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const deactivate = useMutation({
    mutationFn: api.deactivateClip,
    onSuccess: () => {
      toast.info('Clip deactivated')
      void qc.invalidateQueries({ queryKey: ['clips'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const active = data?.filter((c) => c.isActive) ?? []

  return (
    <div className="space-y-6">
      <header>
        <h1 className="font-heading text-2xl font-bold tracking-tight">Cat Clips</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Green screen source clips. Each is analysed on import for its own key colour and crop box.
        </p>
      </header>

      <Card>
        <CardHeader
          title="Import compilation"
          subtitle="Scene-splits a long green screen video into individual clips."
        />
        <CardContent className="space-y-4">
          <Field
            label="Source video path"
            hint="Leave blank to use DEFAULT_COMPILATION_PATH from .env"
            htmlFor="src"
          >
            <Input
              id="src"
              placeholder="D:\path\to\green screen memes.mp4"
              value={path}
              onChange={(e) => setPath(e.target.value)}
            />
          </Field>
          <LoadingButton loading={importClips.isPending} onClick={() => importClips.mutate(path)}>
            {importClips.isPending ? 'Splitting… this takes a few minutes' : '📥 Import clips'}
          </LoadingButton>
          {importClips.isPending && (
            <p className="text-xs text-muted-foreground">
              Scene detection reads the whole file, then each segment is re-encoded and analysed.
            </p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader title={`Library (${active.length} active)`} />
        {!data?.length ? (
          <EmptyState icon="🐱" title="No clips yet" body="Import a green screen compilation to build the library." />
        ) : (
          <CardContent className="overflow-x-auto p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>File</TableHead>
                  <TableHead>Duration</TableHead>
                  <TableHead>Key</TableHead>
                  <TableHead>Crop</TableHead>
                  <TableHead>Size</TableHead>
                  <TableHead>Moods</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.map((c) => (
                  <TableRow key={c.id} className={c.isActive ? '' : 'opacity-40'}>
                    <TableCell className="font-mono text-xs">{c.filename}</TableCell>
                    <TableCell className="text-muted-foreground">{formatDuration(c.durationMs)}</TableCell>
                    <TableCell>
                      <span className="inline-flex items-center gap-1.5">
                        <span
                          className="inline-block size-3 rounded-sm ring-1 ring-border"
                          style={{ background: c.keyColor.replace('0x', '#') }}
                        />
                        <span className="font-mono text-xs text-muted-foreground">{c.keyColor}</span>
                      </span>
                    </TableCell>
                    <TableCell className="font-mono text-xs text-muted-foreground">
                      {c.cropFilter ?? '—'}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {c.width}×{c.height}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">{c.moodTags}</TableCell>
                    <TableCell className="text-right">
                      {c.isActive && (
                        <Button variant="ghost" size="sm" onClick={() => deactivate.mutate(c.id)}>
                          Disable
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        )}
      </Card>
    </div>
  )
}
