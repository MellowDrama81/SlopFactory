# Manual test matrix

This document is the single source of truth for manual verification. Run the applicable tests on release-candidate builds in addition to the automated suite. Record the build version, device/OS version, pass/fail result, and any screenshots or logs with the test run.

## Test setup

- **Windows:** use Windows 10 22H2 (build 19045) or Windows 11. Launch the packaged MSIX profile from Visual Studio so the app has its normal package identity.
- **Android:** use a physical device or emulator running Android 8.0 (API 26) or later. Start with a fresh app install for storage, permissions, and first-run tests.
- Use disposable test locations and media. Uninstall and removable-storage tests can permanently remove app-specific data.
- Keep a small fixture set available: a text file, PNG or JPEG, audio or video file, and a nested folder containing at least two files. Keep one deliberately unsupported file for viewer checks.

## MT-01 — Theme persistence and Windows high contrast

1. Open **Library settings**.
2. Select **Light**. Confirm the change is immediate on the current page and after navigating to another page.
3. Close the application completely, launch it again, and confirm **Light** remains selected and active.
4. Repeat steps 2–3 with **Dark**.
5. Select **Follow system**, change the operating-system light/dark setting, return to the app, and confirm the theme follows it.
6. On Windows, enable a contrast theme in **Settings > Accessibility > Contrast themes**, then relaunch the app. Tab through navigation, buttons, inputs, dialogs, and the recycle bin. Confirm text, focus indicators, selected states, warnings, and disabled controls remain visible and distinguishable. Restore the normal contrast theme after the test.

**Pass:** each preference applies immediately and survives restart; high-contrast colors do not make any tested control unreadable or inoperable.

## MT-02 — Responsive layout and touch interaction

1. On Android, test a phone-width viewport (approximately 360–430 dp) and a tablet-width viewport (at least 600 dp). On Windows, test a narrow window and a normal desktop-width window.
2. At each size, visit Library settings, the file list, file detail, import flow, links, and recycle bin.
3. Exercise the primary controls: open/switch a library, import a fixture, open a file detail, change metadata, create or follow a link, recycle and restore an item, and start then cancel an integrity scan.
4. On Android, perform the same actions by touch only. Rotate the device if it supports rotation.
5. Check dialogs, confirmation controls, menus, text fields, progress displays, and long paths/names. Scroll where necessary.

**Pass:** controls are reachable, tappable, and not clipped or overlapped; horizontal scrolling is not needed to perform a workflow; text remains understandable at each tested size.

## MT-03 — Windows keyboard and focus recovery

1. On Windows, use only the keyboard. Press `Tab` and `Shift+Tab` through the library list, file list, detail actions, import controls, Library settings, and recycle bin.
2. Confirm every focused interactive control has a clearly visible indicator. Use `Enter` and `Space` to activate buttons, checkboxes, menus, and confirmation actions.
3. Open a confirmation interaction (for example, recycle, restore, forget, clear previews, or permanently delete), cancel it, and confirm focus returns to the invoking control or another sensible nearby control.
4. Complete the same interaction and confirm focus moves to a meaningful surviving control rather than disappearing or moving to the browser chrome.

**Pass:** all primary actions are keyboard-operable, focus is visible, and focus remains usable after cancellation, completion, navigation, or list updates.

## MT-04 — Android app-specific and removable storage

1. On an Android device or emulator with an available external/emulated app-specific volume, open **Library settings** and select that volume from **App-specific storage location**.
2. Create or open a test library there. Import a fixture and note the active library name and location.
3. Make the selected volume unavailable using the device's removable-storage procedure (for example, safely remove the test SD card, or use the emulator's configured removable/emulated storage controls).
4. Return to SlopFactory and trigger a normal operation or wait for its availability check. Confirm the library closes safely and is shown as unavailable; do not accept any repair or replacement library.
5. Make the same volume available again, reopen or retry the remembered library, and confirm the original library and imported fixture are available.

**Pass:** removal does not corrupt, replace, or silently recreate the library; the remembered entry remains; only the original matching volume/library is reopened.

## MT-05 — Android uninstall, backup, document pickers, and permissions

1. With a disposable app-specific library selected, open the Android system **App info** screen for SlopFactory and review storage/permissions.
2. From SlopFactory, read the Android-storage warning in **Library settings**. Use **Open system settings** and confirm it opens the SlopFactory App info screen.
3. Use **Import** and choose a fixture through the Android system document picker. Confirm the picker is system-provided and import succeeds only for the selected item(s).
4. Export a file and confirm the Android system save/create-document picker is used. Cancel once, then export successfully to a chosen destination.
5. Cancel or deny any picker/permission request offered by the operating system. Confirm the app reports a safe cancellation/failure, leaves managed data unchanged, and provides the contextual system-settings shortcut when the platform reports a permanently denied permission.
6. In Android App info, confirm the app has not requested broad storage, camera, microphone, contacts, location, or media-library access for these workflows.
7. Uninstall SlopFactory, then reinstall it. Confirm the warning accurately described the outcome: the test app-specific library is gone. Do not use a library you need to retain for this test.
8. Where the device exposes backup controls, confirm SlopFactory app data is not offered for cloud backup. Record the device-specific observation; the merged manifest check remains part of automated/build verification.

**Pass:** system document pickers handle import/export; cancellation/denial is safe; no unrelated broad permissions are granted; app-specific data is clearly warned about and is removed by uninstall; the settings shortcut is contextual and functional.

## MT-06 — Cross-platform acceptance workflow

Perform this sequence once on Windows and once on Android using a fresh disposable library.

1. Create a library, rename it, restart the app, and confirm it reopens.
2. Switch to a second empty library, then reopen the first from **Recent libraries**. Confirm switching never moves or copies data.
3. Import the nested fixture. Confirm its virtual folder hierarchy is present. Open each supported media type and the unsupported fixture; verify viewers are appropriate and export/external-open remains available where offered.
4. Add ordinary and sensitive metadata. Reveal a sensitive value, then switch libraries and restart. Confirm it is concealed again and its value was not exposed in notices or history.
5. Create a file link, recycle a file/folder/link, restore it, then use a separate disposable item to test permanent deletion and its confirmation flow.
6. Start a full integrity scan, observe progress, cancel it once, then resume/complete it. Export the findings JSON and inspect it for scan status and opaque identifiers only—no filenames, paths, hashes, metadata values, credentials, or file bytes.
7. On Windows, make an active test library temporarily unavailable (for example, use a disposable removable volume or safely rename its parent while the app is open), then restore availability and retry. Confirm the remembered entry is preserved and no replacement library is silently created.

**Pass:** every workflow completes without data loss outside the explicitly tested permanent deletion; warnings and recovery paths are clear; sensitive data and diagnostic exports remain minimized.

## MT-07 — Generate tab strip and draft autosave

1. Open **Generate** with at least one configured Text model. Confirm a single default-titled draft tab ("Draft 1") is already open.
2. Click **+** to add a second tab. Confirm it opens with empty fields and its own automatic title ("Draft 2"), and that the first tab's fields are unaffected.
3. In the first tab, type a prompt, then immediately switch to the second tab. Confirm the status indicator showed **Saving…** then **Saved**, and switching back to the first tab still shows the prompt you typed (autosave/flush-on-switch did not lose it).
4. Edit the **Tab title** field on the second tab. Confirm the tab's label updates to the custom title. Click **Reset to automatic title** and confirm it reverts to "Draft 2".
5. Click **Duplicate tab** on a tab with a model/prompt already set. Confirm a new tab appears next to it with the same field values but no custom title, and that it is not linked to any run or history entry.
6. With three or more tabs open, use the **‹**/**›** buttons to move a tab. Confirm its position updates immediately, the leftmost tab's **‹** is disabled, the rightmost tab's **›** is disabled, and the new order survives an app restart.
7. Click the **×** on a tab that is not generating. Confirm the three-way close panel appears: **Discard without saving**, **Save settings first**, and **Keep tab open**.
8. Click **Keep tab open**. Confirm the tab remains open and unchanged.
9. Click **×** again, then **Save settings first**. Enter a title already used by an existing saved setting and confirm **Save and close**; confirm an inline error appears and the tab stays open. Enter a new, unused title and confirm **Save and close**: confirm the tab and its draft are gone, and the new saved settings entry appears on `/saved-settings` with the tab's model/prompt/result-count/destination.
10. Click **×** on another tab and choose **Discard without saving**. Confirm the tab and its draft are gone permanently, with no recycle-bin entry anywhere in the app.
11. Close every remaining tab. Confirm the app never shows zero tabs — a fresh empty draft appears automatically.
12. Start a generation on one tab, then switch to another tab while it is still running. Confirm the busy tab's **×** stays disabled until the generation finishes, and that switching tabs does not crash or duplicate the in-flight request.
13. Restart the app and reopen **Generate**. Confirm previously open draft tabs (title, prompt, model, and other field values) are restored from the library rather than reset.
14. Simulate an autosave failure (for example, make the library location briefly unwritable) and confirm the status shows **Not saved** with a working **Retry save** action.
15. On a tab with a model selected, recycle and permanently delete that model (or mark it **Needs Review**) from `/models` in another tab, then switch back to the affected tab. Confirm a notice explains the previously selected model is no longer available, the model select falls back to another active model instead of showing a blank/mismatched selection, and clicking **Generate** works normally with the fallback model rather than silently doing nothing.
16. Enter a title and click **Save As** on a fresh tab. Confirm it creates a new saved setting and the **Save** button (previously disabled) becomes enabled. Change the prompt and click **Save**. Confirm it updates the same saved setting in place (no new entry appears on `/saved-settings`) rather than creating another one.
17. Open the same saved setting from `/saved-settings` into two separate tabs (via **Use**, once from each tab context or by using **Use** twice). In the first tab, change the prompt and click **Save**; confirm it saves normally. In the second tab, change the prompt differently and click **Save**. Confirm a conflict panel appears (rather than silently overwriting the first tab's save) offering **Overwrite**, **Save As**, and **Cancel**. Click **Overwrite** and confirm it saves the second tab's changes; reopening the saved setting confirms the second tab's prompt won, not the first's.

**Pass:** tabs create/duplicate/rename/reset/reorder/close correctly; all three close options behave as described, including a duplicate-title error keeping the tab open; autosave never silently loses an edit across a tab switch or restart; a discarded tab is unrecoverable and clearly warned about before confirming; the app never has zero tabs open; a draft whose model became unavailable is clearly explained rather than silently broken; **Save** and **Save As** behave as distinct, predictable actions; a save conflict between two tabs on the same saved setting is surfaced explicitly rather than silently overwritten.

## MT-08 — Generation queue and concurrency

1. With one configured model on one connection, open three draft tabs and click **Generate** on all three in quick succession. Confirm exactly one shows **Generating…** while the other two show **Queued** with an increasing position number, and that the sidebar/top notice shows a matching "N queued, M running" count.
2. Let the first finish. Confirm the second tab automatically transitions from **Queued** to **Generating…** without any manual action, and the notice count updates.
3. While a tab is queued, click its **Cancel** button. Confirm the tab returns to its idle prompt/form state immediately (no provider request was ever visibly made — for example, no network activity for that submission), and that `/generation-history` gains no entry for it.
4. While a tab is actively generating, click its **Cancel** button. Confirm the same behavior as before this change (a cancellation message, no history entry).
5. Configure a second connection with its own model. Submit one generation on each connection at the same time. Confirm both run concurrently (both tabs show **Generating…** simultaneously), proving per-connection scheduling doesn't serialize unrelated connections.
6. Start a generation on one tab, then navigate away to a different page (for example `/generation-history`) before it finishes, then navigate back to `/generate` and reselect that tab. Confirm the submission continued in the background and its result (or Queued/Generating status) is still shown correctly, not lost or reset.
7. With a generation queued (not yet running), close the app's active library or switch to a different one. Confirm the queued job disappears without creating a history entry, and that a job that was actively running at that moment does not crash the app.
8. Open **Queue** from the sidebar (or click the activity notice) while several jobs are queued/running across one or more connections. Confirm each connection's jobs are grouped together, each entry shows the originating tab title, model, and prompt, and a running entry has no reorder buttons.
9. Use the **‹**/**›** buttons on a queued entry to move it within its connection's group. Confirm the order updates immediately and matches the order those jobs actually start in. Confirm the leftmost/rightmost queued entry has its respective button disabled.
10. Click **Cancel** on a queued entry from the **Queue** page (not from `/generate`). Confirm it's removed the same way cancelling from its originating tab would behave.
11. Set the device-wide submission cap (Library Settings) to at least 3, then start enough generations across enough connections/models that more than one is running simultaneously. While at least one is actively **Generating…**, turn on the device's real battery saver / energy saver mode (Windows: Settings > System > Power > Battery saver; Android: Settings > Battery > Battery Saver). Confirm every already-**Generating…** tab keeps running to completion — none are cancelled or interrupted — while `/queue` and the sidebar notice now show **Energy Saver is limiting submissions to 1 at a time**, and any newly submitted generation stays **Queued** instead of starting immediately even though slots are nominally free.
12. With energy saver still on and at least one generation queued from step 11, turn energy saver back off. Confirm the queued generation starts automatically within a moment, with no need to resubmit, cancel/retry, or navigate away and back.

**Pass:** exactly one job per connection runs at a time; queued jobs start automatically as slots free up; queued-cancel never contacts the provider or creates history; a submission survives navigating away from and back to `/generate`; switching libraries cleanly drops/cancels outstanding work without a crash; the **Queue** page's grouping, reordering and cancellation all match what happens from `/generate` itself; enabling the device's real energy saver mode never interrupts an already-running generation and reliably limits new starts to one at a time; disabling it resumes queued work automatically.

## MT-09 — Revisioned credential lifecycle

1. Create a connection with a valid API key that passes **Test Connection**, then click **Save**. Confirm it saves immediately without any extra prompt (the happy path is unchanged from the user's perspective).
2. Edit that connection, click **Replace API key**, type a key you know is invalid, and click **Save**. Confirm Save does *not* navigate away; instead a decision panel appears with **Keep Existing Key** and **Save New Key as Unverified**, showing the test failure reason.
3. From that panel, click **Keep Existing Key**. Confirm you're returned to the Connections list, the connection's status is unchanged, and re-opening the editor shows the original masked "Credential stored" view (the invalid key was never persisted).
4. Repeat step 2, then click **Save New Key as Unverified** instead. Confirm you're returned to the Connections list and the connection's status shows an unverified/failed state rather than **Credentials Required** (the new key was saved despite failing the test).
5. Edit the same connection again with a key that passes the test, click **Save**, and confirm it saves normally and the status becomes verified — proving the previously-saved unverified key was fully replaced, not left alongside the new one.
6. On a device or emulator with an existing library created before this feature shipped (or restore one from a backup taken before this change), open it in the current build. Confirm every connection that had a working credential still generates successfully with no forced re-entry of any key and no visible change in status — this exercises the silent one-time legacy-credential adoption on first open.
7. If reachable in a debug build (for example by directly clearing a connection's secure-storage entry via a debug tool while leaving its database row untouched, or by restoring a library snapshot known to be in this state), force a connection into **Credential State Requires Repair**. Confirm the Connections list shows that status ahead of any test-result status, the editor skips the masked "Credential stored" view and shows the key input directly behind an error banner, and entering and saving a working key clears the repair state.

**Pass:** a successful Save behaves exactly as before this feature; a failed test blocks navigation until the user explicitly chooses Keep or Save-Unverified; Keep Existing Key never persists the failed candidate; Save New Key as Unverified persists it and reflects an unverified/failed status; a subsequent successful Save fully supersedes it; a pre-existing library upgrades silently with no connection incorrectly losing its working credential; and a **Credential State Requires Repair** connection is clearly surfaced and recoverable.

## Reporting

For each test case, record:

- test ID and result (`Pass`, `Fail`, or `Blocked`);
- platform, OS version, device/emulator model, and app build;
- exact steps and expected/actual result for any failure;
- screenshots or sanitized logs where they help reproduce the issue; never include sensitive metadata values, library contents, or credentials.
