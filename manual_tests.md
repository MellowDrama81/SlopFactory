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

## Reporting

For each test case, record:

- test ID and result (`Pass`, `Fail`, or `Blocked`);
- platform, OS version, device/emulator model, and app build;
- exact steps and expected/actual result for any failure;
- screenshots or sanitized logs where they help reproduce the issue; never include sensitive metadata values, library contents, or credentials.
