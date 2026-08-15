# jellyfin-plugin-leaving-soon

A Jellyfin plugin that surfaces **scheduled-deletion media** ("leaving soon") as
symlink-backed libraries, e.g. `Movies - Leaving Soon` and `Shows - Leaving Soon`.

The plugin uses a **provider-pull model**: it polls one or more configured provider
apps for media that is scheduled for deletion, then manages the symlinks, the
Jellyfin virtual folders, and the library refreshes itself. No push integration
is needed on the provider side.

## Supported providers

| Provider | Endpoint polled | Notes |
|---|---|---|
| Maintainerr | `GET /api/collections/leaving-soon` | New endpoint added alongside `overlay-data` |
| OxiCleanarr | `GET /api/media/leaving-soon` | Normalized leaving-soon contract |

Both return the same normalized contract:

```jsonc
{
  "items": [
    {
      "mediaServerId": "jellyfin-item-guid",
      "type": "movie" | "show",
      "title": "The Matrix",
      "deletionDate": "2026-09-01T00:00:00Z"
    }
  ]
}
```

The plugin resolves each item's on-disk path from Jellyfin's own metadata
(`ILibraryManager.GetItemById`), so providers do not need to expose file paths.

## How it works

1. On the configured interval (`SyncIntervalMinutes`, default 15) the plugin polls
   every enabled provider.
2. Items are deduped by `mediaServerId`; a provider that fails contributes nothing
   and is logged (it never aborts the sync).
3. Movies and shows are reconciled separately:
   - ensure the host directory exists under `BasePath`,
   - ensure the Jellyfin virtual folder exists (create or add path),
   - create symlinks for newly-scheduled items,
   - remove stale symlinks no longer scheduled,
   - trigger a library refresh,
   - regenerate the library cover so it reflects the current leaving-soon set.
4. `HideWhenEmpty`: when a library has zero items it is **disabled** (hidden from all
   user views) instead of deleted — its metadata rows and symlinks are kept intact, so
   re-enabling on the next sync with items is instant with zero rescan. When the library
   comes back from the empty period its cover is force-regenerated so the previous leaving
   set's collage does not linger.

## Uninstall

Uninstalling the plugin cleans up after itself:

- removes every symlink it created under `<BasePath>/movies` and `<BasePath>/tv`,
  and deletes those subdirectories once empty (never recursive — real content or a real
  library at the base path always survives),
- disables and removes the leaving-soon libraries, purging their orphaned metadata rows.

Cleanup is guarded: a library is only removed when every one of its locations points
under the plugin's own base path, so a real (admin-created) library that happens to share
a configured name is never touched.

## Configuration

Edit `config.xml` in the plugin's config directory and restart Jellyfin.

| Setting | Default | Description |
|---|---|---|
| `BasePath` | `/config/leaving-soon` | Host directory for symlinks; `movies/` and `tv/` subdirectories. Defaults to the Jellyfin config volume so the container user can always write it (no chown needed) |
| `MoviesLibraryName` | `Movies - Leaving Soon` | Jellyfin library name for movies |
| `TvLibraryName` | `Shows - Leaving Soon` | Jellyfin library name for TV |
| `HideWhenEmpty` | `true` | Hide empty leaving-soon libraries from the sidebar instead of showing them empty |
| `SyncIntervalMinutes` | `15` | Poll interval |
| `ForceEmptyAfterFailureCount` | `3` | Consecutive provider failures tolerated before an empty result is trusted |
| `Providers` | `[]` | List of provider configs (`Type`, `Name`, `Enabled`, `Url`, `ApiKey`, `IncludeCollections`) |

Example provider configs:

```xml
<Providers>
  <ProviderConfig>
    <Type>maintainerr</Type>
    <Name>maintainerr</Name>
    <Enabled>true</Enabled>
    <Url>http://maintainerr:6246</Url>
    <ApiKey></ApiKey>
    <IncludeCollections></IncludeCollections>
  </ProviderConfig>
  <ProviderConfig>
    <Type>oxicleanarr</Type>
    <Name>oxicleanarr</Name>
    <Enabled>true</Enabled>
    <Url>http://oxicleanarr:9709</Url>
    <ApiKey></ApiKey>
    <IncludeCollections></IncludeCollections>
  </ProviderConfig>
</Providers>
```

Note on auth:
- Maintainerr's collections API currently has no enforced auth.
- OxiCleanarr's `/api/media/leaving-soon` sits behind JWT auth; the plugin works with
  `admin.disable_auth: true`, or you may add API-key support later and send it via
  `ApiKey` (sent as a Bearer token).

## API

- `GET /api/leaving-soon/status` - plugin status and configuration summary (admin auth)
- `GET /api/leaving-soon/debug` - diagnostic snapshot of the current sync inputs: configured
  providers and per-item path resolution (admin auth). Helps trace why a sync reconciled to
  an empty library.
- `POST /api/leaving-soon/sync` - trigger an immediate sync (admin auth)
- `POST /api/leaving-soon/test-connection` - test a provider's URL/auth against its
  leaving-soon endpoint (admin auth). Accepts unsaved provider settings
  (`Type`, `Name`, `Url`, `ApiKey`, `IncludeCollections`); the config page's
  per-provider **Test** button uses it.

## Docker

The default `BasePath` is `/config/leaving-soon` (inside the Jellyfin config volume),
so the container user can always create the symlink directories — no permission setup
needed. If you prefer the leaving-soon libraries to live on the media mount instead,
set `BasePath` to e.g. `/data/leaving-soon` and pre-create it with the container's
UID/GID:

```sh
mkdir -p /data/leaving-soon/movies /data/leaving-soon/tv
chown -R 1000:1000 /data/leaving-soon
```

## Building

```bash
./build.sh 1.0.0
```

The plugin DLL is produced under `build/`; a release zip and md5 are generated at
the repo root.

## Repository

https://github.com/ramonskie/jellyfin-plugin-leaving-soon
