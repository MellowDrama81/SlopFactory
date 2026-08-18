# SlopFactory user documentation

SlopFactory is a local-first application for generating text, image, audio, and video content with
configurable AI providers, for Windows 10 version 22H2 (build 19045) or later and Android 8.0 (API
level 26) or later. Its bundled media library exists to organize the source files you feed into a
generation and the outputs you get back — not as a general-purpose file manager in its own right.
This documentation describes behavior that is implemented and verified in the current development
build.

## Available documentation

- [Getting started](getting-started.md)
- [Connections, models and AI generation](generation.md)
- [Managing the local library](library.md)

## Current development status

SlopFactory can connect to OpenAI, a generic OpenAI-compatible API, OpenRouter, DeepInfra, and
1min.AI; generate text, image, audio and video content against configured models (DeepInfra and
OpenRouter cover all four modes, 1min.AI covers text/image/audio); queue, cancel, retry and review
generation results (including an explicit retain/discard decision for a result that doesn't match
its expected type); track provider-reported cost, or see a live pre-generation cost estimate where
one provider (OpenRouter) publishes the pricing to compute one from; and save, reuse and review that
generation history — see [Connections, models and AI generation](generation.md).

Around that, it can create its default local library, import, duplicate, and edit supported text
files as independent managed copies, organize source and output files in folders, browse them with
paged search and filters, view supported UTF-8 text, raster images, audio, video, and sanitized SVG,
attach typed user metadata, create and display directed file links, export files (optionally with a
privacy-minimal `.slopfactory.json` sidecar describing a generated file's origin), search, review,
bulk-restore, or permanently delete recycle-bin aggregates, and explicitly scan the local library for
integrity issues — see [Managing the local library](library.md). Windows and Android projects both
compile from the same source and use the same library format.
