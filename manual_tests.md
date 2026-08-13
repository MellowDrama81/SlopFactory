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

## MT-10 — Unified recycle bin for connections, models, and saved settings

1. On `/connections`, `/models`, and `/saved-settings`, confirm each page now shows only its active list and a **Recycle** action — no recycled-list toggle, and no Restore/Delete Permanently buttons remain — with a link to the recycle bin at the bottom.
2. Recycle a connection that has at least one model with a saved setting attached. Open `/recycle-bin`. Confirm the connection shows as one entry with a summary like "2 model(s), 1 saved setting(s)", and that the model and saved setting do **not** also appear as their own separate rows.
3. Filter the recycle bin's category dropdown to **Connections**, then **Models**, then **Saved settings** in turn. Confirm each filter shows only entries of that kind.
4. Recycle a model directly from `/models` while its connection stays active. Confirm it now appears in the recycle bin as its own row (originally in the connection's label), and that restoring it from the bin succeeds and the model reappears as active on `/models`.
5. Select the recycled connection from step 2 and click **Restore** from the recycle bin. Confirm the connection, its model, and its saved setting are all active again afterward (check `/connections`, `/models`, `/saved-settings`), and none of them show as separate recycle-bin entries anymore.
6. Recycle a different connection that has a stored, working API key. From the recycle bin, click **Delete Permanently** on it (confirm the warning) or use **Empty Recycle Bin** with it included. Confirm the connection, its models and saved settings are all gone from their respective pages, and — using whatever debug/inspection means are available for this build — confirm its secure-storage credential entries are also gone, not just its database rows.
7. Recycle two or more connections (each with a stored credential) at once, then use **Empty Recycle Bin**. Confirm all of them are fully removed, including their credential entries, and that the operation completes normally even though multiple connections' credentials are being cleaned up in the same action.
8. Try to restore a recycled connection whose label now collides with an existing active connection (create a new active connection reusing the recycled one's exact label first). Confirm the recycle bin's restore preview reports a name-conflict blocker and does not restore it silently.

**Pass:** Connections/Models/SavedSettings pages only handle recycling; all restore and permanent-delete actions live in `/recycle-bin`; a recycled connection's models and saved settings are folded into its own entry rather than double-listed; the category filter, batch restore, and Empty Recycle Bin all work correctly across the three new kinds; permanently deleting a connection through the bin (individually or via Empty Recycle Bin, including multiple connections at once) also removes its secure-storage credential entries, not just its database rows; a label/title conflict is reported before restoring rather than failing silently or overwriting.

## MT-11 — Generation-completion OS notifications

1. On Library Settings, confirm **Generation notifications** is off by default and no notification permission has been requested yet.
2. Turn the toggle on. On Android, confirm the system notification-permission prompt appears at this moment (not before) and only on Android 13+; on Windows, confirm the toggle simply enables with no prompt.
3. On Android, deny the permission prompt from step 2. Confirm the toggle reports it could not be enabled and stays off.
4. Re-enable it and grant permission this time. Start a generation, then background the app (Android: press Home; Windows: minimize or switch to another window) before it finishes. Confirm an OS notification appears once it completes, showing only the model label and a status (Completed/Failed/Partially Completed) — no prompt text, filename, or provider error text anywhere in the notification.
5. Tap/click the notification. Confirm the app comes to the foreground (or launches, if it was fully closed) and navigates directly to that generation's `/generation-history/{id}` detail page.
6. Start another generation while the app stays in the foreground and focused the whole time. Confirm no OS notification appears for it at all — only the in-app status update.
7. Start a generation, background the app, but keep that record's own `/generation-history/{id}` page open in another window/already navigated to before backgrounding (Windows: two windows if supported, or navigate to the page, then background). Confirm no redundant OS notification appears for that specific record while its page is the one currently open.
8. Turn the notifications toggle back off. Start a generation and background the app before it finishes. Confirm no OS notification appears, with no repeated permission prompt.

**Pass:** the setting defaults off; Android's notification permission is requested only on enabling, and denial keeps the feature off without crashing; a backgrounded/unfocused completed or failed generation produces an OS notification containing only the model label and status; tapping it opens the correct generation-history record; no notification appears while foregrounded, while its own record page is already open, or while the setting is off.

## MT-12 — Saved-settings Review Changes diff

1. Open the same saved generation setting into two separate tabs (use **Use** from `/saved-settings` twice, or once and duplicate the tab).
2. In the first tab, change the prompt and click **Save**. Confirm it saves normally (no conflict, since it's the first change).
3. In the second tab (still holding the original loaded revision), change the destination folder and result count, then click **Save**. Confirm the conflict panel appears instead of silently overwriting.
4. Click **Review Changes**. Confirm it expands a list showing only the fields that actually differ between your unsaved change and the just-saved version from step 2 — for this scenario, at least **Prompt** (yours: original prompt; saved: the tab-1 prompt) plus your own actually-changed fields (destination folder, result count) if they differ from what's currently saved. Confirm fields you didn't touch that also happen to match the saved version are *not* listed.
5. Confirm the diff shows human-readable values, not raw IDs — the destination folder as a path (e.g. "Library / Subfolder") and the model as its label, not a GUID.
6. Without closing Review Changes, click **Overwrite**. Confirm it saves successfully using your second tab's values, and that neither version was mutated by merely viewing the diff (re-open the setting fresh afterward and confirm it matches what you saved, not some merged result).
7. Repeat steps 1–3, but this time have both tabs load the setting with truly identical field values and only the title differing between them. Reproduce a conflict where nothing else actually changed, and click **Review Changes**. Confirm it reports no fields differ, rather than showing an empty or broken list.

**Pass:** Review Changes shows exactly the fields that differ between the two versions, with human-readable values, and never modifies either version; Overwrite/Save As/Cancel remain fully usable regardless of whether Review Changes was opened; a conflict with no actual field differences is handled gracefully.

## MT-13 — Android compact tab-switcher and searchable tab management

1. On Android, open **Generate**. Confirm the full tab strip is gone, replaced by a single compact control showing the active tab's title and its position out of the total count (e.g. "Draft 1 (1 of 3)").
2. Tap the compact control. Confirm a searchable list opens showing every open tab, each with move/select/rename/duplicate/close controls, and a text field to filter by title.
3. Type part of a tab's title into the search field. Confirm the list filters live (no submit button needed) to only matching tabs.
4. On Windows, open **Generate** with enough tabs open that the strip would previously have wrapped onto a second row. Confirm the strip now scrolls horizontally in place instead of wrapping, and confirm a **Manage tabs** button next to it opens the same searchable list used on Android.
5. From the list (either platform), rename a tab that is **not** the currently active one. Confirm its title updates in the list and in the strip/compact control without switching you away from your current tab.
6. From the list, duplicate a tab that is **not** the active one. Confirm a new duplicate tab appears and you are switched to it (and, on Android, the switcher closes since you've now navigated to a tab).
7. From the list, use the move controls to reorder tabs without first switching to any of them. Confirm the new order is reflected both in the list and in the strip/compact control, and survives an app restart.
8. Start a generation on one tab, then open the switcher. Confirm that tab shows a running/queued status indicator in the list, and its close button stays disabled there too (matching the strip's existing behavior).
9. Tap/click a tab's title from within the list. Confirm it switches to that tab and closes the switcher.
10. Open the switcher with a large number of tabs open (20+, using duplicate repeatedly). Confirm the list remains smooth to scroll and does not need every tab rendered at once (no visible lag opening the switcher regardless of tab count).

**Pass:** Android shows only the compact control (never the full strip); Windows/tablet keep the scrollable strip plus a working **Manage tabs** entry point to the same list; the list's search filters live; rename/duplicate/reorder/close all work directly from the list without requiring a switch to that tab first; an active job's tab still can't be closed from the list; selecting a tab from the list switches to it and closes the list; the list stays responsive with many tabs open.

## MT-14 — Saved-settings source recycled/deleted while a tab is open

1. Use a saved generation setting into a tab (**Use** from `/saved-settings`). In a second tab or browser context, recycle that same saved setting from `/saved-settings`.
2. Back in the first tab, change the prompt and click **Save**. Confirm a panel appears explaining the saved settings were recycled, offering **Restore and save**, **Save As**, and **Cancel** — not the generic error message.
3. Click **Restore and save**. Confirm the saved setting becomes active again on `/saved-settings` with your tab's current values (prompt, model, etc.), and the tab now shows it saved successfully.
4. Repeat steps 1–2, but this time click **Cancel** on the recycled-settings panel. Confirm the tab is unaffected and you can keep editing.
5. Repeat steps 1–2, but this time click **Save As** instead. Confirm a new, separate saved setting is created with your tab's values, the original recycled one is untouched, and the tab's source reference now points to the new one.
6. Use a saved generation setting into a tab, then permanently delete that saved setting (recycle it, then permanently delete from `/recycle-bin` or `/saved-settings`). Back in the tab, click **Save**. Confirm a message explains the saved settings were permanently deleted, and that only **Save As** remains available afterward (the **Save** button itself should now be disabled/hidden, matching how a tab with no loaded settings normally behaves).
7. From that same tab, click **Save As** with a new title. Confirm it creates a new saved setting successfully and the tab now behaves as a normal saved tab going forward.
8. Reproduce the recycled-settings panel again (step 1–2), but first create a second active saved setting reusing the recycled one's exact title before clicking **Restore and save**. Confirm the restore fails with a name-conflict message rather than silently succeeding or corrupting either record.

**Pass:** a recycled source saved setting offers Restore-and-save/Save-As/Cancel instead of a generic error; Restore and save both un-recycles the original and applies the tab's current changes in one action; a permanently deleted source clears the tab's save-in-place link so only Save As remains; a title conflict at restore time is reported clearly rather than failing silently.

## MT-15 — A generation tab's own model recycled or permanently deleted

1. Select a model on a `/generate` tab, then recycle that model from `/models` (in another tab or after navigating away and back).
2. Return to the affected tab (switch away and back, or reopen `/generate`). Confirm a notice specifically names the recycled model (not a generic "unavailable" message) and offers a **Restore model** button, while the model dropdown has already fallen back to another active model so the tab stays usable in the meantime.
3. Click **Restore model**. Confirm the model becomes active again on `/models`, the notice disappears, and the dropdown now shows that model selected again on this tab.
4. Repeat steps 1–2, but this time manually pick a different model from the dropdown instead of clicking Restore. Confirm the notice clears once you've made an explicit selection and Generate works normally with your chosen model.
5. Select a model on a tab, then permanently delete that model (recycle it, then permanently delete from `/recycle-bin` or `/models`). Return to the affected tab. Confirm a message explains the model was permanently deleted, the dropdown shows no model selected (a visible "Choose a model" placeholder, not a silently-substituted real model), and the **Generate** button is disabled.
6. From that same tab, select any active model from the dropdown. Confirm the notice clears, **Generate** becomes enabled again, and generating works normally.
7. Recycle a model whose owning connection is also currently recycled. From the affected tab, click **Restore model**. Confirm it fails with a message explaining the connection must be restored first, rather than silently succeeding or leaving the tab in a broken state.
8. Recycle a model, then create a new active model reusing its exact label before clicking **Restore model** from the affected tab. Confirm the restore fails with a name-conflict message rather than silently succeeding or corrupting either model.
9. Mark a tab's model **Needs Review** (via a provider-model-ID or mode change on `/models`) rather than recycling or deleting it. Confirm the tab still shows the existing generic "model unavailable, choose another" message and auto-falls-back to another model as before — this case should be unaffected by this change.
10. On a tab with one of its 3 source slots (Source 1/2/3) filled, separately recycle or permanently delete that source file (not the model). Confirm that slot alone silently reverts to "None" with no restore button or blocking notice, and the other two slots are unaffected — this remains intentionally unchanged.

**Pass:** a recycled model shows a specific message naming it with a working inline Restore action; a permanently deleted model blocks Generate until an explicit replacement is chosen, with the dropdown visibly showing no selection rather than a silent substitution; restoring into a recycled-connection or label-conflict state fails with a clear message; the Needs-Review case and the source-image field are both unaffected by this change.

## MT-16 — Generation-history recycle bin and file tombstoning

1. Generate at least one text result and one image result so `/generation-history` has entries with result files.
2. From `/generation-history`, click **Recycle** on one record. Confirm it disappears from the list immediately and a link to the recycle bin is shown.
3. Open `/recycle-bin`. Confirm the recycled record appears with the model's label as its name and "Generation History" as its original location, and confirm its source/result files are **not** also listed as separately recycled.
4. Restore it from the bin. Confirm it reappears on `/generation-history` and its detail page (`/generation-history/{id}`) loads normally with all its result files still linkable.
5. Recycle the same record again, then permanently delete it from the bin (or via **Empty Recycle Bin**). Confirm the record is gone, but its result files are still present and active (check the destination folder or `/file/{id}` directly) — permanently deleting a generation record must never delete its files.
6. From `/generation-history/{id}` (a still-active record), click **Recycle** directly from the detail page. Confirm the same recycle behavior as step 2, and confirm the confirmation panel explains that the record's files are not affected.
7. On an active generation record with a result file, permanently delete just that result file (via `/file/{id}` or the recycle bin, not the generation record). Return to `/generation-history/{id}`. Confirm the result list shows a "permanently deleted" entry with the file's former name and type instead of a broken link, and the record itself is untouched (still active, other results still linkable).
8. Do the same for a record with all 3 source slots filled: permanently delete one of the source files directly (e.g. the Source 2 file). Return to the record's detail page. Confirm the deleted slot shows a "permanently deleted" message with its former name and type (labelled with its slot number) instead of silently showing nothing, while the other two slots still show working links.
9. Confirm a recycled generation record's detail page (reached via the bin's "view details" link) still displays correctly — prompt, status, result/tombstone list — even though it's not on the active `/generation-history` list.

**Pass:** generation records can be recycled, restored, and permanently deleted through both the list, detail page, and unified recycle bin; recycling or permanently deleting a record never touches its source or result files; a permanently deleted source or result file leaves a readable "permanently deleted" tombstone (former name and type) in generation history instead of a broken link or silent disappearance; a recycled record's detail page remains viewable via the bin.

## MT-17 — Typed generation settings (Use/Reset Provider Default)

1. On `/generate`, select a Text-mode model. Confirm a collapsed **Generation settings** section appears (near the source-image field), and that it does **not** appear when a non-Text-mode model is selected.
2. Expand it. Confirm five fields: Temperature, Top P, Max tokens, Frequency penalty, Presence penalty — each blank by default with a "leave blank to use the provider default" help note stating its valid range.
3. Set a value in each field (e.g. Temperature 0.7, Top P 0.9, Max tokens 500, Frequency penalty 0.5, Presence penalty -0.5) and submit a generation. Confirm it completes normally.
4. Enter an out-of-range value in one field (e.g. Temperature 3). Confirm submission is rejected with a clear validation message rather than silently sent to the provider.
5. Clear a previously-set field back to blank (**Reset to Provider Default**). Confirm autosave persists the change — reload the page/tab and confirm the field is still blank, not reverted to its old value.
6. Save the current settings (with some fields set, some blank) via **Save As**, then load a different draft, then reopen the saved setting via **Use**. Confirm all five fields reload exactly as saved, including which ones are blank.
7. Open a past generation from `/generation-history` via **Use Again**. Confirm the settings used for that generation load into the form.
8. Open `/generation-history/{id}` for a generation submitted with explicit settings. If the detail page surfaces settings, confirm the explicit values are shown (not fabricated defaults) — otherwise treat this as informational, since detail-page display of settings is not required by this slice.
9. Switch the model to an Image-mode model with the settings section still expanded and values entered, then switch back to a Text-mode model. Confirm the previously entered values are still present (not silently cleared by the mode switch).

**Pass:** the five typed settings are Text-mode-only, each starts blank/Use Provider Default, an explicit value can be set and later reset to blank, out-of-range values are rejected before submission, and the settings round-trip correctly through autosave, saved settings, and Use Again.

## MT-18 — Multi-source input slots (Source 1/2/3)

1. On `/generate`, select a Text-mode model. Confirm three source fields appear — Source 1, Source 2, Source 3 — each independently optional and offering the same list of active image files.
2. Fill all 3 slots with 3 different images and submit a generation. Confirm it completes normally and, if the result panel/history detail shows sources, all 3 appear as separate links in slot order.
3. Select the same file in two different slots (e.g. Source 1 and Source 3). Confirm an inline error appears and the **Generate** button is disabled until the duplicate is resolved.
4. Fill only Source 2, leaving Source 1 and Source 3 blank, and submit. Confirm generation completes normally (a non-contiguous single slot works, not just Source 1 alone).
5. Fill all 3 slots, then switch to an Image-mode model and back to a Text-mode model. Confirm all 3 selections are still present (not silently cleared by the mode switch).
6. Autosave: fill Source 2 and Source 3, wait for autosave, then reload the page/tab. Confirm both selections persist.
7. Save the current selections via **Save As**, load a different draft, then reopen the saved setting via **Use**. Confirm all 3 slots reload exactly as saved.
8. Open a past generation from `/generation-history` via **Use Again** where more than one slot was used. Confirm all of them load into the form.

**Pass:** all 3 source slots are Text-mode-only, independently optional, reject a duplicate file selected across slots before submission, are unaffected by a temporary mode switch, and round-trip correctly through autosave, saved settings, and Use Again.

## MT-19 — Approximate prompt/context token estimate

1. On `/generate`, select a Text-mode model. Confirm a "~N tokens (rough estimate, not exact...)" line appears below the Prompt field, and that it does **not** appear when a non-Text-mode model is selected.
2. Type continuously in the Prompt field without clicking away. Confirm the estimate updates live, character by character, rather than only after you click or tab out of the field.
3. Type in System Instructions (for a model that supports it). Confirm the estimate reflects the combined length of both fields, not just the prompt.
4. Clear both fields. Confirm the estimate reads 0.
5. Confirm the estimate never disables or blocks the **Generate** button, however long the text — it's informational only, with no enforced limit.

**Pass:** the estimate is Text-mode-only, updates live while typing in both the prompt and system-instructions fields, reflects their combined length, and never blocks submission.

## Reporting

For each test case, record:

- test ID and result (`Pass`, `Fail`, or `Blocked`);
- platform, OS version, device/emulator model, and app build;
- exact steps and expected/actual result for any failure;
- screenshots or sanitized logs where they help reproduce the issue; never include sensitive metadata values, library contents, or credentials.
