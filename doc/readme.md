# CodeAlta User Guide

An agentic AI coding CLI assistant developed in .NET.

## Infrastructure Status

Current infrastructure-first progress includes workspace bootstrapping primitives:

- `CodeAlta.Workspaces`: workspace/project descriptors, machine override profiles, catalog loading.
- Scope resolution (`global`, `workspace`, `project`) into concrete checkout and `.codealta` roots.
- Checkout planning (`clone` vs `update`) without network side effects.
- `CodeAlta.Persistence`: SQLite migrations, task/artifact/agent repositories, and markdown artifact store.

## Workspace Descriptor Layout

Global repository layout (implemented reader support):

- `workspaces/<workspaceKey>/workspace.yaml`
- `workspaces/<workspaceKey>/projects/*.yaml`
- `machines/<machineId>.yaml`

The YAML model uses UUID v7 strings for workspace/project `id` values and validates
workspace/project keys using `^[a-z0-9][a-z0-9\\-_.]{1,63}$`.

## Persistence Model

The persistence layer currently provides:

- SQLite schema bootstrap with `schema_version` migration tracking.
- Durable tables for `tasks`, `task_events`, `artifacts`, `artifact_links`, `agents`, and `agent_sessions`.
- Search foundation tables: `documents`, `documents_fts` (FTS5), and `document_embeddings`.
- Markdown artifact read/write with YAML frontmatter (`ArtifactStore`) and plain-text extraction for indexing.

## Indexing and Search

Current search infrastructure (`CodeAlta.Search`) includes:

- `IndexingQueue` + `Indexer` for background-capable indexing jobs.
- `DocumentIndexStore` to upsert documents, maintain FTS rows, and persist embeddings.
- `SearchService` with:
  - FTS query mode
  - hybrid mode (FTS prefilter + vector rerank).
- A deterministic local `HashEmbedder` for tests and offline indexing.
- `LlamaSharpEmbedder` for local GGUF-based embeddings when a model path is configured.
