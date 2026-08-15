# SlopFactory user documentation

SlopFactory is a local-first media library being built for Windows 10 version 22H2 (build 19045) or later and Android 8.0 (API level 26) or later. This documentation describes behavior that is implemented and verified in the current development build.

## Available documentation

- [Getting started](getting-started.md)
- [Managing the local library](library.md)
- [Connections, models and AI generation](generation.md)

## Current development status

The current build can create its default local library, import, duplicate, and edit supported text files as independent managed copies, organize files in folders, browse them with paged search and filters, view supported UTF-8 text, raster images, and sanitized SVG, attach typed user metadata, create and display directed file links, search, review, bulk-restore, or permanently delete recycle-bin aggregates, and explicitly scan the local library for integrity issues. Windows and Android projects both compile from the same source and use the same library format.

It can also connect to OpenAI, generic OpenAI-compatible, OpenRouter and DeepInfra APIs; generate text, image, audio and video content against configured models; queue, cancel, retry and review generation results (including an explicit retain/discard decision for a result that doesn't match its expected type); track provider-reported cost and rate-limit state; and save, reuse and review that generation history — see [Connections, models and AI generation](generation.md).
