# Milestone 4 completion checklist

This checklist breaks the Milestone 4 scope in `plan.md` ("Resilience and release readiness":
multi-library background work; Windows notification-area behavior; Android background transfers;
removable-storage loss and recovery staging; crash and session recovery; integrity checks;
accessibility, localization readiness, performance, diagnostics, packaging and the full automated
and manual test matrix) into independently completable units, following the same convention as
`milestone1.md`, `milestone2.md` and `milestone3.md`. An item is complete only when its stated
automated verification passes; platform-labelled items also require the applicable device check in
[manual_tests.md](manual_tests.md).

Milestones 1–3 remain partially open (see `milestone1.md`/`milestone2.md`/`milestone3.md`); per
`plan.md`, milestone boundaries are implementation/validation phases rather than strict release
gates, so Milestone 4 work can proceed in parallel. All milestones must be complete before the
first public release.

Milestone 4 does **not** claim the sidecar/export JSON system or the external-export cleanup
journal (`plan.md`'s Export section, lines ~600-767 — never claimed by any milestone's stated
scope), pre-generation cost estimation/confirmation thresholds (`plan.md` Cost and Usage,
lines ~1591-1646), the remaining Provider Safety Responses machinery or provider-file-transfer
disclosure UI, export-specific naming safeguards, generation-prompt library-browsing search
integration, the OpenAI/generic-adapter audio-video stubs, or Submission Outcome
Unknown/idempotency/Attempt Reconciliation — all of those are Milestone 3 checklist debt or
`milestone3.md`'s own "Possible future work," not resilience/release themes, and stay tracked
there.

## Multi-library background work

- [x] Keep each library with active work open and locked by the same SlopFactory process until its
      operations finish, even while the user switches the active library away from it
      (`plan.md:423-424`); explicitly submitted work continues after switching (`plan.md:426`'s
      first half — the "queued work requiring post-restart confirmation stays paused" half doesn't
      apply yet since nothing in the app today requires post-restart confirmation to resume, see
      **Crash and session recovery**'s resume-polling item). Implemented as a predicate hook rather
      than a direct dependency between the two services, to avoid a circular constructor reference:
      `AppLibraryState.RegisterKeepOpenPredicate` (`src/Mellow.SlopFactory.Gui/Services/AppLibraryState.cs`)
      lets `GenerationQueueService` register `HasActiveWorkFor(ILibraryWorkspace)` once at startup
      (`MainLayout.razor`'s `OnInitialized`, alongside `Queue.Start()`); `SwitchAsync`/`RelinkAsync`/
      `AdoptCopyAsync` consult it before disposing the outgoing workspace, moving it into a
      `_backgroundWorkspaces` dictionary (keyed by library ID) instead when it still has active work.
      `GenerationQueueService.OnLibraryChanged` was changed from "cancel/drop anything not
      `ReferenceEquals` the new active workspace" to "cancel/drop only a job whose workspace is
      genuinely no longer open" (`AppLibraryState.IsWorkspaceOpen`, true for the active workspace or
      any background-tracked one) — job execution itself (`ExecuteAsync`/`ExecuteVideoGenerationAsync`)
      needed no changes, since it already only ever touches its own captured `job.Workspace`
      reference. **Switching back to a backgrounded library reuses the existing instance** rather
      than attempting a second `OpenAsync`, which would otherwise fail against the OS-level exclusive
      lock (`FileShare.None`) this same process already holds — a real correctness issue found and
      fixed while implementing this, not merely a nice-to-have. `ReleaseBackgroundWorkspaceIfIdleAsync`
      releases a background workspace's lock once `GenerationQueueService` reports no more active
      work for it (called from `RunJobAsync` after each job completes) (`plan.md:430`), and
      `AppLibraryState.DisposeAsync` disposes every remaining background workspace on app shutdown.
      Verified by `GenerationQueueServiceTests.cs`:
      `RegisteringTheKeepOpenPredicateLetsALibrarySwitchAwayFromKeepActiveWorkRunning` (work survives
      the switch, background tracking appears and clears on completion) and
      `SwitchingBackToABackgroundLibraryReusesItsInstanceInsteadOfFailingToReacquireItsLock`
      (round-trip switch without a lock exception); the pre-existing
      `LibrarySwitchDropsQueuedAndCancelsRunningJobsTiedToTheOutgoingWorkspace` test (unchanged)
      continues to prove the original cancel/drop behavior still holds when nothing registers the
      predicate.
- [x] Add a global activity indicator that groups active work by library display name, covering
      every library with queued or running work rather than only the active one (`plan.md:425`).
      `AppLibraryState.BackgroundLibraries` exposes each tracked library's ID/display name/workspace;
      `GenerationQueueService.GetActiveJobCountForWorkspace` supplies its count.
      `MainLayout.razor`'s shell shows a notice per background library with a **Switch to this
      library** action (`AppLibraryState.SwitchAsync`, which reuses the tracked instance per the item
      above rather than reopening it).
- [x] Block **Forget Library** while that library has active work (`plan.md:428`).
      `AppLibraryState.HasActiveWorkFor(libraryId)` reports true for the active library (if the
      predicate says so) or any library present in the background set; `LibrarySettings.razor`'s
      recent-libraries **Forget** button is disabled and shows an explanatory notice for either case,
      not only for "this is the currently open library" as before.

  **Not done this pass**: routing notifications and the activity indicator's own entries to identify
  the owning library, so selecting one switches to that library and opens the relevant record
  (`plan.md:427`'s tap-through half — the activity indicator's own "Switch to this library" action
  above already covers switching, just not from a tapped OS notification). `INotificationService
  .Show`/`Tapped` and `GenerationNotificationCoordinator` carry only a generation-record ID today,
  with `MainLayout.OnNotificationTapped` navigating on the assumption the record belongs to whichever
  library is currently active — true before this milestone (only one library was ever open), no
  longer guaranteed now that a background library can also complete work. Extending this needs the
  library path threaded through `GenerationJobOutcome` → `NotifyRequested` → `INotificationService
  .Show`/`Tapped`, plus a `LibraryState.SwitchAsync` call before navigating on tap — a real, bounded
  change, just not made in this pass to keep this phase's diff reviewable; tracked here rather than
  silently dropped.

  Pausing local work safely for a library whose removable volume disappears mid-operation while
  keeping its remote asynchronous jobs tracked for later reconciliation (`plan.md:429`) is covered by
  **Removable-storage loss and recovery staging** below, not this section.

  **Deliberately deferred, previously noted**: `milestone2.md` explicitly scoped this out — "a real
  multi-library background-work model remains a separate, larger milestone" — so this section is
  new work, not a revision of anything already shipped.

## Work queue resilience

- [x] Add a **Dependency Recycled** pause state for a queued, not-yet-submitted job whose source
      file or destination folder is recycled — retaining its history and queue position and never
      proceeding with the recycled dependency automatically (`plan.md:413`); restoring the
      dependency makes the paused job eligible again at its existing position without disturbing
      active work (`plan.md:414`). Permanent deletion of that dependency makes the queued request
      non-runnable, requiring cancel-and-resubmit from the originating tab rather than in-place
      editing of the immutable queued snapshot (`plan.md:415`). Implemented entirely inside
      `GenerationQueueService` (`src/Mellow.SlopFactory.Gui/Services/GenerationQueueService.cs`):
      `GenerationJobPhase.DependencyRecycled` plus a per-job `RecycledDependencyIds` set and
      `NonRunnable` flag on the internal `QueuedJob`. `NotifyFileRecycled`/`NotifyFolderRecycled`
      add a dependency ID to that set and flip a still-`Queued` job to `DependencyRecycled`;
      `NotifyFileRestored`/`NotifyFolderRestored` remove it and only return the job to `Queued`
      once the set is empty and it isn't `NonRunnable` — so a job paused by two independently
      recycled dependencies needs both restored before it resumes.
      `NotifyFilePermanentlyDeleted`/`NotifyFolderPermanentlyDeleted` set `NonRunnable` permanently
      (no restore ever clears it). `Pump()`'s dequeue scan skips a `DependencyRecycled` node instead
      of stopping at the head of the queue, so one paused job never stalls unrelated jobs behind it
      on the same connection. Fixed a real bug found while wiring this in: `Cancel()` previously
      only recognized `Phase == Queued` for the "remove without touching a `CancellationTokenSource`"
      path, so cancelling a paused (never-started) job would have silently no-op'd — extended to
      `Queued or DependencyRecycled`. Wired from the UI: `Home.razor`'s single and bulk file/folder
      recycle actions call `NotifyFileRecycled`/`NotifyFolderRecycled` after a successful recycle
      (blocking first via `IsFileActivelyInUse`/`IsFolderActivelyInUse`, see below); `RecycleBin.razor`
      calls the `Restored`/`PermanentlyDeleted` variants after a successful restore or permanent
      deletion for `File`/`Folder` recycle-bin entries. `GenerationQueue.razor`/`Generate.razor` show
      a **Dependency Recycled**/**Dependency Permanently Deleted** notice for the affected run card.
      Verified by `GenerationQueueServiceTests.cs`: pause-then-restore-resumes, a later queued job
      running around a paused one, dual-dependency pause only clearing once both are restored,
      permanent-deletion marking `NonRunnable` and surviving an unrelated restore, and the
      `Cancel()` fix itself.
- [x] Add pinned-item deletion protection: pinned items cannot be permanently deleted
      (`plan.md:416`); recycling a connection, model or destination folder used by an active
      generation requires the user to cancel or wait for it (`plan.md:417`); a source file cannot
      be recycled while actively being read or uploaded, but can be recycled once its upload
      completes without cancelling the remote job (`plan.md:418`). `GenerationQueueService` exposes
      `IsFileActivelyInUse`/`IsFolderActivelyInUse` (true only for a `Running`-phase job — a merely
      `Queued` one never blocks, it pauses instead per the item above) and
      `IsConnectionActivelyInUse`/`IsModelActivelyInUse` (true for `Running` or `Monitoring`, so an
      async video job being polled still counts). `Home.razor`'s recycle actions and
      `RecycleBin.razor`'s permanent-deletion confirmation both check these first and skip/report
      blocked items rather than proceeding; `Connections.razor`/`Models.razor`'s recycle
      confirmations block entirely (matching "requires the user to cancel or wait", not a partial
      skip) when a connection/model is actively in use. Verified by
      `ActivelyInUseQueriesOnlyReportRunningJobsNotMerelyQueuedOnes`.
- [x] Add a cascade warning when recycling a connection or model that cancels dependent queued jobs
      which have not yet sent a request, listing them in the warning before the cascade proceeds
      (`plan.md:419`). `GenerationQueueService.GetQueuedJobTitlesForConnection`/`ForModel` surface
      the affected count for the warning; `CancelQueuedJobsForConnection`/`ForModel` cancel exactly
      those still-`Queued` jobs (never a `Running`/`Monitoring` one — recycling is blocked entirely
      while one exists, per the item above) once the user confirms. Wired into
      `Connections.razor`/`Models.razor`'s existing recycle-confirmation dialogs. Verified by
      `RecyclingAConnectionCascadeCancelsItsQueuedButNeverSubmittedJobs`.

- [ ] Add a recycle/permanent-deletion preview reporting affected *open generation tabs* — draft
      working copies, as opposed to already-queued jobs, which the **Dependency Recycled** item
      above fully covers (`plan.md:409-410`'s tab-count half) — and give each affected tab a stable
      "unavailable reference" with restore/replace actions, converting to a non-restorable missing
      dependency on permanent deletion (`plan.md:411-412`), and have recovered drafts/saved settings
      keep a recycled dependency as an unavailable reference rather than silently dropping it
      (`plan.md:420`). **Not done this pass**: this needs a real redesign of how `Generate.razor`'s
      drafts represent a source-file/destination-folder reference that no longer resolves — today a
      missing reference is silently cleared back to blank the next time the tab loads
      (`Generate.razor`'s existing `_activeImageFiles.Any(...) ? ... : string.Empty` pattern,
      predating this milestone), which is the opposite of "stable unavailable reference." That is a
      larger tab-state-model change, not a bounded follow-on to the queue-side work above, so it
      stays open rather than being force-fit into this pass.

  **Scope note**: `plan.md`'s Milestone 4 one-line summary names only "multi-library background
  work," not this section by name — these bullets share the same Work Queues area of `plan.md`
  (lines 405-422) but were never claimed by Milestone 2 or 3 and are squarely about queue
  robustness against destructive library changes, which is why they're grouped under this
  milestone's resilience theme rather than left as unscoped debt.

## Windows notification-area and background-work behavior

- [x] Add the **Keep Running** / **Cancel Work and Exit** / **Return to App** dialog shown when
      closing the main window while local work is active (`plan.md:440`).
      **Correction to this item's original planning-time note**: it previously claimed a
      no-active-work exit path and a draft-flush **Retry Save**/**Exit and Lose Unsaved Edits**/
      **Return to App** gate were "already shipped" (`plan.md:434-439`) via `FlushForSuspensionAsync`.
      Checking directly before implementing this phase found that's not accurate: `FlushForSuspensionAsync`
      is wired to MAUI's cross-platform `Window.Destroying`/`Window.Stopped` events, which are plain
      notifications — every existing handler discards their event args (`(_, _) => ...`) and nothing
      anywhere sets a `Cancel` flag, because there is none to set. That means today closing the
      window *always* proceeds regardless of unsaved drafts; the flush is a best-effort background
      attempt, not a real blocking gate, and no **Retry Save** dialog exists anywhere in the code.
      This phase's own dialog is therefore the first *real* close-blocking gate in the app, built on
      the native WinUI `AppWindow.Closing` event (which genuinely supports `args.Cancel`, unlike
      MAUI's abstraction) rather than the non-blocking event used elsewhere — `Platforms/Windows/App.xaml.cs`'s
      `HookWindowClosing` subscribes to it once the main window exists, checks
      `GenerationQueueService.RunningCount`/`QueuedCount`, and only cancels the close (then requests
      the dialog via the new `IWindowsExitCoordinator`) when work is actually active; with none, the
      close proceeds untouched, matching `plan.md:434`. The draft-flush gate itself remains exactly
      as before (unchanged, still non-blocking) — building a real one is a separate, pre-existing
      gap this milestone doesn't claim.
- [x] **Keep Running** places SlopFactory in the Windows notification area, preserves active work,
      is not itself an exit, and keeps failed draft edits in memory with retry available once the
      window reopens (`plan.md:441-442`). `IWindowsExitCoordinator.KeepRunning` raises `KeptRunning`,
      which the native handler uses to hide the window (`AppWindow.Hide()`) — the process, its
      libraries and in-memory state are completely untouched, so failed draft edits remain exactly
      as available as they always were once the window is shown again.
- [ ] If **Cancel Work and Exit** would also lose unsaved draft edits, run the existing draft-exit
      gate to completion before cancellation or process termination begins (`plan.md:443`).
      **Not done**: since the draft-exit gate itself isn't a real blocking mechanism today (see the
      correction above — there is nothing to "run to completion" that would actually stop
      termination), this bullet has no real gate to sequence with yet. `CancelWorkAndExit` does
      still call `FlushForSuspensionAsync` indirectly (via the same `Window.Destroying` handlers
      firing during the real close that follows `Environment.Exit(0)`... actually `Environment.Exit`
      terminates immediately without running window-lifecycle events at all, so today's flush does
      **not** get a chance to run on this path). This is a real, narrow gap worth flagging
      precisely: `CancelWorkAndExit`'s cancellation of queue jobs is unconditional and immediate,
      with no draft-save attempt first. Left open rather than silently claimed as handled.
- [x] Add a notification-area icon showing aggregate status with reopen/exit actions
      (`plan.md:444`). `ITrayIconService`/`WindowsTrayIconService` implements this directly against
      the classic Win32 `Shell_NotifyIcon` API (a dedicated invisible native window receives the
      icon's callback message and a right-click context menu's **Open SlopFactory**/**Exit**
      commands) rather than adding a third-party NuGet package — there is no notify-icon control in
      the stable Windows App SDK, and this project prefers not to add a new dependency for something
      a small amount of interop already covers. `MainLayout.razor` calls `TrayIcon.Show(...)` with a
      running/queued-count tooltip when the user chooses **Keep Running**; the tray's **Open**
      action restores the window, **Exit** routes through the same `CancelWorkAndExit` path as the
      in-app button. A `NullTrayIconService` is registered on Android (no tray-icon concept there;
      background-work status there is a persistent notification instead, in the next section).
      Provider cancellation on exit and asynchronous-job persistence for next-launch reconciliation
      (`plan.md:445-446`) were already true before this phase — `GenerationQueueService.Cancel`
      already attempts the normal provider-cancellation path for a running job, and async remote
      jobs already persist to the per-library registry regardless of how the app exits.
- [x] Add a rememberable **Keep Running** choice, changeable later in settings (`plan.md:447`).
      `IWindowsExitCoordinator.RememberedKeepRunning`/`SetRememberedKeepRunning` persist it via the
      existing `IAppPreferenceStore`; the native close handler checks it before ever requesting the
      dialog, and `LibrarySettings.razor` (Windows only) exposes a checkbox to change it later.
      Never hiding in the notification area without first explaining that SlopFactory remains active
      (`plan.md:448`) is satisfied by construction: the window is only ever hidden as a direct
      response to the user explicitly clicking **Keep Running** in the dialog above (or having
      previously chosen to remember that decision) — there is no path that hides the window silently
      without that explanation having been shown at least once.

  **Not independently verified beyond compiling**: like the single-instance work in Crash and
  Session Recovery, this is real WinUI/Win32 interop with no automated test harness for actual
  window-close/tray-icon/context-menu behavior — `WindowsExitCoordinator`'s decision logic itself
  (remembered-choice handling, cancel-and-raise-exit, return-to-app) is unit-tested, but the native
  glue in `Platforms/Windows/App.xaml.cs`/`WindowsTrayIconService.cs` needs a manual check on a real
  Windows install (close with active work showing the dialog, Keep Running hiding to tray, tray
  Open/Exit, and the remembered-choice setting actually skipping the dialog next time).

## Android background transfers

- [ ] Use Android's user-initiated data-transfer mechanism for uploads and result downloads where
      available, with an appropriate backward-compatible scheduled-work fallback (`plan.md:265`).
- [ ] Show the required ongoing notification with progress and a cancel action for active
      background transfers (`plan.md:266`); request the notification permission only when
      background transfer behavior is first actually needed, with an explanation (`plan.md:267`).
- [ ] Warn that leaving SlopFactory may interrupt the operation when permission or platform
      restrictions prevent reliable background execution (`plan.md:268`); reserve background
      execution for active transfers rather than indefinite provider-status polling
      (`plan.md:269`).
- [ ] Persist asynchronous provider job IDs and resume polling through scheduled work or when the
      application becomes active (`plan.md:270`) — this is the Android-background-execution half of
      the same resume-polling-on-reopen requirement tracked under **Crash and session recovery**
      below; a generation is never started automatically during device boot (`plan.md:271`).
- [ ] Record Android execution suspension and timeout separately from provider failure
      (`plan.md:272`), so a suspended transfer isn't misreported as the provider having failed it.

## Removable-storage loss and recovery staging

- [x] Add a minimal device-wide registry of staged results — library ID, a cached display name (so
      the UI can show something meaningful even while that library is unavailable), draft ID, safe
      filename, media type, byte size and creation time (`plan.md:322`, extended with the fields
      `plan.md:329` requires the staging list to show — never a prompt, model settings or source
      content). Implemented as `IPendingResultRegistryService`/`PendingResultRegistryService`
      (`src/Mellow.SlopFactory.Gui/Services/IPendingResultRegistryService.cs` +
      `PendingResultRegistryService.cs`), mirroring `IRecentLibraryService`'s existing
      Preferences-backed JSON-list pattern — the closest existing precedent for a small device-wide
      record list, read-modify-write under one in-process lock. **Scope note**: `plan.md:322`
      describes this as a registry of every outstanding async job (so a minimal cross-check survives
      even if a library's own per-library `async_remote_jobs` table becomes unreachable); this pass
      only creates an entry reactively, at the exact moment a result would otherwise be lost (see
      below), rather than proactively mirroring every submitted async job device-wide from the
      moment of submission — a full parallel mirror of every in-flight job is a larger, separate
      piece of bookkeeping not attempted here.
- [x] When a video result finishes at the provider but its destination volume is disconnected,
      stage the already-downloaded bytes into device-wide app-specific recovery storage instead of
      discarding them (`plan.md:323`). `IRecoveryStagingPathProvider`/`MauiRecoveryStagingPathProvider`
      supplies the physical folder (`FileSystem.Current.AppDataDirectory/recovery-staging`);
      `IRecoveryStagingService`/`RecoveryStagingService` combines it with the registry above.
      `GenerationQueueService.ExecuteVideoGenerationAsync`'s final commit is now wrapped: on catching
      an `IOException`/`Microsoft.Data.Sqlite.SqliteException` with at least one downloaded file in
      hand, it checks `ILibraryAvailabilityProbe.IsAvailable` for the destination library's root —
      only if that reports unavailable does it stage the files (an ordinary validation/provider
      failure, or a storage error while the library is genuinely still available, is never
      staged — it falls through to the existing `LocalFailureOutcome` path unchanged).
      `GenerationJobOutcome` gained a `StagedForRecovery` flag so `Generate.razor`'s run cards show a
      distinct notice with a link to the new `/recovery-staging` page instead of an ordinary failure
      message. **Real bug found and fixed while wiring this in**: `ExecuteAsync`'s catch clause only
      listed `IOException`/`UnauthorizedAccessException`/`SlopFactoryException`/`ObjectDisposedException`
      — a genuine storage failure during the final commit's SQLite write throws
      `Microsoft.Data.Sqlite.SqliteException` directly (the mutation wrapper does no exception
      translation), which escaped as an unobserved task exception from `RunJobAsync`'s fire-and-forget
      call site, silently losing the outcome entirely with no notification at all. Now caught
      uniformly, matching the existing `PermanentlyDeleteFileCoreAsync`/`PermanentlyDeleteFolderCoreAsync`
      precedent for translating that same exception type. **Scope note**: only the video path is
      wired up — Image/Audio/Text's synchronous commits could hit the same failure mode, but video is
      where an unavailable-mid-flight library is most realistic (it is the one asynchronous,
      long-running mode), and the staging helper (`IRecoveryStagingService.StageAsync`) is written
      generically enough to extend to the other modes later without rework.
- [x] Add **Export Copy** (`plan.md:331`), writing staged bytes to a user-selected external
      destination without creating a library record or changing the intended library; exporting
      never marks a staged result as reconciled or deletes it (`plan.md:332`), and a failed or
      cancelled export leaves the staged copy unchanged (`plan.md:333`, since
      `IRecoveryStagingService.ReadBytesAsync` never mutates the registry — only
      `DiscardAsync`/`Remove` do). `IPlatformFileActionService` gained `ExportRawBytesAsync` (same
      Windows `FileSavePicker`/Android `CreateDocumentAsync` idiom as the existing `ExportAsync`,
      but for bytes that don't belong to any `FileRecord`/`ILibraryWorkspace`, which the staged
      bytes never do until reconciled). Surfaced on a new **Recovery staging** page
      (`RecoveryStaging.razor`, `/recovery-staging`, linked from the main nav and from a staged run
      card) grouped by library display name, with **Discard** as the explicit-confirmation-gated
      deletion action (`plan.md:328`, `334`).
- [x] Block ordinary **Forget Library** while completed provider results remain staged for that
      library (`plan.md:463`), or while its unreconciled dirty-draft markers exist
      (`plan.md:459` — see the correction below), offering a combined **Delete Recovery Data and
      Forget** action (`LibrarySettings.razor`) that discards every staged result for that library
      and clears its dirty-draft markers before completing the normal forget workflow
      (`plan.md:464-465`, `461`). Staged results and dirty-draft markers are never removed merely
      because the library is forgotten or unavailable on their own (`plan.md:466`) — only this
      explicit action removes them.

  **Correction to milestone4.md's original planning note**: this section originally claimed
  `milestone2.md` already shipped "the parallel `Forget Library` block/`Delete Recovery Drafts and
  Forget` path for unreconciled draft snapshots." That was wrong — checked directly against
  `LibrarySettings.razor` and `AppLibraryState.cs` before starting this phase: Milestone 2 shipped
  only the dirty-draft *marker* (`AppLibraryState.DirtyDraftIds`, IDs only, no content) and a bare
  **Dismiss** button in `MainLayout.razor`; there was no Forget-Library gating tied to it at all
  until this phase added `AppLibraryState.GetDirtyDraftCount`/`DeleteDirtyDraftsFor` and wired them
  into the same **Delete Recovery Data and Forget** action built for staged results above. The
  planning-time description was describing `plan.md`'s spec, not shipped code — corrected here per
  this project's standing practice of fixing a prior inaccuracy as soon as it's found, rather than
  carrying it forward.

  **Not done this pass**: automatic reconciliation — "when the intended library returns, move the
  staged result into it atomically and delete the staged copy" (`plan.md:325`). The device-wide
  registry deliberately excludes the prompt and model settings needed to recreate a normal
  generation-history record (matching `plan.md:322`'s privacy constraint), and
  `RecordMediaGenerationResultAsync` requires exactly that context to commit one — so an automatic,
  provenance-preserving reconcile-on-return needs new design (what a staged result becomes once
  imported without a prompt to attach it to) rather than being a bounded extension of what shipped
  here. **Export Copy** and **Discard** are real, working resolutions in the meantime; a dedicated
  reconcile action is left open below rather than force-fit into this pass.
  Retry-while-still-available (`plan.md:326`) and the "notify the user a result is awaiting its
  library" surfacing (`plan.md:327`) beyond the recovery-staging page's own list are the same kind
  of follow-on, left open together.

- [ ] Add automatic reconciliation once the intended library becomes available again — move the
      staged result into it and delete the staged copy (`plan.md:325`), retry the staged download
      while internal storage was insufficient and the provider result remains available
      (`plan.md:326`), and notify the user proactively that a result is awaiting its library
      (`plan.md:327`) rather than only showing it on the Recovery staging page when visited. See the
      "Not done this pass" note above for why this needs new design (a provenance-preserving import
      path that doesn't depend on the prompt/settings context the device-wide registry deliberately
      never retains) rather than being an extension of what shipped in this phase.

## Crash and session recovery

- [x] Enforce one running SlopFactory process per signed-in Windows user session (`plan.md:352`);
      launching SlopFactory again activates the existing process instead of starting another
      (`plan.md:355`). Implemented in `Platforms/Windows/App.xaml.cs` using
      `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey` checked synchronously in the
      `App` constructor, before `InitializeComponent()` — a second launch redirects its activation to
      the existing instance and calls `Environment.Exit(0)` immediately, so no window is ever created
      for it. Doing this check synchronously (blocking on `RedirectActivationToAsync(...).AsTask()
      .GetAwaiter().GetResult()`) rather than fire-and-forget matters: `OnLaunched` would otherwise be
      free to run against a half-constructed app before the async redirect/exit completed.
      **Not independently verified beyond compiling** — this is real WinUI platform code with no
      automated test harness for actual process launch/redirect behavior; needs a manual check on a
      real Windows install (launch, launch again, confirm only one window and one process).
- [x] Forward a second-launch request to open a library or import files to the existing process,
      requiring explicit user confirmation before it changes the active library or imports anything
      (`plan.md:356`); forwarded requests never switch libraries, import files or submit work
      automatically (`plan.md:357`). No new code needed here: `RedirectActivationToAsync` delivers
      the second launch's activation args to the existing process's own `Activated` handler, which
      already only handles file activation by queuing paths into `IncomingImportService` — a
      pre-existing flow whose confirmation step (the user must explicitly accept queued incoming
      files before anything imports) already satisfies this requirement. There is no
      "open a specific library" launch argument anywhere in this app to forward, so nothing exists to
      build for that half.
- [x] Keep per-library exclusive locking enforced on both platforms as protection against other
      processes and unexpected re-entry (`plan.md:360`), consistent with the single Windows process
      being able to hold multiple libraries open at once for explicitly submitted background work
      (`plan.md:358`) — already true since `LibraryWorkspaceFactory.AcquireLock`'s `FileShare.None`
      lock file predates this milestone and Milestone 4's own multi-library background-work section
      builds on it directly; no change needed here.
- [x] Create a sanitized local diagnostic record on crash (`plan.md:184`); on next launch, notify
      the user that the application did not close normally and offer to view, clear or export the
      crash diagnostics (`plan.md:185`), with stack traces and request context following the
      existing diagnostic redaction rules (`plan.md:186`, `plan.md:171-176` — no stack traces are
      actually captured, since nothing in this app catches unhandled exceptions to record one; see
      the scope note below). `IDiagnosticsLogger` gained `MarkSessionStarted`/`MarkSessionEndedNormally`/
      `DidNotCloseNormallyLastSession`, backed by a simple marker file written at startup and deleted
      on a graceful exit — if the marker from a previous run is still present at the next
      `MarkSessionStarted()` call, that run never reached a graceful exit, so a crash entry
      (`DiagnosticLogEntry.IsCrash`) is logged and the flag is set. Wired to `App.xaml.cs`'s
      constructor (session start) and its `Window.Destroying` handler (the same signal
      `FlushForSuspensionAsync` already treats as "the app is genuinely closing," reused here for
      consistency) for session end. `MainLayout.razor` shows a dismissible notice with **View
      diagnostics**/**Clear diagnostics** actions when the flag is set on startup. **Scope note**:
      this detects *that* the process didn't exit cleanly (crash, kill, power loss), which is what
      `plan.md:184`'s "diagnostic record" and "did not close normally" prompt actually need — it does
      not capture the crash's stack trace itself, since nothing in the app subscribes to
      `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException` to record one before the
      process dies; adding that is a natural, bounded follow-on once a real crash needs deeper
      diagnosis, not attempted here.
- [x] Resume polling of every incomplete asynchronous provider job when the application reopens,
      keyed off the already-persisted job IDs (`plan.md:1433`), scoped to the one case resumable
      without inventing missing context: a job the provider already confirmed
      `AsyncRemoteJobPhase.CompletedAwaitingDownload` (has an existing generation-record
      position to commit into) is automatically retried via the same
      `RetryMissingResultDownloadAsync` **Refresh Provider Status** already uses, called once when
      `GenerationQueueService.Start()` runs and again on every subsequent library switch/reopen
      (`OnLibraryChanged`). **Not resumable, and deliberately not attempted**: a job still genuinely
      `Submitted`/`Processing`/`MonitoringPaused` when the app closed has no persisted prompt, model
      or settings context to resume a poll loop into — only `DraftId`, connection ID, provider job ID
      and monitoring deadline are ever persisted per async job (by design, matching the device-wide
      registry's own privacy constraint elsewhere in this milestone). Building a full resume for that
      case is the same underlying gap as Milestone 3's already-tracked **Submission Outcome
      Unknown**/**Attempt Reconciliation** debt — a genuine architecture change (capturing a durable
      pre-submission snapshot), not a bounded extension of this phase. Those jobs remain visible and
      discardable via `Connections.razor`'s existing "unresolved async jobs" notice, unchanged.

## Integrity checks

- [x] Add an explicit, user-triggered integrity-investigation action that performs a real
      byte-for-byte re-comparison for diagnosing suspected storage or implementation faults,
      distinct from and in addition to the existing single-pass hash check routine duplicate/
      classification workflows already rely on (`plan.md:583`, `plan.md:580-582`).
      **Correction to this item's original scope**: this was already fully built before this
      milestone — `LibraryWorkspace.RunIntegrityScanAsync` (backing `LibrarySettings.razor`'s
      existing "Library Integrity" panel, present since Milestone 1) already performs a real,
      explicit, user-triggered, resumable/cancellable SHA-256 re-hash of every active managed file's
      on-disk bytes against its stored `ContentHash` — exactly the "explicit integrity
      investigation" `plan.md:583` describes, and structurally separate from the import-time
      duplicate-detection comparison (which only compares an incoming candidate's hash against an
      already-stored digest, never re-reading the existing file's bytes). Checking directly against
      the codebase before starting this phase (rather than assuming milestone4.md's original,
      planning-time framing was accurate) found no gap here to fill.
- [x] Add managed-file existence verification before export and before provider submission
      (`plan.md:547`), keeping explicit existence, containment and hash checks in those workflows
      regardless of file-watcher state (`plan.md:548`); make export and provider upload
      unavailable while managed content is missing (`plan.md:557`), and never claim to recover
      missing bytes when no backup exists (`plan.md:558`). **Also already substantially in place**:
      `LibraryWorkspace.ExportCoreAsync`'s `ValidateRegularManagedFile` checks existence/
      directory-substitution/reparse-point/hard-link regardless of cached `ContentState`, and the
      export never completes unless the freshly-streamed bytes' hash matches the stored digest
      (so a missing-content export already fails, satisfying `plan.md:557-558`); Text mode's source
      image submission already goes through `GetVerifiedContentFileAsync` (full live existence+hash
      re-check on every call, `ReadImageFileAsync`'s underlying helper), so provider submission was
      already covered too. No other adapter currently uploads local managed-file bytes to a provider
      (audio/video accept no source input, per Milestone 3), so there is nothing further to verify
      there yet.
- [x] Add export outgoing-stream-mismatch handling: an outgoing-stream mismatch detected before
      commit aborts the export, cleans up the temporary output, writes no sidecar, marks the
      library record for integrity review, and reports that export did not complete
      (`plan.md:649`) — without marking the library record corrupt or changed merely because a
      destination read-back mismatch occurred after the outgoing stream already matched the stored
      digest and size (`plan.md:651`); if the mismatched object replaced or cannot be removed at the
      destination, report it as potentially corrupt without claiming the prior external object was
      restored (`plan.md:652`). The pre-commit outgoing-stream check already existed
      (`Hashing.CopyAndHashAsync` + a hash comparison before `File.Move`, aborting and cleaning up the
      temp file on mismatch); genuinely new this pass: `ExportCoreAsync` now re-hashes the actual
      destination file immediately after `File.Move` and compares it to the already-verified outgoing
      hash — a new `FileExportOutcome.VerificationFailed` value distinguishes this from an ordinary
      pre-commit `Failed`, since the destination path may already have replaced something. On
      mismatch it attempts to delete the bad destination file, reporting whether that succeeded
      (matching `plan.md:652`'s "cannot be removed... reports it as potentially corrupt" case) without
      ever touching the *source* library record's `ContentState` (nothing in the export path does,
      by construction, so "never marks the record corrupt" is unconditionally true). `FileDetails.razor`
      recommends an integrity scan (`IntegrityScanRecommendationService`) on this outcome instead of
      silently discarding it. **Not independently unit-tested**: triggering a genuine destination
      read-back mismatch requires the destination file to diverge between an atomic `File.Move` and
      an immediate re-read on the same volume, which isn't practically reproducible in a controlled
      test without making the hashing step itself fault-injectable — left as a code-reviewed, not
      automated-test-covered, path.
- [x] Block content-replacement commit while the file is actively pinned by a queued/running
      generation using it as a source input (`plan.md:556`) — reuses
      `GenerationQueueService.IsFileActivelyInUse` from this milestone's Work Queue Resilience phase;
      `FileDetails.razor`'s `CommitReplacementAsync` now checks it before calling
      `CommitManagedContentReplacementAsync`, matching the same GUI-layer enforcement pattern already
      used for recycling (Infrastructure/`LibraryWorkspace` has no reference to the queue service, by
      design — the same layering already established in Work Queue Resilience). **Not
      unit-tested**: like the rest of this project's Razor-page code-behind, this guard lives in
      markup/code-behind, which this codebase deliberately does not cover with automated tests.

- [ ] Add **Reacquire Permanently Deleted Output**: when an output file was permanently deleted but
      its history tombstone still identifies a remotely available provider result, let the user
      explicitly reacquire it (`plan.md:1418`) — confirmation-gated, downloaded and validated
      through the normal safety pipeline, creating a new file identity while preserving the former
      file's tombstone and recording that the result was reacquired rather than restored
      (`plan.md:1419`). Compare the downloaded content hash against the permanent-deletion
      tombstone's stored hash (`plan.md:1420`); a mismatch preserves the tombstone and requires a
      clear warning before the new bytes may be committed as a separate **Provider Output Changed**
      result — never described as recovery of the permanently deleted file (`plan.md:1421`).
      **Not done this pass**: `GenerationRecord`'s tombstone (`FileIdentitySnapshot` — display name,
      media type, content hash only) is exactly what's needed to *compare against* once new bytes
      are in hand, but there is nothing to *download from* — the per-library `async_remote_jobs`
      registry row for a job is deleted once its generation commits successfully (by design, so it
      doesn't linger as a stale "unresolved" row forever), and `GenerationRecord` itself never
      retains the provider job ID or a fresh result URL permanently. A "reacquire" action needs a
      real answer to "reacquire from where" that this data model doesn't currently provide — adding
      that means deciding whether to retain a provider job ID indefinitely post-commit (with its own
      privacy/staleness tradeoffs) purely to support a rarely-used recovery path, which is a real
      design decision, not a bounded implementation task. Left open rather than built on a guess.
- [ ] Add the provider-safety-classification/content-replacement integrity rules for a **Missing**
      or **Content Changed** record: a classification received in that state attaches only to the
      immutable provenance of the record's original bytes (`plan.md:549`); restoring
      algorithm/digest/size-matching bytes reactivates the classification and concealment on the
      current file, while differing or externally changed bytes never inherit it (`plan.md:550`).
      **Not done — blocked on the same prerequisite `milestone2.md` already documented as missing**:
      `milestone2.md` explicitly scoped the entire provider-safety-classification/concealment storage
      mechanism (a persistent per-file classification value, concealment state, reveal sessions) out
      of Milestone 2 as "confirmed not buildable honestly right now," since no supported adapter
      exposes a signal to drive it. `plan.md:549-550` describes reactivating *that* classification on
      a matching-hash restore — with no classification value ever persisted anywhere in this
      codebase, there is nothing to reactivate. This stays blocked on the same adapter-signal gap
      `milestone3.md`'s own "Possible future work" section already tracks (Provider Safety
      Responses), not new debt introduced here.
- [ ] The content-replacement mechanics this needs already exist and are unaffected by the two items
      above: a content-replaced file keeps its original provenance for historical context but
      clearly states its current bytes aren't the original content (`plan.md:551`), and generation
      history keeps the original result hash, media type and byte size, immutable after replacement
      (`plan.md:552`) — both already shipped via `LibraryWorkspace.CommitManagedContentReplacementAsync`/
      `AcceptFileContentAsync`'s existing `RestoresOriginal`/provenance-retention logic, predating
      this milestone. **Still open**: after a replacement, revalidating every open generation-tab
      draft and saved generation setting referencing the file against its new media properties
      (`plan.md:553`) — an incompatible reference showing **Needs Review** and blocked from
      submission until replaced/restored/removed (`plan.md:554`), a compatible reference instead
      showing **Content Replaced** (`plan.md:555`). Not attempted this pass: doing this properly
      needs a real revalidation pass over `Generate.razor`'s draft-loading code (which today silently
      clears an unresolvable source-file reference back to blank rather than flagging it, the same
      pre-existing gap noted in the Work Queue Resilience section above) and over saved generation
      settings, plus a genuine "Needs Review" state distinct from a silently-cleared reference — a
      larger, dedicated UI/data-model change bundled with (not separable from) the tab-reference
      redesign already left open in Work Queue Resilience, rather than a bounded addition to this
      phase.

## Accessibility

- [x] Add Windows Narrator and Android TalkBack support (`plan.md:157`), building on the
      already-shipped Milestone 1 slice (focus restoration and dialog roles, `ui.js`'s
      `role="dialog"`-driven focus management) rather than duplicating it. Every interactive
      control across the app already uses real `<button>`/`<a>`/form elements (confirmed by an
      audit before starting this phase — no `<div onclick>` patterns exist anywhere), which already
      gives Narrator/TalkBack their accessible name/role/state for free; this phase's job was
      finding and closing the *specific* gaps in what's announced and how contrast/sizing hold up,
      covered by the items below rather than a separate "add support" task.
- [x] Ensure status is never communicated by colour alone anywhere in the interface
      (`plan.md:158`) — an audit confirmed every status surface already pairs colour with a text
      label (connection/model status, run-card headings, recycle-bin state, content-state badges);
      no colour-only signal was found anywhere, so no change was needed here.
      Meet WCAG 2.2 AA contrast targets throughout (`plan.md:159`): computing actual contrast
      ratios from `app.css`'s literal colour values found two real failures — `.muted`/`.empty`/
      `small` (the shared dark-theme colour `#9ea5b3`) was only 2.3-2.5:1 against the Light theme's
      backgrounds (used extremely widely: empty states, file locations, help text, RecycleBin state
      summaries), and `.danger` button text was 4.39:1 against its `#c95151` background, just under
      the 4.5:1 normal-text threshold. Fixed with a `.theme-light`/`.theme-system` override
      (`color: #475569`, ~7:1) and a darkened `.danger` background (`#b84545`) — darkening a
      background against white text only ever increases contrast, so this couldn't regress the
      already-passing dark theme. Locked in by
      `UiAssetTests.AccessibilityStylesCoverReducedMotionContrastAndUniversalTouchTargets`.
- [x] Respect system text scaling, high-contrast mode, reduced-motion settings and light/dark theme
      preferences (`plan.md:160`); ensure thumbnails and media controls provide text alternatives
      (`plan.md:161` — this milestone4.md item's own bullet numbering combines what `plan.md` lists
      as two separate lines; see the dedicated bullet below for the announcement half).
      `prefers-reduced-motion` and light/dark theme preferences were already fully handled before
      this phase; `forced-colors: active` existed but only affected focus-outline colour, so a new
      `@media (prefers-contrast: more)` rule was added (widening borders/outline thickness on every
      themed surface). Thumbnail/media alt text was already correct where it carries real
      information (`FileDetails.razor`'s image preview has a localized `alt`; native `<video>`/
      `<audio controls>` get their transport-control accessible names from the platform for free);
      `Home.razor`'s browser thumbnails use `alt=""` deliberately, since the filename is already
      exposed as adjacent text inside the same accessible button. **Not done**: system text-scale
      following (e.g. Windows "Make text bigger") has no runtime detection/response anywhere in the
      app beyond `rem`-based CSS (which lets ordinary browser/WebView zoom work, but doesn't
      actively read and apply the OS-level accessibility text-scale setting) — building that needs
      native interop (`Windows.UI.ViewManagement.UISettings.TextScaleFactor` /
      Android `Configuration.fontScale`) bridged into a CSS variable, a real but separate platform
      feature not attempted this pass.
- [x] Announce generation progress, completion, failures and validation errors accessibly without
      repeatedly interrupting the user (`plan.md:162`). Two real gaps found and closed:
      (1) `Generate.razor`'s active run-card phase text and its completed/failed outcome section had
      **no live region at all** — Narrator/TalkBack would only ever hear a job's queue
      position/running/completion state if the user happened to have focus there; now both carry
      `role="status"`, as does `GenerationQueue.razor`'s per-job phase text. (2) the opposite
      problem existed in `Home.razor`'s import progress and `LibrarySettings.razor`'s integrity-scan
      progress: both were `role="status" aria-live="polite"` regions whose text changes on **every
      single processed item** — a 500-file import would re-announce 500 times back-to-back, the
      literal "repeatedly interrupting" scenario this requirement warns against. Fixed by making
      both `aria-live="off"` (still visible, just not proactively announced) — a one-shot completion
      message (already `role="status"`, existing since earlier milestones) already announces once
      the whole operation finishes. Locked in by
      `UiAssetTests.GenerationProgressAndCompletionAreAnnouncedAccessibly` and
      `UiAssetTests.PerItemImportAndScanProgressDoNotRepeatedlyInterruptScreenReaderUsers`.
      **Not done**: validation-error announcements aren't programmatically associated with their
      input via `aria-invalid`/`aria-describedby` anywhere — no `<EditForm>`/`DataAnnotationsValidator`
      is used anywhere in this codebase (validation is manual `.notice.error`/`role="alert"` text
      blocks, confirmed by a full-repo grep finding zero existing `aria-invalid`/`aria-describedby`
      usage). Retrofitting that association is a real, mechanical, but wide change spanning every
      form page (`ConnectionEdit.razor`, `ModelEdit.razor`, `LibrarySettings.razor`, etc.) —
      substantial enough that doing it for one page and not the others would leave an inconsistent
      pattern; left as a dedicated follow-on rather than a partial pass here.
- [x] Size touch targets appropriately and ensure no action depends on hover input
      (`plan.md:163`). WCAG 2.2 SC 2.5.8's 24×24 CSS px minimum applies regardless of pointer type,
      but the existing `min-height: 44px` rule only fired under `@media (pointer: coarse)` — small
      icon-only controls (`.tab-move`/`.tab-close`, the tab reorder/close buttons in `Generate.razor`/
      `GenerationQueue.razor`) had no guaranteed minimum on a mouse/trackpad/fine-pointer
      touchscreen. Added an unconditional `.tab-move, .tab-close { min-height: 24px; min-width:
      24px; }` rule alongside the existing coarse-pointer 44px enhancement. No hover-only action was
      found anywhere in the codebase (every action already has a click/tap/keyboard-activation
      handler; hover only ever triggers a CSS-only visual `:hover` filter, never reveals or gates a
      control), so no change was needed there.

## Localization readiness

- [x] Use the device locale for dates, times, numbers, byte sizes and currencies throughout the
      application (`plan.md:146`), beyond Milestone 1's no-hard-coded-strings resource-coverage
      guard. An audit found this was **already true almost everywhere by default**: every
      date/time display (`.ToLocalTime().ToString("g"/"t")` etc.) and every byte-size helper
      (`FormatBytes`'s `$"{bytes:0.##} MB"`-style interpolation) never overrides culture, so they
      already format via `CultureInfo.CurrentCulture` — and this app has no
      `InvariantGlobalization`/trimming setting anywhere, so `CurrentCulture` genuinely reflects the
      device's OS locale rather than being forced to a fixed one. The one real, concrete gap:
      `CostSummary.razor` (3 call sites) and `GenerationHistoryDetail.razor` (1 call site) formatted
      the numeric portion of a reported cost with a hard-coded `CultureInfo.InvariantCulture`,
      ignoring the device's decimal-separator/grouping convention — fixed to `CurrentCulture`; the
      ISO currency *code* (e.g. `"USD"`) is still shown as-is alongside the number, not translated
      or reformatted as a symbol, since guessing a symbol for an arbitrary reported currency code
      would risk being wrong. Locked in by
      `UiAssetTests.ReportedCostAmountsFormatUsingTheDeviceLocaleNotAFixedInvariantOne`.
- [x] Accommodate future longer translations and right-to-left languages in layouts without
      restructuring core screens (`plan.md:151`). Converted every physical-direction CSS property
      in `app.css` to its logical equivalent (`margin-left`→`margin-inline-start`,
      `border-right`→`border-inline-end`, `padding-left`→`padding-inline-start`,
      `text-align: left`→`text-align: start`, etc.) — these flip automatically under a future
      `dir="rtl"` without any additional CSS, whereas a hard-coded `left`/`right` would need a
      parallel RTL override for every rule. Grid-based layouts (`.app-shell`'s sidebar/content
      columns, `.dl` metadata grids) were already direction-agnostic (CSS Grid auto-placement
      already respects `direction`) and needed no change. Locked in by
      `UiAssetTests.LayoutUsesLogicalCssPropertiesInsteadOfPhysicalLeftRightOnesForRtlReadiness`.
      Longer-translation accommodation was already in place via this app's existing
      flex-wrap/`clamp()`/`overflow-wrap: anywhere` usage throughout `app.css`, predating this
      milestone.
- [x] Lay groundwork so the device-wide language setting can support a future language override
      (`plan.md:147`), without shipping a second language in this milestone (the first release is
      English only, `plan.md:144`). **Correction to this item's original scope**: this groundwork
      already exists and needed no new code — every user-facing string already goes through
      `IStringLocalizer<UiStrings>` backed by `.resx` resource files (the mechanism Milestone 1's
      own resource-coverage guard enforces), which is exactly .NET's standard mechanism for adding a
      translated `UiStrings.<culture>.resx` file and switching `CultureInfo.CurrentUICulture` to
      pick it up later — there is nothing further to prepare structurally until an actual second
      language and its translated resource file exist.
- [x] Confirm provider model IDs, metadata keys and raw technical identifiers stay unlocalized
      (`plan.md:148`), common provider errors continue converting into localizable application
      messages while sanitized technical details remain separately available (`plan.md:149`), and
      user prompts, metadata, filenames and provider output are never machine-translated
      (`plan.md:150`) — all already true by construction throughout this codebase (provider model
      IDs/metadata keys are always rendered as plain values via `@value` interpolation, never
      passed through `Strings[...]`; error messages shown to the user are always a
      localized/templated `Strings[...]` call with the raw exception detail kept separate in
      diagnostics per this milestone's own Diagnostics section; nothing in the app calls a
      translation service of any kind). No change needed.

## Diagnostics

- [x] Add the rolling diagnostic log: local-only, size-limited rolling files (`plan.md:167`),
      entries older than 30 days removed automatically (`plan.md:168`), a 50 MB device-wide rolling
      cap with oldest-first eviction (`plan.md:169`), time and size limits applied together on every
      write, whichever removes an entry first (`plan.md:170`). Implemented as
      `IDiagnosticsLogger`/`DiagnosticsLogger`
      (`src/Mellow.SlopFactory.Gui/Services/IDiagnosticsLogger.cs` + `DiagnosticsLogger.cs`) — a
      single JSON-lines file under a device-wide folder
      (`FileSystem.Current.AppDataDirectory/diagnostics`), taking a plain directory-path constructor
      argument (like `LibraryWorkspaceFactory`'s root path) rather than yet another MAUI
      path-provider interface, so it's directly testable with a real temporary directory. Every
      `Log()` call re-filters by age and re-checks the byte-size cap together, evicting oldest
      entries first until both are satisfied. **Scope note**: this is a single-file
      read-modify-write-on-write design, not a true multi-file rolling log — a deliberate
      simplification acceptable for occasional diagnostic events rather than a high-frequency
      logging hot path, matching this codebase's existing preference for simple device-wide state
      management (`IRecentLibraryService`'s identical read-modify-write-the-whole-list pattern) over
      a more complex but marginally more efficient alternative.
- [x] Enforce the diagnostic redaction rules architecturally rather than by convention:
      `DiagnosticLogEntry` (`plan.md:171-177`) is a closed set of narrow, structured fields —
      `Timestamp`, `OperationType`, `ProviderType`, `LocalRecordId`, `HttpStatusCode`,
      `ProviderRequestId`, `RetryCount`, `SanitizedError`, `DurationMs`, `IsVerbose`, `IsCrash` — with
      no free-text field a caller could accidentally pass a prompt, credential or file content
      through, except `SanitizedError`, the one free-text field `plan.md:171` itself permits
      ("sanitized errors"); a caller is still responsible for not putting raw content there, the same
      trust boundary the rest of this codebase already places on "sanitized" values.
- [x] Add a diagnostics viewer/export UI (`plan.md:178`): `Diagnostics.razor` (`/diagnostics`,
      linked from a new **Diagnostics** panel in Library Settings) lists every retained entry,
      **Clear** wipes them via `IDiagnosticsLogger.Clear()`, and **Export diagnostics** writes them
      as indented JSON through `IPlatformFileActionService.ExportRawBytesAsync` (the same raw-bytes
      export primitive added for recovery staging), with the export warning from `plan.md:179`
      shown on the page itself.
- [x] Add a temporary verbose-diagnostics toggle that expires automatically one hour after
      activation — including across an application restart — reverting to ordinary logging without
      extending the deadline through activity (`plan.md:180-181`); verbose diagnostics still never
      record credentials or file contents, since it uses the same structured `DiagnosticLogEntry`
      shape as ordinary logging (`plan.md:182`). `IDiagnosticsLogger.EnableVerbose`/`DisableVerbose`
      persist the expiry via the existing `IAppPreferenceStore`, and re-activating while already
      active is a no-op rather than resetting the deadline.
- [x] Confirm no analytics/usage telemetry collection and no automatic crash-report upload
      (`plan.md:183`) — true by construction: nothing in this milestone (or any earlier one) adds a
      network call other than to a configured AI provider; any future telemetry requires explicit
      opt-in, documented before collection begins (`plan.md:190`).

  **Scope note on instrumentation coverage**: the logger and its viewer are fully built and tested,
  but only one real call site currently produces entries —
  `GenerationQueueService.RunJobAsync` logs one entry per completed/failed/staged-for-recovery job.
  `plan.md`'s Diagnostics section describes logging spanning every operation type (connections,
  imports, exports, etc.); wiring the same `IDiagnosticsLogger` into those call sites is
  straightforward given the interface now exists, but doing so for every operation in the app is a
  large, mechanical follow-on left for a dedicated pass rather than this phase, which focused on
  building the subsystem itself correctly (redaction-safe by construction, capped, verbose-mode
  aware, viewable and exportable) end to end for at least one real, already-instrumented path.

## Packaging and distribution

- [ ] Produce a signed Windows MSIX (Store install/uninstall plus a direct-download copy from the
      official project site) and a signed Android App Bundle for Google Play plus a signed direct
      APK (`plan.md:38-39`).
- [ ] Show the installed distribution channel, semantic version and platform build number in
      **About** and diagnostics (`plan.md:40`), with **About** providing a user-activated link to
      the official download page (`plan.md:45`).
- [ ] Confirm store installs rely only on their store update path and direct-download installs
      never self-update or make automatic background update-check requests
      (`plan.md:41-44`); official download/provider-documentation links stay static HTTPS URLs with
      no SlopFactory-added tracking query parameters (`plan.md:46`), and update behavior is never
      automatically switched, combined or redirected between channels (`plan.md:47`).
- [ ] Publish cryptographic checksums and signing-certificate information for direct packages on
      the official download pages (`plan.md:48`).
- [ ] Share stable application/package identity and signing lineage between store and direct
      production builds (`plan.md:49`); require an explicit installer-level warning when switching a
      production installation's source, preserving existing storage identity and never
      self-initiated (`plan.md:50`); reject side-by-side production variants and version downgrades
      (`plan.md:51`), and stop installation without replacing the existing application or data if
      the platform can't validate the shared signing lineage (`plan.md:52`).
- [ ] Keep signing credentials outside the source repository (`plan.md:54`); use semantic
      application versions and platform build numbers for releases (`plan.md:55`); use separate
      package identifiers for development builds, which cannot access production secure storage or
      app-specific Android libraries (`plan.md:60`).
- [ ] Confirm application updates can upgrade library schemas but never silently relocate or delete
      libraries (`plan.md:57`); preserve the versioned device-local export-cleanup journal across
      normal updates and migrate recognized entries conservatively, leaving an unrecognizable entry
      pending for user review rather than dropped or guessed at (`plan.md:58-59`) — scoped to
      whichever export/cleanup mechanism exists once this item is implemented, since the cleanup
      journal itself is out of this milestone's scope (see the exclusions above).
- [ ] Confirm uninstall behavior on each platform: Windows uninstall removes application-owned
      preferences and regenerable caches but never a library (`plan.md:62`); on either platform,
      uninstall also removes the device-local export-cleanup journal and may leave unresolved
      external temporary files that SlopFactory cannot reliably intercept an OS-managed uninstall to
      remove first (`plan.md:63`); after reinstall, never scan external storage for opaque
      temporary-name patterns or delete suspected leftovers, since the missing journal means
      ownership can't be proven safely (`plan.md:64`). Reinstallation checks the known
      default-library location and offers to reopen a valid library found there
      (`plan.md:65`), with other preserved libraries selectable again through the normal
      library-location workflow (`plan.md:66`); API keys must be re-entered when their OS
      secure-storage entries don't survive uninstall (`plan.md:67`). Android uninstall removes
      app-specific libraries, preferences and secure-storage data after the existing warning
      (`plan.md:68`).

## Final Milestone 4 verification

- [ ] Add automated coverage for every behavior above, including crash/process-kill mid-operation,
      volume-disconnect-mid-commit, second-instance-launch forwarding, and dependency-recycled
      queue-pause edge cases, following the same discipline `milestone3.md`'s Final Verification
      pass used (real gaps closed, not just happy-path coverage restated).
- [ ] Run the full automated test suite and verify clean Windows and Android builds (Debug and
      Release) with zero errors, matching the standard established in milestones 1–3.
- [ ] Execute a manual acceptance pass on supported Windows and Android devices per
      `manual_tests.md`, extended with new entries for this milestone's resilience scenarios:
      simulated crash/unclean-exit recovery, removable-volume disconnect during an active commit,
      Android background-transfer notification/permission flows, Windows notification-area
      keep-running behavior, second-instance launch forwarding, and a screen-reader
      (Narrator/TalkBack) pass over the core generate/library/queue screens.
- [ ] Run a performance-profiling pass across large libraries, long queues and low-end/minimum-
      supported devices (`plan.md`'s Milestone 4 summary names "performance" but defines no
      dedicated requirements section — this is verification against the existing acceptance-test
      discipline applied to scale/hardware conditions, not a new feature set) and record findings.
- [ ] Update `plan.md` by removing only verified completed requirements, and keep `docs/user/`,
      `docs/developer/` and `README.md` aligned with finished Milestone 4 behavior.

## Possible future work

Nothing in Milestone 4's scope is currently known to be blocked on missing adapter support or
unresearched provider behavior the way several Milestone 3 items were — this milestone's scope is
almost entirely first-party resilience/packaging work rather than provider-dependent. This section
is kept for consistency with `milestone3.md`'s convention and as a landing spot for anything that
turns out to be blocked once work begins (for example, a discovery that a specific store review
policy or platform API constrains a bullet above in a way not visible from `plan.md` alone).
