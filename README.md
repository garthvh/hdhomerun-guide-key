# HDHomeRun Guide Auth Sync

A Jellyfin plugin that keeps the HDHomeRun XMLTV guide `DeviceAuth` key in sync
with Jellyfin's Live TV listings provider.

Licensed under [GPL-3.0](LICENSE).

## The problem

HDHomeRun tuners (CONNECT, QUATRO, etc.) expose a `DeviceAuth` token via their
`/discover.json` endpoint. Jellyfin's built-in XMLTV listings provider uses that
token as a query parameter on `https://api.hdhomerun.com/api/xmltv?DeviceAuth=...`
to pull guide data. That token isn't permanently fixed — it can rotate (e.g.
after a firmware update or device re-registration) — and when it does, the
listings provider URL configured in Jellyfin goes stale silently: guide data
stops updating with no obvious error in the UI.

This plugin closes that gap by periodically re-checking the tuner and rewriting
the listings provider URL whenever the key changes.

## How it works

`UpdateDeviceAuthTask` (registered as the scheduled task **Sync HDHomeRun Guide
Auth Key**, category **Live TV**) runs every 4 hours by default and:

1. Builds a list of tuner base URLs to check:
   - Every tuner host already configured under **Live TV → Tuner Devices** of
     type `hdhomerun` (if `UseConfiguredTunerHosts` is enabled — the default).
   - Plus any URLs from the plugin's own `ManualTunerUrls` setting, for tuners
     you haven't added as a Jellyfin tuner device.
2. Queries `http://<tuner>/discover.json` for each and reads the `DeviceAuth`
   field.
3. If every tuner reports the same `DeviceAuth` value, it rewrites the `Path`
   of any existing `xmltv` listings provider pointed at `api.hdhomerun.com` to
   use the current key (creating one if none exists yet), then saves Jellyfin's
   `livetv` configuration.
4. If the key actually changed (or a provider was just created), it also queues
   Jellyfin's built-in **Refresh Guide** task, so channel/program data gets
   pulled with the new key immediately instead of waiting for Refresh Guide's
   own schedule. A run where nothing changed does not queue a refresh.
5. Safety checks: if no tuner responds, or if tuners disagree on the key
   (multiple physical devices with different keys), it logs a warning and
   leaves the configuration untouched rather than guessing.

Source: [UpdateDeviceAuthTask.cs](src/HDHomeRunAuthPlugin/UpdateDeviceAuthTask.cs),
[Plugin.cs](src/HDHomeRunAuthPlugin/Plugin.cs),
[PluginConfiguration.cs](src/HDHomeRunAuthPlugin/PluginConfiguration.cs).

## Configuration

Dashboard → Plugins → HDHomeRun Guide Auth Sync:

| Setting | Default | Purpose |
|---|---|---|
| Check tuner hosts already configured under Live TV → Tuner Devices | on | Reuses the tuner URLs Jellyfin already knows about. |
| Additional tuner base URLs (comma-separated) | empty | For tuners not registered as a Jellyfin Live TV tuner device, e.g. `http://192.168.1.137`. |

You can also run the task on demand from Dashboard → Scheduled Tasks → Live TV
→ **Sync HDHomeRun Guide Auth Key** → run now.

The 4-hour interval comes from `UpdateDeviceAuthTask.GetDefaultTriggers()` and is
only applied when Jellyfin has no saved trigger override for this task. If you
manually edit the schedule from Dashboard → Scheduled Tasks, that override
persists and a future code change to the default interval won't take effect
until the override is cleared.

## Building

```bash
cd src/HDHomeRunAuthPlugin
dotnet build -c Release
```

Output: `src/HDHomeRunAuthPlugin/bin/Release/net9.0/HDHomeRunAuthPlugin.dll`

Requires the .NET 9 SDK and `Jellyfin.Controller` 10.11.11 (restored via NuGet,
see [HDHomeRunAuthPlugin.csproj](src/HDHomeRunAuthPlugin/HDHomeRunAuthPlugin.csproj)).

## Installing

### Via Plugin Repository (recommended)

Add this repository URL in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/garthvh/hdhomerun-guide-key/main/manifest.json
```

Then install **HDHomeRun Guide Auth Sync** from the **Dashboard → Plugins**
catalog. Updates will appear automatically when new versions are released.

### Manual

Copy the built DLL into Jellyfin's plugin directory inside a versioned
subfolder (the version comes from `<Version>` in
[HDHomeRunAuthPlugin.csproj](src/HDHomeRunAuthPlugin/HDHomeRunAuthPlugin.csproj)):

```bash
mkdir -p /config/plugins/HDHomeRunAuthPlugin_<version>
cp HDHomeRunAuthPlugin.dll /config/plugins/HDHomeRunAuthPlugin_<version>/
```

Remove any older version's folder so Jellyfin doesn't load two copies of the
same plugin GUID, then restart the Jellyfin server (or recreate the pod if
Jellyfin runs in a container).

If you change `UpdateDeviceAuthTask.GetDefaultTriggers()` (e.g. the sync
interval), the new default takes effect on restart — as long as nobody has
manually edited the schedule in Dashboard → Scheduled Tasks. A saved override
wins and must be cleared by hand.

## Verifying

- **Dashboard → Plugins** should list "HDHomeRun Guide Auth Sync" at the
  deployed version.
- **Dashboard → Scheduled Tasks → Live TV** should show **Sync HDHomeRun Guide
  Auth Key** with the expected interval.
- Logs should contain lines like:
  ```
  HDHomeRunAuthPlugin.UpdateDeviceAuthTask: Updated HDHomeRun XMLTV listings provider guide auth key.
  ```
  A no-op run instead logs that no change was needed.
- When the key changes, the task also queues Jellyfin's built-in **Refresh
  Guide** task (`Queued the Refresh Guide task to pick up the new guide auth
  key.`) so channels/programs are fetched immediately.
- Confirm the key matches the tuner:
  ```bash
  curl -s http://<tuner-ip>/discover.json | grep -o '"DeviceAuth":"[^"]*"'
  ```
  The value should match the `DeviceAuth=` parameter in your XMLTV listings
  provider URL (check via Jellyfin's API or Dashboard → Live TV → Data Sources).

## Repo layout

```
.github/workflows/release.yml    # CI: build, release zip, update manifest
src/HDHomeRunAuthPlugin/
  Plugin.cs                       # plugin metadata, registers the config page
  PluginConfiguration.cs          # persisted settings (tuner URLs, toggle)
  PluginServiceRegistrator.cs     # DI registration (adds IHttpClientFactory)
  UpdateDeviceAuthTask.cs         # the scheduled task itself
  configPage.html                 # Dashboard settings page
  meta.json                       # plugin catalog metadata (name, guid, version)
  build.yaml                      # CI/build descriptor
manifest.json                     # Jellyfin plugin repository manifest (auto-updated)
```
