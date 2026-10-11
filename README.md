# MDBList Collections for Jellyfin

Mirrors any [MDBList](https://mdblist.com) list into a Jellyfin collection. Add a list, and a
scheduled task creates the collection and keeps its contents matching the list.

- Movies and shows are matched by IMDb, TMDb or TVDB id, so file names don't matter.
- The collection mirrors the list: items that leave the list leave the collection, and items you add
  to it by hand are removed on the next sync.
- A collection without a poster gets a collage of the list's top items. A poster you set yourself is
  never replaced. Delete it to get a fresh collage.
- If you set a collection name in the plugin, every sync applies it. If you leave it empty, the
  collection starts with the list's own name and keeps any rename you make in Jellyfin.
- If a fetch fails (bad list, rate limit, MDBList down), that list is skipped and its collection is left
  untouched. A failed fetch is never treated as an empty list.

Requires Jellyfin 12 and a free MDBList API key (mdblist.com → Preferences → API Access). The task
uses about two API calls per list per run.

## Install

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/rbcetin/jellyfin-plugin-mdblistcollections/main/manifest.json
```

Then install MDBList Collections from the catalogue and restart.

Or do it by hand: unzip the [release](https://github.com/rbcetin/jellyfin-plugin-mdblistcollections/releases)
into `<config>/plugins/MDBList Collections_<version>/` and restart.

## Setup

1. Dashboard → Plugins → MDBList Collections: enter the API key, then add lists. A list can be an
   mdblist.com list URL (`https://mdblist.com/lists/snoak/imdb-top-250-movies`), `user/slug`, or a
   numeric list id.
2. Saving the settings syncs right away. The task **Sync MDBList collections** also runs at startup and daily; run it from Dashboard →
   Scheduled Tasks to apply changes now.

## Build

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.MdbListCollections/Jellyfin.Plugin.MdbListCollections.csproj -c Release
```

Copy `bin/Release/net10.0/Jellyfin.Plugin.MdbListCollections.dll` into
`<config>/plugins/MDBList Collections_1.0.0.0/` and restart.

Written with AI assistance (Claude).

## License

MIT
