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

- [ ] Add the **Keep Running** / **Cancel Work and Exit** / **Return to App** dialog shown when
      closing the main window while local work is active (`plan.md:440`), distinct from the
      already-implemented no-active-work exit path and the draft-flush **Retry Save** /
      **Exit and Lose Unsaved Edits** / **Return to App** gate (both already shipped —
      `plan.md:434-439`, `FlushForSuspensionAsync`).
- [ ] **Keep Running** places SlopFactory in the Windows notification area, preserves active work,
      is not itself an exit, and keeps failed draft edits in memory with retry available once the
      window reopens (`plan.md:441-442`).
- [ ] If **Cancel Work and Exit** would also lose unsaved draft edits, run the existing draft-exit
      gate to completion before cancellation or process termination begins (`plan.md:443`).
- [ ] Add a notification-area icon showing aggregate status with reopen/exit actions
      (`plan.md:444`); exiting attempts provider cancellation where supported under the normal
      cancellation rules, and submitted asynchronous remote jobs remain persisted for reconciliation
      on the next launch (`plan.md:445-446`).
- [ ] Add a rememberable **Keep Running** choice, changeable later in settings (`plan.md:447`), and
      never hide in the notification area without first explaining that SlopFactory remains active
      (`plan.md:448`).

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

- [ ] Add a minimal device-wide pending-job registry storing library ID, provider type, connection
      ID, remote job ID and status only — never prompts or source content (`plan.md:322`).
- [ ] When a provider result becomes available while its destination volume is disconnected,
      download it into temporary internal app-specific recovery storage before its remote URL
      expires (`plan.md:323`); recovery staging is identified as temporary and is never treated as
      another library (`plan.md:324`). When the intended library returns, move the staged result
      into it atomically and delete the staged copy (`plan.md:325`); if internal storage is
      insufficient, retain remote job details and retry the download only while the provider result
      remains available (`plan.md:326`).
- [ ] Notify the user that a result is awaiting its library and may expire remotely
      (`plan.md:327`); a staged result is never placed into a different library automatically and
      can be discarded explicitly (`plan.md:328`).
- [ ] Add a recovery-staging list showing each completed result's safe filename, media type, size,
      generation identifier and validation status (`plan.md:329`), previewable through the normal
      sandboxed viewer and media-decoding safety limits (`plan.md:330`).
- [ ] Add **Export Copy**, writing staged bytes to a user-selected external destination without
      creating a library record or changing the intended library (`plan.md:331`); exporting does
      not mark a staged result as reconciled or delete it — it stays staged until the intended
      library accepts it or the user explicitly discards it (`plan.md:332`), and a failed or
      cancelled export leaves the staged copy unchanged (`plan.md:333`).
- [ ] Remove a pending-job registry entry only after successful reconciliation or explicit discard
      (`plan.md:334`).
- [ ] Block ordinary **Forget Library** while completed provider results remain in internal
      recovery staging for that unavailable library (`plan.md:463`), offering cancel/reconnect/
      reconcile or **Discard Staged Results and Forget** (`plan.md:464`), which lists each affected
      generation and staged result, warns the recovered bytes will be permanently lost, and requires
      explicit confirmation before deleting the temporary files (`plan.md:465`). Staged provider
      results are never deleted merely because the library is forgotten or unavailable
      (`plan.md:466`).

  **Relationship to existing Session Recovery work**: `milestone2.md` already shipped the
  draft/autosave half of Session Recovery (dirty-draft marker, autosave, emergency draft snapshot
  for an unavailable library, and the parallel `Forget Library` block/`Delete Recovery Drafts and
  Forget` path for unreconciled draft snapshots, `plan.md:459-462`). This section is the separate
  *result*-staging half plan.md defines alongside it, which has no implementation yet.

## Crash and session recovery

- [ ] Enforce one running SlopFactory process per signed-in Windows user session (`plan.md:352`);
      launching SlopFactory again activates the existing process instead of starting another
      (`plan.md:355`).
- [ ] Forward a second-launch request to open a library or import files to the existing process,
      requiring explicit user confirmation before it changes the active library or imports anything
      (`plan.md:356`); forwarded requests never switch libraries, import files or submit work
      automatically (`plan.md:357`).
- [ ] Keep per-library exclusive locking enforced on both platforms as protection against other
      processes and unexpected re-entry (`plan.md:360`), consistent with the single Windows process
      being able to hold multiple libraries open at once for explicitly submitted background work
      (`plan.md:358`).
- [ ] Create a sanitized local diagnostic record on crash (`plan.md:184`); on next launch, notify
      the user that the application did not close normally and offer to view, clear or export the
      crash diagnostics (`plan.md:185`), with stack traces and request context following the
      existing diagnostic redaction rules (`plan.md:186`, `plan.md:171-176`).
- [ ] Resume polling of every incomplete asynchronous provider job when the application reopens,
      keyed off the already-persisted job IDs (`plan.md:1433`) — closing the known gap
      `GenerationQueueService.cs`'s own code comment and `milestone3.md` both already flag
      ("polling does not resume automatically after an application restart").

## Integrity checks

- [ ] Add an explicit, user-triggered integrity-investigation action that performs a real
      byte-for-byte re-comparison for diagnosing suspected storage or implementation faults,
      distinct from and in addition to the existing single-pass hash check routine duplicate/
      classification workflows already rely on (`plan.md:583`, `plan.md:580-582`).
- [ ] Add managed-file existence verification before export and before provider submission
      (`plan.md:547`), keeping explicit existence, containment and hash checks in those workflows
      regardless of file-watcher state (`plan.md:548`); make export and provider upload
      unavailable while managed content is missing (`plan.md:557`), and never claim to recover
      missing bytes when no backup exists (`plan.md:558`).
- [ ] Add export outgoing-stream-mismatch handling: an outgoing-stream mismatch detected before
      commit aborts the export, cleans up the temporary output, writes no sidecar, marks the
      library record for integrity review, and reports that export did not complete
      (`plan.md:649`) — without marking the library record corrupt or changed merely because a
      destination read-back mismatch occurred after the outgoing stream already matched the stored
      digest and size (`plan.md:651`); if the mismatched object replaced or cannot be removed at the
      destination, report it as potentially corrupt without claiming the prior external object was
      restored (`plan.md:652`).
- [ ] Add **Reacquire Permanently Deleted Output**: when an output file was permanently deleted but
      its history tombstone still identifies a remotely available provider result, let the user
      explicitly reacquire it (`plan.md:1418`) — confirmation-gated, downloaded and validated
      through the normal safety pipeline, creating a new file identity while preserving the former
      file's tombstone and recording that the result was reacquired rather than restored
      (`plan.md:1419`). Compare the downloaded content hash against the permanent-deletion
      tombstone's stored hash (`plan.md:1420`); a mismatch preserves the tombstone and requires a
      clear warning before the new bytes may be committed as a separate **Provider Output Changed**
      result — never described as recovery of the permanently deleted file (`plan.md:1421`).
- [ ] Add the provider-safety-classification/content-replacement integrity rules for a **Missing**
      or **Content Changed** record: a classification received in that state attaches only to the
      immutable provenance of the record's original bytes (`plan.md:549`); restoring
      algorithm/digest/size-matching bytes reactivates the classification and concealment on the
      current file, while differing or externally changed bytes never inherit it (`plan.md:550`).
      A content-replaced file keeps its original provenance for historical context but clearly
      states its current bytes aren't the original content (`plan.md:551`), and generation history
      keeps the original result hash, media type and byte size, immutable after replacement
      (`plan.md:552`). After replacement, revalidate every open generation-tab draft and saved
      setting referencing the file against its new media properties (`plan.md:553`) — an
      incompatible reference shows **Needs Review** and can't be submitted until replaced, restored
      or removed from that input role (`plan.md:554`); compatible references stay selected but show
      **Content Replaced** (`plan.md:555`). A file pinned by queued preparation, upload or another
      active submitted operation can't be replaced or accepted until the pin releases or the work
      is cancelled (`plan.md:556`).

## Accessibility

- [ ] Add Windows Narrator and Android TalkBack support (`plan.md:157`), building on the
      already-shipped Milestone 1 slice (focus restoration and dialog roles) rather than
      duplicating it.
- [ ] Ensure status is never communicated by colour alone anywhere in the interface
      (`plan.md:158`), and meet WCAG 2.2 AA contrast targets throughout (`plan.md:159`).
- [ ] Respect system text scaling, high-contrast mode, reduced-motion settings and light/dark theme
      preferences (`plan.md:160`); ensure thumbnails and media controls provide text alternatives
      (`plan.md:161`).
- [ ] Announce generation progress, completion, failures and validation errors accessibly without
      repeatedly interrupting the user (`plan.md:162`).
- [ ] Size touch targets appropriately and ensure no action depends on hover input
      (`plan.md:163`).

## Localization readiness

- [ ] Use the device locale for dates, times, numbers, byte sizes and currencies throughout the
      application (`plan.md:146`), beyond Milestone 1's no-hard-coded-strings resource-coverage
      guard.
- [ ] Accommodate future longer translations and right-to-left languages in layouts without
      restructuring core screens (`plan.md:151`).
- [ ] Lay groundwork so the device-wide language setting can support a future language override
      (`plan.md:147`), without shipping a second language in this milestone (the first release is
      English only, `plan.md:144`).
- [ ] Confirm provider model IDs, metadata keys and raw technical identifiers stay unlocalized
      (`plan.md:148`), common provider errors continue converting into localizable application
      messages while sanitized technical details remain separately available (`plan.md:149`), and
      user prompts, metadata, filenames and provider output are never machine-translated
      (`plan.md:150`).

## Diagnostics

- [ ] Add the rolling diagnostic log: local-only, size-limited rolling files (`plan.md:167`),
      entries older than 30 days removed automatically (`plan.md:168`), a 50 MB device-wide rolling
      cap spanning ordinary/verbose/crash diagnostics with oldest-first eviction (`plan.md:169`),
      time and size limits applied together, whichever removes an entry first (`plan.md:170`).
- [ ] Enforce the diagnostic redaction rules: never log API keys, authorization headers, raw or
      improved prompts, system instructions, prompt-improvement guidance, source-file contents,
      generated-file contents or signed result URLs (`plan.md:171`); prohibit prompt-related text,
      excerpts, hashes and tokenized forms, permitting only byte counts and normalized operation
      state (`plan.md:172`); never record provider moderation categories or provider-supplied
      descriptions of sensitive content — only that a safety response occurred, its normalized
      outcome, provider type and technical correlation data (`plan.md:174-175`); exclude sensitive
      user-metadata keys/values from diagnostics and diagnostic exports, limiting troubleshooting to
      opaque metadata-entry IDs, value types, byte counts and sanitized validation outcomes
      (`plan.md:176-177`).
- [ ] Permit timestamps, provider types, operation types, local record IDs, HTTP status codes,
      provider request IDs, retry information, sanitized errors and performance timings
      (`plan.md:173`).
- [ ] Add a diagnostics viewer/export UI: view, clear and export sanitized diagnostics
      (`plan.md:178`), warning before export that diagnostics can reveal provider names, model IDs,
      timings and file sizes (`plan.md:179`).
- [ ] Add a temporary verbose-diagnostics toggle that expires automatically one hour after
      activation — including across an application restart — reverting to ordinary logging without
      extending the deadline through activity (`plan.md:180-181`); verbose diagnostics still never
      record credentials or file contents (`plan.md:182`).
- [ ] Confirm no analytics/usage telemetry collection and no automatic crash-report upload
      (`plan.md:183`); any future telemetry requires explicit opt-in, documented before collection
      begins (`plan.md:190`).

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
