import { useEffect } from 'react'
import { useForm, Controller } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { z } from 'zod'
import { api, type SettingsDto } from '../lib/api'
import { toast } from '../lib/store'
import { US_TIMEZONES, localSlotToUtc, utcSlotToLocal } from '../lib/tz'
import {
  Card,
  CardContent,
  CardHeader,
  Field,
  Input,
  LoadingButton,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Textarea,
} from '../components/ui'

const schema = z.object({
  scriptProvider: z.enum(['claude', 'codex']),
  backgroundSource: z.enum(['pexels', 'codex']),
  pexelsApiKey: z.string().nullable(),
  fbPageId: z.string().nullable(),
  fbAccessToken: z.string().nullable(),
  igUserId: z.string().nullable(),
  igAccessToken: z.string().nullable(),
  defaultPostCaptionTemplate: z.string().min(1, 'Template cannot be empty'),
  autoUpload: z.boolean(),
  targetTimezone: z.string(),
  batchSize: z.coerce.number().int().min(1).max(50),
  targetDurationMs: z.coerce.number().int().min(2000).max(60000),
  morningLocal: z.string(),
  afternoonLocal: z.string(),
  eveningLocal: z.string(),
})

type FormValues = z.input<typeof schema>

export function Settings() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['settings'], queryFn: api.getSettings })

  const form = useForm<FormValues>({
    resolver: zodResolver(schema) as never,
    defaultValues: {
      scriptProvider: 'claude',
      backgroundSource: 'pexels',
      pexelsApiKey: '',
      fbPageId: '',
      fbAccessToken: '',
      igUserId: '',
      igAccessToken: '',
      defaultPostCaptionTemplate: '{topic} {hashtags}',
      autoUpload: false,
      targetTimezone: 'America/New_York',
      batchSize: 5,
      targetDurationMs: 13000,
      morningLocal: '09:00',
      afternoonLocal: '12:00',
      eveningLocal: '19:00',
    },
  })

  const { register, handleSubmit, control, reset, watch, formState } = form
  const tz = watch('targetTimezone')

  useEffect(() => {
    if (!data) return
    reset({
      scriptProvider: data.scriptProvider === 'codex' ? 'codex' : 'claude',
      backgroundSource: data.backgroundSource === 'codex' ? 'codex' : 'pexels',
      pexelsApiKey: data.pexelsApiKey ?? '',
      fbPageId: data.fbPageId ?? '',
      fbAccessToken: data.fbAccessToken ?? '',
      igUserId: data.igUserId ?? '',
      igAccessToken: data.igAccessToken ?? '',
      defaultPostCaptionTemplate: data.defaultPostCaptionTemplate,
      autoUpload: data.autoUpload,
      targetTimezone: data.targetTimezone,
      batchSize: data.batchSize,
      targetDurationMs: data.targetDurationMs,
      // Slots live as UTC in the DB but are edited as wall-clock in the target zone.
      morningLocal: utcSlotToLocal(data.morningSlotUtc, data.targetTimezone),
      afternoonLocal: utcSlotToLocal(data.afternoonSlotUtc, data.targetTimezone),
      eveningLocal: utcSlotToLocal(data.eveningSlotUtc, data.targetTimezone),
    })
  }, [data, reset])

  const save = useMutation({
    mutationFn: (s: SettingsDto) => api.saveSettings(s),
    onSuccess: () => {
      toast.success('Settings saved')
      void qc.invalidateQueries({ queryKey: ['settings'] })
    },
    onError: (e: Error) => toast.error(e.message),
  })

  const onSubmit = handleSubmit((v) => {
    const parsed = schema.parse(v)
    save.mutate({
      scriptProvider: parsed.scriptProvider,
      backgroundSource: parsed.backgroundSource,
      pexelsApiKey: parsed.pexelsApiKey || null,
      fbPageId: parsed.fbPageId || null,
      fbAccessToken: parsed.fbAccessToken || null,
      igUserId: parsed.igUserId || null,
      igAccessToken: parsed.igAccessToken || null,
      defaultPostCaptionTemplate: parsed.defaultPostCaptionTemplate,
      autoUpload: parsed.autoUpload,
      targetTimezone: parsed.targetTimezone,
      batchSize: parsed.batchSize,
      targetDurationMs: parsed.targetDurationMs,
      morningSlotUtc: localSlotToUtc(parsed.morningLocal, parsed.targetTimezone),
      afternoonSlotUtc: localSlotToUtc(parsed.afternoonLocal, parsed.targetTimezone),
      eveningSlotUtc: localSlotToUtc(parsed.eveningLocal, parsed.targetTimezone),
    })
  })

  const zoneShort = US_TIMEZONES.find((t) => t.value === tz)?.short ?? ''

  return (
    <form onSubmit={onSubmit} className="space-y-6">
      <header className="flex items-end justify-between">
        <div>
          <h1 className="font-heading text-2xl font-bold tracking-tight">Settings</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Pipeline, posting, and scheduling configuration.
          </p>
        </div>
        <LoadingButton type="submit" loading={save.isPending}>
          Save changes
        </LoadingButton>
      </header>

      <Card>
        <CardHeader title="Pipeline" />
        <CardContent className="grid gap-5 sm:grid-cols-2">
          <Field
            label="Script Provider"
            hint="Writes topics, dialogue, cat actions, and hashtags with your authenticated local CLI."
          >
            <Controller
              control={control}
              name="scriptProvider"
              render={({ field }) => (
                <Select value={field.value} onValueChange={field.onChange}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="claude">Claude CLI</SelectItem>
                    <SelectItem value="codex">Codex CLI</SelectItem>
                  </SelectContent>
                </Select>
              )}
            />
          </Field>

          <Field label="Background Source" hint="Pexels needs an API key; Codex generates locally.">
            <Controller
              control={control}
              name="backgroundSource"
              render={({ field }) => (
                <Select value={field.value} onValueChange={field.onChange}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="pexels">Pexels (stock photos)</SelectItem>
                    <SelectItem value="codex">Codex CLI (AI generated)</SelectItem>
                  </SelectContent>
                </Select>
              )}
            />
          </Field>

          <Field label="Pexels API Key" hint="Leave blank to use generated gradient backgrounds.">
            <Input type="password" placeholder="••••••••" {...register('pexelsApiKey')} />
          </Field>

          <Field label="Batch Size" error={formState.errors.batchSize?.message}>
            <Input type="number" min={1} max={50} {...register('batchSize')} />
          </Field>

          <Field
            label="Reel Duration (ms)"
            hint="Reference reels average 13.3s; top performers run 15-30s."
          >
            <Input type="number" min={2000} max={60000} step={500} {...register('targetDurationMs')} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader title="Publishing" />
        <CardContent className="space-y-5">
          <div className="flex items-center justify-between rounded-xl bg-muted/40 px-4 py-3 ring-1 ring-border">
            <div>
              <div className="text-sm font-medium">Auto Upload</div>
              <p className="mt-0.5 text-xs text-muted-foreground">
                Off sends finished videos to the Review queue for manual approval.
              </p>
            </div>
            <Controller
              control={control}
              name="autoUpload"
              render={({ field }) => (
                <Switch checked={field.value} onCheckedChange={field.onChange} aria-label="Auto upload" />
              )}
            />
          </div>

          <div className="grid gap-5 sm:grid-cols-2">
            <Field label="Facebook Page ID">
              <Input placeholder="1234567890" {...register('fbPageId')} />
            </Field>
            <Field label="FB Access Token">
              <Input type="password" placeholder="••••••••" {...register('fbAccessToken')} />
            </Field>
            <Field label="Instagram User ID">
              <Input placeholder="1784..." {...register('igUserId')} />
            </Field>
            <Field label="IG Access Token">
              <Input type="password" placeholder="••••••••" {...register('igAccessToken')} />
            </Field>
          </div>

          <Field
            label="Default Caption Template"
            hint="Placeholders: {topic} and {hashtags}"
            error={formState.errors.defaultPostCaptionTemplate?.message}
          >
            <Textarea {...register('defaultPostCaptionTemplate')} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader title="Scheduling" subtitle="Times are entered in your target zone and stored as UTC." />
        <CardContent className="grid gap-5 sm:grid-cols-2">
          <Field label="Target Timezone">
            <Controller
              control={control}
              name="targetTimezone"
              render={({ field }) => (
                <Select value={field.value} onValueChange={field.onChange}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {US_TIMEZONES.map((t) => (
                      <SelectItem key={t.value} value={t.value}>
                        {t.label} ({t.short})
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            />
          </Field>
          <div />
          <Field label={`Morning Slot (${zoneShort})`}>
            <Input type="time" {...register('morningLocal')} />
          </Field>
          <Field label={`Afternoon Slot (${zoneShort})`}>
            <Input type="time" {...register('afternoonLocal')} />
          </Field>
          <Field label={`Evening Slot (${zoneShort})`}>
            <Input type="time" {...register('eveningLocal')} />
          </Field>
        </CardContent>
      </Card>

      <div className="flex justify-end">
        <LoadingButton type="submit" loading={save.isPending}>
          Save changes
        </LoadingButton>
      </div>
    </form>
  )
}
