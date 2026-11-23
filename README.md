# Reidentify by TMDB ID (Jellyfin 12.x plugin)

Re-runs **Identify** on each movie and series using the TMDB ID already stored on the item,
**once per item**. It does what the Identify dialog does when you pick a result: keep only the
TMDB ID, drop the old metadata, and fetch everything again in the library's preferred metadata
language. Seasons and episodes are refreshed through their series.

Useful when media arrives with metadata in the wrong language but a correct TMDB ID.

## How it works

- **New items:** when a library scan adds a movie or series, it is re-identified once the scan
  has finished.
- **Existing library:** run **Dashboard > Scheduled Tasks > Library > Re-identify media by TMDB
  ID** by hand. It has no schedule.
- **Once only:** every item that was re-identified is recorded, and never processed again. An
  item that is deleted and later re-added is processed again.
- Locked items and items without a TMDB ID are left alone.

## Install

In Jellyfin: **Dashboard > Plugins > Repositories > add**

    https://raw.githubusercontent.com/20015jjw/jellyfin-plugin-reidentify-by-tmdb/main/manifest.json

Then install **Reidentify by TMDB ID** from the catalog and restart Jellyfin.

## Settings

On the plugin's settings page:

- **Re-identify new items automatically** (default on)
- **Replace images** (default on): when off, only text metadata is replaced
- **Dry run** (default off): only log what would be re-identified
- **Libraries**: which libraries to process; none ticked means all
- **Status**: how many items are done, and a button to forget them all
- **Activity log**: the last 500 things the plugin did

## Before the first run

- Set each library's **Preferred download language** (and country); that is the language TMDB
  is asked for.
- If items have `.nfo` files and the Nfo metadata reader is enabled for the library, the NFO
  text wins over TMDB. Disable the Nfo reader for that library first.
- Try **Dry run** first and read the activity log.

## Build and release

Needs the .NET 10 SDK.

    dotnet build -c Release
    ./release.sh 1.2.0.0 "What changed"

`release.sh` builds, publishes a GitHub release with the zip, and adds the version to
`manifest.json`.
