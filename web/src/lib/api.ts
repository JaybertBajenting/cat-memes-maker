export interface VideoDto {
  id: number
  topicId: number
  topic: string
  mood: string
  layout: string
  status: VideoStatus
  error: string | null
  durationMs: number
  videoUrl: string | null
  thumbUrl: string | null
  createdAt: string
  completedAt: string | null
  batchId: string | null
  caption: string | null
  hashtags: string | null
  scheduledAt: string | null
  postId: number | null
  postStatus: string | null
}

export type VideoStatus =
  | 'rendering'
  | 'awaiting_approval'
  | 'ready'
  | 'posted'
  | 'failed'
  | 'cancelled'
  | 'rejected'

export interface SettingsDto {
  scriptProvider: string
  backgroundSource: string
  pexelsApiKey: string | null
  fbPageId: string | null
  fbAccessToken: string | null
  igUserId: string | null
  igAccessToken: string | null
  defaultPostCaptionTemplate: string
  autoUpload: boolean
  targetTimezone: string
  batchSize: number
  morningSlotUtc: string
  afternoonSlotUtc: string
  eveningSlotUtc: string
  targetDurationMs: number
}

export interface StatsDto {
  total: number
  rendering: number
  awaitingApproval: number
  ready: number
  posted: number
  failed: number
  cancelled: number
  clips: number
}

export interface PostDto {
  id: number
  videoId: number
  topic: string
  platform: string
  caption: string
  hashtags: string
  scheduledAt: string | null
  scheduledAtTimezone: string
  postedAt: string | null
  status: string
  errorMessage: string | null
  fbPostId: string | null
  igPostId: string | null
  videoStatus: string | null
  videoUrl: string | null
  thumbUrl: string | null
}

export interface ClipDto {
  id: number
  filename: string
  durationMs: number
  moodTags: string
  keyColor: string
  keySimilarity: number
  cropFilter: string | null
  width: number
  height: number
  isActive: boolean
}

/** Surfaces the API's problem-details / message body instead of a bare status code. */
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message)
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
  })

  if (!res.ok) {
    let message = `Request failed (${res.status})`
    try {
      const body = await res.json()
      message = body.message ?? body.title ?? message
      if (body.errors) {
        const first = Object.values(body.errors as Record<string, string[]>)[0]
        if (first?.[0]) message = first[0]
      }
    } catch {
      // non-JSON error body, keep the generic message
    }
    throw new ApiError(message, res.status)
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

/** One page of results plus the size of the whole filtered set. */
export type Paged<T> = { items: T[]; total: number; page: number; pageSize: number }

export const api = {
  health: () => request<{ status: string; pipeline: string }>('/api/health'),

  stats: () => request<StatsDto>('/api/queue/stats'),

  queue: (status?: string, page = 1, pageSize = 24) => {
    const p = new URLSearchParams()
    if (status) p.set('status', status)
    p.set('page', String(page))
    p.set('pageSize', String(pageSize))
    return request<Paged<VideoDto>>(`/api/queue?${p.toString()}`)
  },

  /** A topic renders exactly one video and ignores count; blank generates topics as before. */
  generate: (count: number, topic?: string, dialogue?: string) =>
    request<{ batchId: string; jobIds: string[] }>('/api/jobs/batch', {
      method: 'POST',
      body: JSON.stringify({
        count,
        topic: topic?.trim() || null,
        dialogue: dialogue?.trim() || null,
      }),
    }),

  cancel: (videoId: number) =>
    request<{ message: string }>(`/api/jobs/${videoId}/cancel`, { method: 'DELETE' }),

  retry: (videoId: number) =>
    request<{ jobId: string }>(`/api/jobs/${videoId}/retry`, { method: 'POST' }),

  approve: (id: number) =>
    request<{ message: string }>(`/api/videos/${id}/approve`, { method: 'POST' }),

  reject: (id: number) =>
    request<{ message: string }>(`/api/videos/${id}/reject`, { method: 'POST' }),

  getSettings: () => request<SettingsDto>('/api/settings'),

  saveSettings: (s: SettingsDto) =>
    request<{ message: string }>('/api/settings', { method: 'PUT', body: JSON.stringify(s) }),

  posts: (from?: Date, to?: Date) => {
    const p = new URLSearchParams()
    if (from) p.set('from', from.toISOString())
    if (to) p.set('to', to.toISOString())
    return request<PostDto[]>(`/api/posts${p.toString() ? `?${p}` : ''}`)
  },

  reschedule: (id: number, scheduledAtUtc: Date, platform?: string) =>
    request<{ message: string }>(`/api/posts/${id}/schedule`, {
      method: 'PUT',
      body: JSON.stringify({ scheduledAtUtc: scheduledAtUtc.toISOString(), platform }),
    }),

  cancelPost: (id: number) =>
    request<{ message: string }>(`/api/posts/${id}`, { method: 'DELETE' }),

  clips: () => request<ClipDto[]>('/api/clips'),

  importClips: (sourcePath?: string) =>
    request<{ imported: number; skipped: number }>('/api/clips/import', {
      method: 'POST',
      body: JSON.stringify({ sourcePath: sourcePath ?? null }),
    }),

  deactivateClip: (id: number) =>
    request<{ message: string }>(`/api/clips/${id}`, { method: 'DELETE' }),
}
