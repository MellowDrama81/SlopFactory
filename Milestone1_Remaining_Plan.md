# Milestone 1 remaining-work checklist

This plan is derived from the unchecked entries in `milestone1.md`. Each item is a coherent implementation and verification unit. Manual device checks remain in `manual_tests.md` and must be recorded before the associated item can be closed.

## Baseline UI and accessibility

- [x] Reconcile the completed localization migration with `milestone1.md`, retain its resource-coverage tests, and verify the full automated/build matrix.
- [ ] Complete Follow System theme behavior and Windows high-contrast support; add automated coverage where feasible and perform MT-01.
- [ ] Make primary library workflows responsive at phone, tablet, and desktop widths; add viewport-level regression coverage and perform MT-02.
- [ ] Ensure visible focus, keyboard activation, and focus restoration across primary library and Recycle Bin controls; add keyboard regression coverage and perform MT-03.

## Library location, availability, and recovery

- [x] Persist and validate Windows volume identity for remembered library locations, including a regression test for path reuse on a different volume.
- [x] Revalidate root writability, locking, and atomic filesystem capabilities before every library create/open, with no-mutation failure tests.
- [x] Detect active-library unavailable/read-only transitions, close safely, preserve the remembered entry, and test with a controllable filesystem abstraction.
- [x] Implement Windows moved-library relinking only after the original location is unavailable, including ID/lock/path-update tests.
- [x] Show failed opens as sanitized Corrupt remembered entries with Retry, Choose Another, Forget, and Windows Open Location actions; cover no-repair behavior.
- [x] Recommend, but never automatically begin, an integrity scan after watcher overflow, unsafe volume removal, or storage inconsistency; test each trigger and Start/Defer.

## Android storage and permissions

- [ ] Track Android app-specific external storage by volume identity and safely close/reopen it through removal/reappearance; perform MT-04.
- [ ] Add Android app-specific-storage/uninstall warnings and backup exclusion, then verify the merged manifest and MT-05.
- [ ] Use Android system document pickers for import/export while requesting only operation-specific permissions; verify merged manifest and MT-05.
- [ ] Provide a contextual Android system-settings route for permanently denied permissions; verify it in MT-05.

## Import safety and preflight

- [x] Add recursive folder selection on Windows and Android document providers, preserving the selected hierarchy as virtual folders.
- [x] Add Include Hidden Files review behavior while always excluding protected, system, and reparse entries; cover fixture cases.
- [x] Resolve active, recycled, and pending-deletion duplicates with explicit per-item choices and restoration-preview behavior.
- [x] Copy only primary regular-byte streams with SlopFactory-controlled permissions, excluding ACLs, attributes, executable flags, alternate streams, and extended attributes; test Windows fixtures.

## Viewer and sensitive-data resilience

- [x] Add cancellable background thumbnail/media-metadata extraction with progress and no-mutation cancellation tests.
- [x] Replace sensitive-value edit/filter inputs with masked secure-entry controls and verify accessibility/platform attributes.
- [x] Clear sensitive reveal sessions on library close, switch, lock, unavailability, and application restart; test every transition.

## Completion and release validation

- [ ] Add automated coverage for every remaining Milestone 1 behavior, including cancellation, failed I/O, reparse substitution, and cross-library isolation.
- [ ] Perform MT-06 on supported Windows and Android devices and record results in `manual_tests.md`.
- [ ] Reconcile `plan.md`, README, and user/developer documentation with only verified completed behavior, then run the full tests and both platform builds.
