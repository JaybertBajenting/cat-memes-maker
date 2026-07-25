# CatMeme Pipeline — Project Spec

> Automated cat meme reel production and publishing pipeline.
> Format: 9:16 vertical reels. Style: 2-cat POV couple/relationship humor.
> Output: Facebook Reels + Instagram Reels via Graph API.

---

## Reference Pages

Competitor/inspiration pages to study for content style, posting frequency, and caption patterns:

| Page | Link |
|---|---|
| Catchow (Reels) | https://www.facebook.com/profile.php?id=61591663922571&sk=reels_tab |
| MeowMadness (Reels) | https://www.facebook.com/profile.php?id=61591667016018&sk=reels_tab |
| JustCatThings (Reels) | https://www.facebook.com/justcatthings21/reels/ |

---

## Overview

A local desktop automation pipeline that:
1. Uses **Claude CLI** (subprocess) to batch-generate POV topic scripts
2. Uses **Codex CLI** (subprocess) to generate or source background assets
3. Uses **FFmpeg** to composite green screen cat clips, backgrounds, and text overlays into finished 9:16 reels
4. Uses **Facebook/Instagram Graph API** to publish finished reels

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                   Dashboard (React UI)               │
│  - Settings (bg source, cat clips, posting config)  │
│  - Batch trigger (generate N videos)                │
│  - Queue viewer (pending / done / failed)           │
│  - Approve/Reject queue (if auto-upload off)        │
│  - Post scheduler (US timezone)                     │
│  - Calendar page (content schedule view)            │
└──────────────────────┬──────────────────────────────┘
                       │ HTTP
┌──────────────────────▼──────────────────────────────┐
│               ASP.NET Core 9 API                    │
│  - JobController  (trigger batch)                   │
│  - QueueController (status, retry)                  │
│  - SettingsController                               │
│  - PostController (manual trigger, schedule)        │
└──────┬──────────────┬──────────────────┬────────────┘
       │              │                  │
  ┌────▼────┐   ┌─────▼──────┐   ┌──────▼──────┐
  │ Topic   │   │  Asset     │   │  FFmpeg     │
  │ Gen Job │   │  Gen Job   │   │  Edit Job   │
  │(Claude  │   │(Codex CLI /│   │(Composite + │
  │  CLI)   │   │  Pexels)   │   │  Text)      │
  └────┬────┘   └─────┬──────┘   └──────┬──────┘
       │              │                  │
       └──────────────▼──────────────────┘
                      │
              ┌───────▼────────┐
              │  Post Job      │
              │ (Graph API →   │
              │  FB + IG)      │
              └────────────────┘
```

---

## Tech Stack

### Backend
| Layer | Choice |
|---|---|
| Runtime | .NET Core 9 |
| API | ASP.NET Core 9 minimal API |
| Background Jobs | Hangfire 1.8.x |
| Database | SQL Server (EF Core 9.0.9) |
| Logging | Serilog 8.0.0 |
| HTTP Client | RestSharp 112.1.0 |
| Validation | FluentValidation 12.0.0 |
| Config | DotNetEnv 3.1.1 |

### Frontend
| Layer | Choice |
|---|---|
| Framework | React 19.2.4 + Vite 7.3.1 |
| Routing | TanStack Router 1.168.25 |
| Server State | TanStack Query 5.100.6 |
| UI | shadcn 4.6.0 + Tailwind 4.2.1 |
| State | Zustand 5.0.14 |
| Forms | react-hook-form 7.74.0 + Zod 4.3.6 |

### External Tools (subprocess)
| Tool | Purpose |
|---|---|
| Claude CLI | POV topic + caption generation |
| Codex CLI | AI background image generation |
| FFmpeg | Green screen removal, compositing, text, encoding |
| Pexels API | Stock background images/video (optional per settings) |

---

## Feature Breakdown

### 1. Topic Generation (Claude CLI)

Claude CLI is called as a subprocess with a structured prompt to generate a batch of POV scripts.

**Prompt contract:**
```
Generate {count} POV cat meme reel scripts in JSON format.
Each script must have:
- "topic": short POV title (e.g. "POV: When your gf is on her period")
- "cat_a_label": label for cat A (e.g. "Me:")
- "cat_b_label": label for cat B (e.g. "Her:")
- "cat_a_position": "left" | "right" | "top" | "bottom" | "overlay_small"
- "cat_b_position": same options
- "caption_top": optional top text
- "caption_bottom": optional bottom text
- "mood": "funny" | "wholesome" | "dramatic"
Return only a JSON array, no markdown.
```

**Output:** `topics.json` — array of script objects saved to `/pipeline/topics/`

---

### 2. Background Asset Generation

Configurable per project settings:

**Mode A — Stock Image (Pexels API)**
- Query Pexels with a keyword derived from the topic mood (e.g. "cozy living room", "city night")
- Download highest resolution image
- Crop/resize to 1080x1920

**Mode B — AI Generated (Codex CLI)**
- Codex CLI called as subprocess with a prompt like: `"Generate a cozy apartment living room background, vertical 9:16, cinematic lighting, no people"`
- Output saved as PNG to `/pipeline/assets/backgrounds/`

Setting in UI: `Background Source = "pexels" | "codex"`

---

### 3. Cat Green Screen Assets

Pre-downloaded cat green screen clips stored in `/pipeline/assets/cats/`.

Each clip has metadata stored in DB:
- `clip_id`, `filename`, `duration_ms`, `mood_tags[]`, `facing` (left/right)

**Clip selection:** Based on script `mood` field — funny → energetic clips, wholesome → calm clips, dramatic → expressive clips.

---

### 4. FFmpeg Editing Pipeline

Each video goes through these FFmpeg stages in sequence:

#### Stage 1 — Green Screen Removal (Chroma Key)
```
ffmpeg -i cat_clip.mp4 -vf "chromakey=0x00FF00:0.3:0.2" cat_no_bg.webm
```

#### Stage 2 — Resize & Position Cat(s)
Each cat clip is resized and positioned based on the layout defined in the script's `cat_a_position` / `cat_b_position`.

**Supported layouts:**
| Layout | Description |
|---|---|
| `side_by_side` | Cat A left half, Cat B right half |
| `top_bottom` | Cat A top, Cat B bottom |
| `overlay_small` | One cat fullscreen, other as small overlay (corner) |
| `stacked_center` | Both cats centered vertically |

Layout is auto-assigned based on `cat_a_position` + `cat_b_position` combo from the topic script.

#### Stage 3 — Composite onto Background
```
ffmpeg -i background.jpg -i cat_a_no_bg.webm -i cat_b_no_bg.webm \
  -filter_complex "[0][1]overlay=...[tmp];[tmp][2]overlay=..." \
  -t {duration} composite.mp4
```

#### Stage 4 — Text Overlay
FFmpeg `drawtext` filter for:
- **POV title** — top center, large bold white text with shadow
- **Cat A label** — positioned near Cat A
- **Cat B label** — positioned near Cat B
- **Caption top / bottom** — optional, smaller text

Font: Impact or custom bold font stored in `/pipeline/assets/fonts/`

#### Stage 5 — Final Encode
```
ffmpeg -i composite_with_text.mp4 \
  -vf "scale=1080:1920" \
  -c:v libx264 -crf 23 -preset fast \
  -c:a aac -b:a 128k \
  -movflags +faststart \
  output/{video_id}.mp4
```

Output stored in `/pipeline/output/`

---

### 5. Auto-Posting via Graph API

#### Facebook Reels
1. Upload video to Facebook via `POST /me/videos` (resumable upload)
2. Publish as Reel via `POST /{page-id}/video_reels`

#### Instagram Reels
1. Create media container via `POST /{ig-user-id}/media` with `media_type=REELS`
2. Publish via `POST /{ig-user-id}/media_publish`

**Post config per video:**
- `caption` — auto-generated from topic + hashtags
- `scheduled_publish_time` — optional, set from UI scheduler

**Hashtag generation:** Claude CLI called with topic to generate 10–15 relevant hashtags appended to caption.

---

### 6. Calendar Page

A dedicated `/calendar` route in the React UI showing all scheduled and posted content in a monthly calendar view.

**Each calendar day cell shows:**
- Thumbnail of the video scheduled/posted that day
- Platform badge (FB / IG / Both)
- Status chip: `Scheduled`, `Posted`, `Failed`
- Posting time (displayed in the selected US timezone)

**Interactions:**
- Click a day cell → opens a side panel with full post details (topic, caption, hashtags, video preview)
- Click a scheduled post → option to reschedule or cancel the post
- Empty day → quick-add button to schedule a ready video to that day

**Timezone display:** All times shown in the calendar are in the user's selected US timezone (see Settings). Stored as UTC in DB, converted on the frontend.

---

### 7. US Timezone Scheduler

All post scheduling is timezone-aware, targeting US audiences.

**Supported timezones (selectable in Settings):**
| Label | IANA Key |
|---|---|
| Eastern Time | `America/New_York` |
| Central Time | `America/Chicago` |
| Mountain Time | `America/Denver` |
| Pacific Time | `America/Los_Angeles` |

**How it works:**
- User picks their target timezone + daily posting time in Settings (e.g. `6:00 PM ET`)
- Frontend converts to UTC before sending to API
- DB stores `scheduled_at` as UTC
- `PostSchedulerJob` (Hangfire recurring job) compares current UTC time against `scheduled_at` UTC — no timezone math at job execution time
- Calendar UI converts UTC back to the selected US timezone for display using `Intl.DateTimeFormat`

**Best time defaults (pre-filled in Settings, editable):**
- Morning slot: `9:00 AM ET`
- Afternoon slot: `12:00 PM ET`
- Evening slot: `7:00 PM ET`

---

### 8. Auto-Upload Toggle + Approve/Reject Flow

Controlled by a single toggle in Settings: **Auto Upload = On / Off**

#### When Auto Upload = ON
- Pipeline flows normally: `VideoEditJob` completes → video status set to `ready` → `PostSchedulerJob` picks it up and posts automatically

#### When Auto Upload = OFF
- After `VideoEditJob` completes, video status is set to `awaiting_approval` instead of `ready`
- A dedicated **Review Queue** section appears in the Dashboard UI
- Each video card in the Review Queue shows:
  - Inline video preview (HTML5 `<video>` tag pointing to local output path)
  - Topic title + generated caption
  - Two action buttons: **✅ Approve** and **❌ Reject**
- **Approve** → sets status to `ready`, triggers `PostSchedulerJob` for that video
- **Reject** → sets status to `rejected`, soft-deletes the output file, frees the topic for regeneration (optional)
- Approved videos then follow the normal scheduling flow (scheduled time or immediate if no schedule set)

**DB status flow with toggle off:**
```
rendering → awaiting_approval → ready → posted
                             ↘ rejected
```

---

### 9. Cancel During Rendering

While a `VideoEditJob` is actively running, the user can cancel it from the Queue UI.

**How it works:**
- Each `VideoEditJob` stores its **Hangfire Job ID** and the **OS process ID** of the running FFmpeg subprocess in the `videos` table (`hangfire_job_id`, `ffmpeg_pid`)
- The UI shows a **Cancel** button on any video card with status `rendering`
- Clicking Cancel calls `DELETE /api/jobs/{videoId}/cancel`
- The API:
  1. Calls `BackgroundJob.Delete(hangfireJobId)` to remove from Hangfire queue (if not yet started)
  2. If already running: sends `SIGTERM` / `Process.Kill()` to the FFmpeg PID
  3. Deletes any partial output files from `/pipeline/output/`
  4. Sets video status to `cancelled`
- Cancelled videos appear in the queue with a `Cancelled` chip and a **Retry** button to re-queue

**Edge case:** If the job finishes between the cancel request and the kill signal, the API detects the completed status and returns a `409 Conflict` — "Video already completed, cannot cancel."

---

## Database Schema (DBML)

```dbml
Table settings {
  id int [pk, increment]
  background_source varchar // "pexels" | "codex"
  pexels_api_key varchar
  fb_page_id varchar
  fb_access_token varchar
  ig_user_id varchar
  ig_access_token varchar
  default_post_caption_template text
  auto_upload bit [default: true] // if false, videos go to awaiting_approval
  target_timezone varchar // IANA key e.g. "America/New_York"
  default_post_time_utc time // stored as UTC, derived from target_timezone + user input
  created_at datetime
  updated_at datetime
}

Table cat_clips {
  id int [pk, increment]
  filename varchar
  duration_ms int
  mood_tags varchar // comma-separated: "funny,energetic"
  facing varchar // "left" | "right" | "neutral"
  is_active bit
  created_at datetime
}

Table topics {
  id int [pk, increment]
  topic varchar
  cat_a_label varchar
  cat_b_label varchar
  cat_a_position varchar
  cat_b_position varchar
  caption_top varchar
  caption_bottom varchar
  mood varchar // "funny" | "wholesome" | "dramatic"
  status varchar // "pending" | "editing" | "done" | "failed"
  generated_at datetime
}

Table videos {
  id int [pk, increment]
  topic_id int [ref: > topics.id]
  cat_a_clip_id int [ref: > cat_clips.id]
  cat_b_clip_id int [ref: > cat_clips.id]
  background_path varchar
  output_path varchar
  duration_ms int
  status varchar // "rendering" | "awaiting_approval" | "ready" | "posted" | "failed" | "cancelled" | "rejected"
  hangfire_job_id varchar // for cancel support
  ffmpeg_pid int // OS PID of running FFmpeg process, null when not rendering
  error_message text
  created_at datetime
  completed_at datetime
  approved_at datetime
  approved_by varchar // future: multi-user support
}

Table posts {
  id int [pk, increment]
  video_id int [ref: > videos.id]
  platform varchar // "facebook" | "instagram" | "both"
  caption text
  hashtags text
  scheduled_at datetime // stored as UTC
  scheduled_at_timezone varchar // IANA key, for display on calendar
  posted_at datetime
  fb_post_id varchar
  ig_post_id varchar
  status varchar // "scheduled" | "posted" | "failed" | "cancelled"
  error_message text
  created_at datetime
}
```

---

## Pipeline Job Flow (Hangfire)

```
[Batch Trigger]
      │
      ▼
TopicGenerationJob        → calls Claude CLI subprocess → inserts N topics
      │
      ▼
AssetGenerationJob        → per topic: fetch/generate background → saves path to DB
      │
      ▼
VideoEditJob              → pick cat clips → run FFmpeg stages → stores ffmpeg_pid during render
      │                      [Cancel available here via DELETE /api/jobs/{id}/cancel]
      ▼
  auto_upload ON?
  ├── YES → status = "ready" → PostSchedulerJob picks up automatically
  └── NO  → status = "awaiting_approval" → user sees Approve/Reject in Review Queue
                  │
            [Approve] → status = "ready" → PostSchedulerJob
            [Reject]  → status = "rejected" → output file cleaned up
                  │
                  ▼
PostSchedulerJob          → per video "ready" with scheduled_at <= UTC now
                            → post to FB + IG via Graph API → update post record
```

Each job retries 3x with exponential backoff. `VideoEditJob` stores `hangfire_job_id` + `ffmpeg_pid` on start for cancel support.

---

## Folder Structure

```
/pipeline/
  /topics/          → generated topics.json files
  /assets/
    /cats/          → green screen cat clips (.mp4/.webm)
    /backgrounds/   → generated or downloaded background images
    /fonts/         → Impact.ttf or custom bold font
  /output/          → finished reels ready to post
  /logs/            → Serilog output
```

---

## Settings UI (React)

| Setting | Type | Description |
|---|---|---|
| Background Source | Toggle (Pexels / Codex) | Which asset source to use |
| Pexels API Key | Text input | Required if Pexels selected |
| Batch Size | Number input | How many videos to generate per run |
| Facebook Page ID | Text input | Target FB page |
| FB Access Token | Password input | Long-lived page token |
| Instagram User ID | Text input | IG Business account |
| IG Access Token | Password input | Long-lived IG token |
| Default Caption Template | Textarea | `{topic} {hashtags}` |
| Auto Upload | Toggle (On/Off) | Off = videos go to Review Queue for manual approval |
| Target Timezone | Dropdown | ET / CT / MT / PT — affects scheduler + calendar display |
| Morning Slot | Time picker | e.g. `9:00 AM` in selected timezone |
| Afternoon Slot | Time picker | e.g. `12:00 PM` in selected timezone |
| Evening Slot | Time picker | e.g. `7:00 PM` in selected timezone |

---

## Dashboard — Generate Videos UI

The main Dashboard page has a dedicated **Generate Videos** panel at the top, separate from Settings. This is the primary action entry point.

**UI layout:**
```
┌─────────────────────────────────────────────────┐
│  Generate Videos                                │
│                                                 │
│  How many videos?                               │
│  [ Number input, min 1, max 50, default 5 ]     │
│                                                 │
│  [ 🎬 Generate Videos ]   ← primary CTA button  │
│                                                 │
│  Estimated time: ~{n * 2} minutes               │
└─────────────────────────────────────────────────┘
```

**Behavior:**
- Number input is validated client-side: min `1`, max `50`, integers only. Shows inline error if out of range.
- Clicking **Generate Videos** calls `POST /api/jobs/batch` with `{ count: n }` in the body
- Button immediately switches to a **loading/disabled state** with a spinner and label "Starting..." while the API responds
- On success: button resets, a toast notification appears ("Generating {n} videos — check the queue"), and the Queue section below auto-refreshes
- On error: toast shows the error message, button re-enables
- Estimated time is a simple frontend calculation: `n × 2 minutes` shown as a hint below the button (not a guarantee)
- The number input value is persisted in Zustand so it survives page navigation (not reset on re-render)

**API endpoint:**
```
POST /api/jobs/batch
Body: { count: int }
Response: { batchId: string, jobIds: string[] }
```

---

## MVP Scope

| Feature | MVP |
|---|---|
| Claude CLI topic generation | ✅ |
| Pexels background source | ✅ |
| Codex CLI background gen | ⬜ (v2) |
| FFmpeg chroma key + composite | ✅ |
| Text overlay (POV title + labels) | ✅ |
| Facebook Reels posting | ✅ |
| Instagram Reels posting | ✅ |
| Hangfire job queue | ✅ |
| Dashboard UI | ✅ |
| Generate Videos panel (count input + button) | ✅ |
| US timezone-aware scheduler | ✅ |
| Calendar page (monthly view) | ✅ |
| Auto Upload toggle | ✅ |
| Review Queue (Approve / Reject) | ✅ |
| Cancel during rendering | ✅ |
| Clip mood-based selection | ✅ |
| Multiple layout support | ✅ |
| Hashtag generation via Claude CLI | ✅ |

---

## Environment Variables (.env)

```env
PEXELS_API_KEY=
FB_PAGE_ID=
FB_ACCESS_TOKEN=
IG_USER_ID=
IG_ACCESS_TOKEN=
PIPELINE_BASE_PATH=C:/catmeme/pipeline
CLAUDE_CLI_PATH=claude
CODEX_CLI_PATH=codex
FFMPEG_PATH=ffmpeg
DB_CONNECTION_STRING=Server=...;Database=CatMemePipeline;...
DEFAULT_TIMEZONE=America/New_York
AUTO_UPLOAD=true
```
