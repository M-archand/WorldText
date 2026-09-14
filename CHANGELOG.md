# Changelog

## [1.0.3](https://github.com/M-archand/WallText/compare/1.0.2...v1.0.3) (2026-09-13)


### Bug Fixes

* **spawning:** abort stale text loads, guard every deferred spawn behind the load generation, drop a redundant deferral, and stop the redundant round-start reload, so map changes and reloads no longer produce duplicate text ([1d9606b](https://github.com/M-archand/WallText/commit/1d9606b20daefdb579b1f00c9ad1264033117cf5)) ([55e6dce](https://github.com/M-archand/WallText/commit/55e6dce2d15b9694e0c6985d814dfb0e3adeda35)) ([e3ebeb3](https://github.com/M-archand/WallText/commit/e3ebeb317942442a27c3509ec325d5eb2bef07f7)) ([39dc312](https://github.com/M-archand/WallText/commit/39dc312713c12f3c12c98787ba76ef79a3f6a975))
* **database:** rebuild the connection string when the config is reloaded, refresh the move menu text only after the update completes, and log and report failed saves in chat instead of discarding them ([8283cbd](https://github.com/M-archand/WallText/commit/8283cbd1ccb9dde9ff392528a43fa11aa515bc60)) ([f834a8f](https://github.com/M-archand/WallText/commit/f834a8fc842a20e53da4db9acbe8b5fecd864523)) ([2eb67f3](https://github.com/M-archand/WallText/commit/2eb67f367215fb2c616f7bb8b1bc7d279385b9b1))
* **shared api:** resolve the K4-WorldText-API through `TryGetSharedApi` so a missing API no longer throws, and tolerate stale K4 ids after map end so reload and remove no longer throw ([77b470c](https://github.com/M-archand/WallText/commit/77b470ca431c80d33d8b86cd0a1411b8644e1a6c)) ([e59155c](https://github.com/M-archand/WallText/commit/e59155c7be65f9b6f197db5c32ae7e433b02395e))
* revalidate players by slot where they can go stale across deferred and async work ([539322d](https://github.com/M-archand/WallText/commit/539322dadeb3f52ea19579b12b2e083ff19c47b3))


### Performance Improvements

* **threading:** move `maps/*.json` reads and writes off the game thread, and batch world text inserts off the main thread during `!importtext` ([677f088](https://github.com/M-archand/WallText/commit/677f0885249aa851a45afa6b783ded63db7f6df4)) ([b867621](https://github.com/M-archand/WallText/commit/b8676210b9c0d4d0693eba4fb6dfca97043d7d12))
* teleport only the selected text instead of every placement when moving ([742e891](https://github.com/M-archand/WallText/commit/742e891e109aca106c8a1f9f2769fd25d1262735))
* **database:** run `CREATE TABLE` once instead of on every refresh ([a2e8c4e](https://github.com/M-archand/WallText/commit/a2e8c4e400265f7c25b75070ce9a325740609d1c))


### Code Refactoring

* consolidate placement parsing into a single invariant format shared by the JSON files and the database, which still reads the legacy `Vector.ToString()` style so existing placements keep working ([3bdb907](https://github.com/M-archand/WallText/commit/3bdb9074a4281bff36fb99345f3b885683d35bf3))
