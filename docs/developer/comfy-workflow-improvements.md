# Comfy workflow improvement checklist

This checklist records the follow-up work identified from live Comfy Cloud experiments. Built-in workflows must be API-format templates, use only models/nodes verified on Cloud, and have focused automated coverage plus a live result where feasible.

## 1. DWPose to Qwen Union ControlNet shortcut

- [x] Create and live-test a standalone DWPose pose-guide extractor.
- [x] Create a single-run workflow that extracts DWPose from a source image and sends it to Qwen Image + InstantX Union ControlNet.
- [x] Test it against multiple human reference images and compare it with the existing two-step workflow. Live 2026-08-22: both approved reference probes completed; the one-pass graph preserved a usable pose signal but remained sensitive to source framing/prompt (artifacts: `qwen-dwpose-union-*-live.png`).
- [x] Add the verified workflow to the built-in library with focused tests.

## 2. Reusable control-guide extractors

- [x] Create and test Canny/lineart extraction.
- [x] Create and test depth extraction.
- [x] Create and test normal-map extraction.
- [x] Add each remaining verified extractor to the built-in workflow library.

## 3. UI workflow handoff

- [x] Design a direct “Use as control guide” action for compatible completed outputs.
- [x] Implement source-slot preselection from the completed-output action.
- [x] Add UI and service-level tests for the handoff.

## 4. Cloud compatibility diagnostics

- [x] Record required Comfy node types and model filenames for each built-in workflow.
- [x] Add best-effort Cloud preflight checks for required node types.
- [x] Surface submission validation errors as actionable missing-node/model diagnostics.
- [x] Document that Cloud model lists are worker/account dependent and the API is experimental.

## 5. Control tuning

- [x] Identify the few high-value tuning parameters for every ControlNet workflow.
- [x] Expose safe per-workflow defaults and document their effect.
- [x] Test parameter boundaries and preserve existing defaults as the baseline.

## 6. SDXL ControlNet availability

- [x] Obtain explicit authorization before importing any model into Comfy Cloud.
- [x] Confirm required compatible weights are already present in the Cloud ControlNet folder; no import was necessary.
- [x] Verify actual `ControlNetLoader` availability on the active worker (2026-08-22).
- [x] Re-test and add the SDXL Canny, depth, pose, and Union workflows only after validation succeeds. Live 2026-08-22: all four were rejected by `ControlNetLoader` with `Value not in list` even though the experimental inventory/loader enum advertised the SDXL filenames; none were added to the built-in library.
