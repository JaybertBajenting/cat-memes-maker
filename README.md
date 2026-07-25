# CatMeme Maker

Automated POV cat meme reel production and publishing pipeline.
9:16 vertical reels, rendered locally with FFmpeg and published to Facebook + Instagram Reels.

**Double-click `CatMeme Maker` on your desktop to start it.**

---

## What it does

1. **Topic generation** — your choice of Claude CLI or Codex CLI writes POV scripts. Claude keeps the built-in fallback; a selected Codex provider reports CLI failures instead of silently substituting generic output
2. **Background** — Pexels stock, Codex CLI generation, or a generated gradient if no API key is set
3. **Render** — FFmpeg keys the green screen, follows the authored beat storyboard, and burns in text/effects
4. **Visual QA** — sampled encoded frames are scored per beat; weak cats are recast and structural failures regenerate once
5. **Publish** — Graph API posts to Facebook and Instagram on a US-timezone schedule

Everything runs on your machine. One process serves both the API and the UI.

## Default reference-quality profile

Every newly generated script uses the versioned `reference-default-v1` contract. It requires
**6–9 purposeful beats**, begins with setup, escalates or marks elapsed time, and ends on the
strongest payoff. Historical unmarked scripts remain renderable under the legacy rules.

Every profiled beat carries renderer-safe controls for title visibility/style, role-label
visibility, lighting, cat scale, transition, sound cue, and optional paired interaction. The
values come from closed vocabularies; generated text cannot inject a filter, path, or command.
The house style uses an outlined top title, normally hides role labels, replaces time jumps with
full-frame cards, and gives the payoff its own clean reveal.

Generation is asset-aware. The real caster proves **every beat** before the topic is saved. A
script is rejected if the current library cannot perform a requested action pair. Whole-scene
physical gags live in the separate `paired_interaction_assets` library so contact timing is not
faked with two unrelated loops; the initial audited `slap` asset is clip 369 and remains disabled
in the ordinary one-cat pool.

After FFmpeg and technical verification, the app samples the encoded frame inside every beat and
creates a contact sheet under `pipeline/work/visual-qa`. Lighting-invariant structural checks cover
cat visibility, size, region, authored scale, lighting, titles/labels, and full-frame transitions.
The Claude/Codex CLI selected in Settings then inspects that contact sheet for the pose, emotion,
and action mismatches that pixel math cannot understand. This semantic gate fails closed: a missing
reviewer cannot silently approve a possibly inappropriate cat. Recastable failures quarantine the
weak asset and render again, up to three attempts; structural failures regenerate the storyboard
once. Three reference topics must each pass as three independently rendered files. Reusing one
output three times is rejected by the repeatability gate.

---

## Running it

| Action | How |
|---|---|
| Start the app | Double-click **CatMeme Maker** on the desktop |
| Stop the app | Close the launcher console window |
| Rebuild after code changes | `launch\build.ps1` |
| Open manually | <http://localhost:5099> |
| Job queue internals | <http://localhost:5099/hangfire> |

The launcher starts SQL LocalDB, boots the service, waits for it to answer, and opens your browser.
If the app is already running it just opens the browser instead of starting a second copy.

---

## First run

The clip library is cut from **every compilation listed in `COMPILATION_PATHS`** (semicolon
separated in `.env`). Each clip records its source, and **Reanalyze retires any clip whose
source is not on the list** — so removing a path still permanently withdraws its clips.

This used to be a single `DEFAULT_COMPILATION_PATH`, which capped the library at whatever one
video contained: the 3:20 source yielded **11 usable clips, one per action tag**, with
`staring` and `angry` being the *same* clip. That is too thin to cut between shots inside one
reel. Adding the 13:09 source took the pool to **38 clips**, with 8 `staring`, 6 `talking` and
5 `shocked` — and every one of the 132 action pairs castable.

To add a source: append it to `COMPILATION_PATHS`, then **Clips → Import compilation**
(do *not* reset — that deletes clips from the same source), then tag and curate.

Import is not a plain file copy. Each clip is analysed on the way in:

- **Scene splitting** — the compilation is cut at scene changes into individual clips
- **Letterbox removal** — `cropdetect` strips black bars that chroma key cannot touch
- **Per-clip key colour** — the border is sampled to find that clip's actual green
- **Subject box** — the cat's bounding box is found so it fills the frame instead of sitting
  as a small island in a mostly-green plate

That last pair matters: the source compilation contains **75 distinct green values**, so a
single hardcoded `0x00FF00` key fails on most clips.

### Clip quality control

Not every clip in a compilation is usable, and a bad one still produces a valid mp4 — it
just renders the cat sitting on a hard coloured rectangle. Two separate checks catch this,
and **Clips → Reanalyze** re-runs them without re-extracting:

| Check | Measures | Limit | Catches |
|---|---|---|---|
| Backdrop | share of the **full frame** border that is neither key colour nor letterbox black | 0.15 | clips shot over a flat blue/orange plate rather than green |
| Trapped bars | share of the **subject box** that is pure black | 0.015 | letterbox bars caught inside the crop |

They need to be separate. The backdrop check must run uncropped, because the subject box is
deliberately tight and the cat itself sits on its border. The trapped-bar check must run
inside the box, because that is the only place the bars survive.

Neither can be fixed at render time. A second key pass wide enough to remove them also eats
matching fur and shadow — tested, and it punched holes straight through a brown cat.

### Newly imported clips are inert until tagged

Import can only guess tags from clip duration (`funny,energetic`, `dramatic,wholesome`), and
none of those are in the casting vocabulary. A freshly imported clip is therefore **active but
impossible to cast** — it sits in the library looking usable and never gets picked.

`PUT /api/clips/{id}` sets `moodTags` (and `isActive`). It **refuses to leave a clip active
without at least one castable action tag**, so the inert-clip state cannot be recreated by
hand. Tagging is a judgement call about what the cat is visibly *doing*, so it is done off the
contact sheet below, not by any metric.

### Curation beats the metrics, and now outranks them

Reanalyze re-measures every clip and flips `IsActive` both ways. It used to re-enable **any**
disabled clip that passed the contamination checks — which silently undid hand curation and put
52 rejected clips back into circulation in one run.

The metrics measure *keying*, not *content*. A husky, a goat, a cat in a banana costume and a
near-duplicate all score as perfectly clean cut-outs. So Reanalyze now only reverses **its own**
verdicts: a clip disabled from the contact sheet stays disabled.

The two are genuinely complementary — on the last pass the eye caught dogs, costumes and
duplicates, while the trapped-bar metric caught two clips with letterbox bars the eye had
missed. Run both.

### The magenta contact sheet (do this after every import)

Automated metrics catch gross contamination but miss the subtle failures: a rectangle of
*slightly off* green pasted around a cat, semi-transparent over-keying, a burned-in
"SUBSCRIBED" button. Four different metrics were tried against these; none separated them
reliably. What works is looking:

1. Key every active clip over a magenta plate and tile the results into one contact sheet
2. Bad clips are unmistakable in seconds (hard rectangles, magenta bleeding through fur)
3. Disable them with the Clips page or `DELETE /api/clips/{id}`

Under a minute of eyeballing per import, and it is the only filter that caught everything.

---

## Settings that need your input

Everything works out of the box except publishing, which needs credentials:

The **Script Provider** setting chooses Claude CLI or Codex CLI for topics, dialogue,
cat actions, hashtags, and the sampled-frame semantic quality review. Claude stays the default
for existing installs. Both providers use their local CLI login; executable paths can be
overridden with `CLAUDE_CLI_PATH` and `CODEX_CLI_PATH` in `.env`.

Codex runs with `--ignore-user-config`, so app automation is isolated from personal model
and reasoning settings while still using the authenticated CLI session. A non-zero exit or
empty response is surfaced as a job failure rather than producing a generic cat pair.

| Setting | Needed for |
|---|---|
| Pexels API Key | Photo backgrounds (without it you get generated gradients) |
| Facebook Page ID + Access Token | Posting to FB Reels |
| Instagram User ID + Access Token | Posting to IG Reels |
| `PublicMediaBaseUrl` in `.env` | **Required for Instagram** — see below |

### Instagram needs a public URL

Facebook accepts a direct file upload. Instagram does not: it only accepts a publicly
reachable HTTPS video URL. Until `PublicMediaBaseUrl` in `.env` points at a tunnel or CDN
that serves the `pipeline/output` folder, IG publishing will fail with a clear message
rather than silently doing nothing. Facebook publishing works without it.

---

## Making a video from your own topic

The Dashboard's **Topic** box is optional. Leave it blank and a batch is generated as before.
Type a line into it and the pipeline renders exactly one video around that line:

- **Your wording is kept verbatim.** Only the craft around it — labels, actions, layout, mood,
  and dialogue — is written to fit. A model asked to "improve" the line reliably rewrites away
  the specific detail that made it worth filming.
- **Dialogue box appears once a topic is entered.** One turn per line, `a:` or `b:` prefix
  (`left:`/`right:`/`1:`/`2:` also work). Anything you write **overrides** what the model
  proposed; leave it blank and the model decides whether the topic warrants a conversation.
- A line with no recognisable prefix is skipped rather than guessed at, so a stray note in
  the box cannot silently become a spoken turn.

A custom topic still passes every gate a generated one does — `ScriptValidator`, castability,
and render preflight. The difference is what happens when it fails: a generated topic is
dropped and the batch moves on, but a rejected custom topic is recorded as a **failed video
carrying the reason**, because silently discarding something someone typed by hand looks
like the button did nothing.

---

## Conversations

A legacy topic can carry an optional `dialogue` array. New AI output always carries the required
6–9 beat quality-profile storyboard; an unmarked stored topic may still omit it and render the
original static style.

```json
"dialogue": [
  { "speaker": "b", "text": "Can you wake me up?", "cat_a_action": "staring", "cat_b_action": "asking" },
  { "speaker": "a", "text": "Okay" },
  { "speaker": "a", "text": "", "cat_a_action": "sleeping", "cat_b_action": "sleeping" },
  { "speaker": "a", "text": "Time to wake up", "cat_a_action": "talking", "cat_b_action": "shocked" },
  { "speaker": "a", "text": "A few moments later", "stage": "transition" },
  { "speaker": "b", "text": "Why didn't you wake me?", "cat_a_action": "innocent", "cat_b_action": "angry" }
]
```

Measured from a reference reel (15.9s, seven turns) rather than guessed:

| Property | What the reference does | Applied as |
|---|---|---|
| Turn length | 1–4s, roughly proportional to how much there is to read | weight by character count |
| Between turns | ~0.5s of nothing on screen | `TurnGapSec` = 0.45 — the silence is what makes it read as turn-taking rather than a caption |
| Who is speaking | never named; the line simply sits on that cat's side | each turn is centred in its speaker's half |
| Last turn | stays up to the final frame | the punchline is held to the end of the reel |
| Voiceover | none — the audio is cat sounds | text only, clips keep their own audio |

**Timing is derived, not authored.** The model supplies words, speaker, and optional visual
state changes (`cat_a_action`, `cat_b_action`, `stage`); `RenderService.ScheduleDialogue`
lays them out against the reel's real duration. Empty visual beats reserve 3.5 seconds, and
dialogue-heavy scripts can raise the configured target to the reading-time recommendation
(never above 60 seconds). Rendering uses one `drawtext` per turn gated by
`enable='between(t,start,end)'`, so a turn costs a filter stage, not a re-encode.

Visual actions persist until changed. Each state is cast independently with exact action
tags plus topic/role/dialogue semantics, so a story can move from asking, to sleeping, to
waking, to an angry payoff instead of looping one pair for the whole reel. `transition`
creates an opaque full-frame card. Sleeping/sleeping is the sole same-action exception: one
clean sleeper may be mirrored and phase-shifted to form the pair.

Dialogue replaces `caption_top` / `caption_bottom` when present — both want the same strip of
canvas between the title and the cats.

`ScriptValidator` rejects: more than 12 or fewer than 2 turns, a turn over 60 characters (it wraps into
a wall at half-canvas width), emoji, an unknown speaker, a one-sided "conversation", a
speaker-`b` line on a `single_pov` reel, and dialogue JSON that will not parse.

---

## Queue and Review are paged

`GET /api/queue` takes `page` and `pageSize` and returns `{ items, total, page, pageSize }`.
Page size defaults to 24 and is clamped to 100; a page number past the end lands on the last
page rather than an empty list, so a stale link never shows nothing.

This replaced a hard `.Take(200)` with no paging at all. The cap did not just limit the view —
past 200 videos it silently dropped the oldest, with nothing in the UI to say anything was
missing. Queue and Review now render pager controls, and the Dashboard asks for the 8 rows it
actually displays rather than fetching the whole table.

---

## Auto Upload and the review queue

Controlled by one toggle in Settings.

- **On** — rendered videos go straight to `ready` and post on their next scheduled slot
- **Off** (default) — videos land in **Review** with an inline player and Approve / Reject buttons

```
rendering ──▶ awaiting_approval ──▶ ready ──▶ posted
                                 ╰─▶ rejected
```

Rejecting deletes the output file and cancels any scheduled post.

---

## Validation before a render

Two gates run before ffmpeg is launched, and they check different things.

**`ScriptValidator`** gates what the reel *says* — title length, renderable characters, labels
present, actions inside the clip vocabulary, and the quiet-character rule. It runs at topic
generation and again at render time, because a topic can sit in the queue while the library
changes underneath it.

**`RenderPreflight`** gates what the reel is *made of*. It takes the finished `RenderRequest`
— the exact object handed to ffmpeg, so what is checked and what is rendered cannot drift
apart — and validates it against the files actually on disk:

| Check | Catches |
|---|---|
| Background exists, decodes fully, is exactly 1080x1920 | half-written plate, deleted file, wrong-size image. Decoded in full, not header-identified: a truncated PNG has a perfectly valid header |
| Clip files exist and are not stubs | a `cat_clips` row that outlived its file after a reset-import |
| `KeyColor` matches `0xRRGGBB`, similarity in (0,1] | these are interpolated **raw** into the filter_complex string, so a malformed value rewrites the filtergraph instead of erroring |
| `CropFilter` is `w:h:x:y`, and sits inside the file's real frame | a crop measured at import but applied to whatever file now carries that name |
| Stored subject size equals the crop's `w:h` | the row describing a different file than the one on disk |
| Target duration inside 2–60s | the renderer clamps to this range while the verifier compares against the raw target — an out-of-range `TargetDurationMs` used to fail **every** video on duration drift, permanently |

### Casting is decided for both cats at once

Both cats are cast in a single pass (`ClipLibraryService.CastAsync`). Casting cat A first
and cat B from whatever is left strands cat B whenever the clip it needs was also a
candidate for cat A — and in this library that is the normal case, not an edge case:

| Action | Clips that perform it |
|---|---|
| talking, gagging, asking, innocent, dancing, begging, silly, shocked, eating, vibing | exactly **one** each |
| staring **and** angry | the same single clip |

So cat A may only take a clip that still leaves cat B something to play. Two failure modes
remain genuinely impossible and are now refused *before a topic is ever saved*:

- **both cats given the same action** — rejected by `ScriptValidator`. It is also not the
  format: the joke in every reference reel is the contrast between two reactions
- **`staring` + `angry`** — one clip carries both tags, so it cannot play two cats. Topic
  generation asks the real caster whether each script is performable and drops the ones
  that are not, naming the conflicting clip

Of the 132 ordered action pairs, 130 are castable. To make the last two work, tag a second
clip with `staring` or `angry` on the Clips page.

### Failures are repaired, not fatal

A failed check replaces the offending input and re-checks, up to 4 attempts:

- **bad clip** → disabled with the exact reason in `DisabledReason`, then re-cast from the
  remaining pool. Visible on the Clips page and reversible there
- **bad background** → regenerated
- **out-of-range duration** → clamped, and the clamped value is what the verifier is given
- **weak, missing, or semantically wrong cat** → that clip is quarantined with the QA reason and the weak beat is recast, up to three quality attempts
- **weak paired interaction** → that whole-scene asset is quarantined and a different approved interaction is selected
- **broken time card or structural visual failure** → the storyboard is regenerated once, then rejected if it still misses the profile

Only when the loop still cannot assemble a valid request does the video fail, with the
precise rule that tripped in its error message. One stale row cannot take a whole batch down.

Disabling is deliberate and evidence-backed. Preflight disables a clip only for a fact about the
file (missing, unreadable, crop outside the frame). Post-render QA can quarantine a clip for a
repeatable encoded-frame defect or a high-confidence semantic mismatch; it records the exact
reason in `DisabledReason`, and the Clips page can reverse that decision after recuration.

---

## Cancelling a render

Any video with status `rendering` shows a **Cancel** button in the Queue. Cancelling removes
the Hangfire job, kills the live FFmpeg process by its stored PID, deletes partial output, and
marks the video `cancelled` with a Retry button. If the render finishes first, the API returns
409 rather than pretending it cancelled something.

---

## Scheduling

Times are entered as wall-clock in your chosen US timezone, converted to UTC before storage,
and converted back for display. Nothing does timezone math at job execution time — the
scheduler only ever compares UTC to UTC, which keeps DST from shifting your posting slots.

---

## What the reference reels actually do

Defaults come from a survey of **18 reels across all three reference pages**, with view
counts ranging from 260 to 1.2M:

| Property | Finding | Applied as |
|---|---|---|
| Subjects | two or more composited cats, labels beside each — true of every post above 500K views | `side_by_side` default layout |
| Duration | mean 13.3s; top performers 15–30s; the two shortest (4.6s, 4.9s) were the **worst** performers at 592 and 260 views | configured target, automatically extended when dialogue/visual holds need more reading time |
| Title | starts with `POV:` in nearly every high performer; the benchmark reel uses bold outlined text and hides it for the time card/payoff | profiled per-beat outlined title; legacy scripts retain the white pill |
| Hashtags | 2–5, mean 3.3 — the top page reuses one fixed set on every post | prompt asks for 3 |
| Background | ordinary rooms and outdoor scenes | Pexels portrait search |

An earlier pass on this project sampled only 2 reels and concluded the opposite on the first
three rows. Both of those reels turned out to be the bottom two performers in the wider set.
If you re-tune these defaults, sample broadly before trusting a pattern.

`single_pov` remains available for genuine one-character jokes, along with `top_bottom`,
`overlay_small` and `stacked_center`.

---

## Layout of the repo

```
Cat Memes Maker/
  dist/                 published app (what the shortcut runs)
  src/CatMeme.Api/      ASP.NET Core 9 API, jobs, FFmpeg services
  web/                  React 19 + Vite UI (builds into the API's wwwroot)
  pipeline/
    assets/cats/        green screen clips
    assets/backgrounds/ generated or downloaded plates
    output/             finished reels + thumbnails
    logs/               Serilog output
    topics/             generated topic JSON
  launch/               launcher + build scripts + icon
  .env                  paths, keys, connection string
```

---

## Troubleshooting

**App won't start** — the launcher prints the last 15 log lines. Full logs are in `pipeline\logs`.

**Database won't connect** — the app uses the full **SQL Server** instance (Windows service
`MSSQLSERVER`), with the database files at `D:\SqlData\CatMeme\`. Check that the service is
running; the launcher tries to start it and says so if it lacks the rights.

LocalDB was abandoned on purpose, and it is worth knowing why so nobody moves it back:

- **Its auto-start races.** Several clients hitting a stopped instance at once each spawn an
  engine. One grabs the data file; the one clients actually resolve to cannot open it, and
  reports `Failed to open the explicitly specified database`. EF then concludes the database
  does not exist, runs `CREATE DATABASE`, and fails with error 5170 "file already exists" —
  an error that describes nothing about the real fault.
- **`sqllocaldb` cannot be trusted in either direction.** It exits 0 when the engine failed
  to register, and reports "SQL Server process failed to start" when the engine started
  perfectly and logged *"ready for client connections"*.
- **Killing a stuck instance makes it worse.** The unclean shutdown forces crash recovery on
  the next start, which takes longer, which times out again. A launcher that "repairs" this
  by killing processes feeds the loop it is trying to break.
- **`AUTO_CLOSE` was on**, so the database released its files whenever it went idle and could
  then lose them to the wrong process. It is now off, and the connection is a plain service.

The launcher's only database step is now opening a real connection and retrying briefly
while a freshly started service finishes recovery. It never kills anything.

The old LocalDB copy of the database is still on disk at `C:\Users\M4tth3w\CatMemePipeline.mdf`,
detached and unused, as a fallback.

**Renders fail with "No cat clips in the library"** — import a compilation from the Clips page.

**Cat has holes punched through it** — that clip's detected key is too aggressive. Disable it on
the Clips page, or re-import. Keying uses `chromakey` (chroma-only) precisely so that grey and
white cats survive; `colorkey` would cut them to pieces.

**Green fringe around the cat** — lower that clip's similarity, or disable the clip.

**Cat sits on a coloured rectangle** — that clip's backdrop is not green. Run
**Clips → Reanalyze**; if it still slips through, lower `BackdropLimit` in
`ClipLibraryService`. The reanalyze response reports the P50/P75/P90 of both scores so you
can pick a limit from the data rather than guessing.

**A render looks soft or blurry** — a source clip was upscaled too far. Renders are capped at
2.2x upscale, and the verifier flags low sharpness as an amber warning on the queue card
rather than failing the video.

**A clip disabled itself with a "crop ... falls outside" or "file is missing" reason** — that
is preflight, and the reason is a fact about the file, not a quality call. Usually it means
clips were re-imported while old rows still pointed at the previous files. Run
**Clips → Import compilation** with reset to rebuild the library cleanly.

**"blocked before render - inputs still failed preflight"** — preflight could not assemble a
valid request in 4 attempts. The message names the rule that tripped; check `pipeline\logs`
for the per-attempt detail.
