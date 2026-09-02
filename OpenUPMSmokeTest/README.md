# OpenUPM smoke test

This standalone Unity project verifies that OpenUPM can resolve and download
Svelto.Tasks and Svelto.Common without using the embedded packages in the
neighbouring `MillionPoints` project.

## Pinned package versions

- `com.sebaslab.svelto.tasks` `2.0.0-preview.2`
- `com.sebaslab.svelto.common` `3.7.1`

Svelto.Tasks `2.0.0-preview.2` declares Svelto.Common `3.7.1` as its
dependency.

## Verification

1. Open this directory as a Unity project.
2. Wait for Package Manager to finish resolving dependencies.
3. Confirm `Packages/packages-lock.json` records both Svelto packages with
   `"source": "registry"` and `"url": "https://package.openupm.com"`.
4. Confirm the Unity Console has no compilation errors. `Assets/Scripts/OpenUpmPackageSmokeTest.cs`
   imports both namespaces, so compilation proves both packages are available
   to project scripts.

This project deliberately contains no scene or runtime behaviour; package
resolution and compilation are the smoke test.
