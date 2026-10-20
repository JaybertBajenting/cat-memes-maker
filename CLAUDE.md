# CLAUDE.md — CatMeme Maker

Automated "POV cat meme" reel pipeline: an AI CLI writes the script as a storyboard of beats, green-screen
cat clips are cast to each beat, FFmpeg keys and composes a 9:16 reel, a visual QA gate checks the
result, and the Graph API publishes it to Facebook and Instagram Reels on a schedule.

One process serves the API and the React UI. See `README.md` for operator detail and
`catmeme-pipeline-spec.md` for the original spec.

## Stack

- `src/CatMeme.Api` — ASP.NET Core minimal API, .NET 9 (SDK pinned in `global.json`)
- EF Core on SQL Server LocalDB (`(localdb)\MSSQLLocalDB`, database `CatMemePipeline`), migrations in `Data/Migrations`
- Hangfire (same database) for jobs: a general queue, plus a single-worker queue for FFmpeg renders
  (`VideoRenderQueueFilter`) so encodes never compete for the CPU
- FluentValidation, Serilog, ImageSharp, RestSharp; FFmpeg, and the Claude / Codex CLIs as subprocesses
- `web/` — React + Vite + TypeScript, built into `src/CatMeme.Api/wwwroot`
- `src/CatMeme.Api.Tests` — xUnit

## Commands

```powershell
dotnet build CatMemePipeline.sln
dotnet test  CatMemePipeline.sln
dotnet run --project src/CatMeme.Api            # http://localhost:5099, dashboard at /hangfire
npm --prefix web install; npm --prefix web run dev
launch\build.ps1                                 # web build into wwwroot, then publish the API to dist/
dotnet ef migrations add <Name> --project src/CatMeme.Api
```

Configuration is `.env` (loaded at startup from the app folder, the working directory, or two levels up)
layered over `appsettings.json`. API keys and Page tokens belong in `.env` or the Settings page, never in
`appsettings.json`.

## Layout

```
src/CatMeme.Api/
  Program.cs                     composition root, Hangfire servers, recurring post scheduler
  Api/Endpoints.cs               all HTTP routes
  Domain/                        entities, storyboard quality contract, paired-interaction assets
  Jobs/PipelineJobs.cs           generate -> render -> verify -> publish job chain
  Services/ClipLibraryService    compilation import: scene split, letterbox crop, per-clip key colour, subject box
  Services/StoryboardCastabilityService   proves every beat can be cast before a script is saved
  Services/VisualBeatPlanner, RenderService, FFmpegService   storyboard -> filter graph -> mp4
  Services/VideoVerifier, VisualQualityReviewService         technical and visual QA gates
  Services/GraphApiService       Facebook / Instagram Reels publishing
web/src/                         pages, components, lib (API client)
launch/                          desktop launcher and build script
```

## How it works (invariants)

- Scripts follow the versioned `reference-default-v1` contract: 6-9 beats, setup -> escalation -> payoff.
  Beat controls (title style, lighting, scale, transition, sound) come from closed vocabularies so
  generated text can never inject a filter, path or command.
- Generation is asset-aware: a script that the current clip library cannot cast is rejected before it is
  saved. Newly imported clips are inert until they carry a castable action tag.
- Every render is sampled per beat into a contact sheet; structural checks run in code, and the selected AI
  CLI reviews the sheet. The semantic gate fails closed: no reviewer means no approval. Recastable
  failures quarantine the asset and retry (up to three attempts).
- Hand curation outranks the metrics: Reanalyze may only reverse its own verdicts, never a clip disabled
  by a person.
- Instagram needs `PublicMediaBaseUrl`: the Graph API fetches the video from a public URL.

## Conventions

- Explain *why* in comments and docs, with the numbers that justified the choice (the README is written
  this way; keep it so).
- New behaviour gets an xUnit test; render and concurrency rules have their own test classes.
- FFmpeg filter graphs are built from typed values, never from model output.
- Keep CPU-heavy work on the render queue.

## Never

- Commit `.env`, Page access tokens, Pexels or other API keys, or the clip library media.
- Re-enable clips that a person disabled from the contact sheet.
