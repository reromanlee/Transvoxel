### Installation

Every way to add Transvoxel to a project, and what each one means for updates. The package lives in the repository's [`UnityPackage/`](../UnityPackage) folder.

### Git URL

Open **Window ▸ Package Manager ▸ + ▸ Install package from git URL** and enter:

```
https://github.com/reromanlee/Transvoxel.git?path=/UnityPackage
```

Or add it to `Packages/manifest.json` by hand:

```json
{
  "dependencies": {
    "com.reromanlee.transvoxel": "https://github.com/reromanlee/Transvoxel.git?path=/UnityPackage"
  }
}
```

1. **The `?path=` part is required.** Without it the Package Manager finds no `package.json` at the repository root and refuses the URL.
2. **This tracks `main`.** For anything you intend to ship, pin a release tag from the [releases page](https://github.com/reromanlee/Transvoxel/releases). Tags are the bare version with no `v` prefix, and the `#tag` always goes last: `https://github.com/reromanlee/Transvoxel.git?path=/UnityPackage#<version>`.
3. **Releases up to 2.0.0 keep the package at the repository root**, so pin them without `?path=`: `https://github.com/reromanlee/Transvoxel.git#2.0.0`.
4. **Unity needs Git 2.14.0 or newer on your `PATH`.** "No 'git' executable was found" in the Package Manager means exactly that.
5. **Nothing updates on its own.** An unpinned URL gets an **Update** button that re-resolves to the latest commit on `main`; a pinned tag stays put until you change it.
6. **Git installs are read-only.** To change the package, install it from a local clone instead.

### Release tarball

Every [release](https://github.com/reromanlee/Transvoxel/releases) attaches `com.reromanlee.transvoxel-<version>.tgz`, about 0.8 MB. No Git is needed, and the version is frozen.

1. Download the `.tgz` from the release.
2. Put it inside your project, for example in the `Packages/` folder next to `manifest.json`, so the path stays valid for everyone who clones the project.
3. Open **Window ▸ Package Manager ▸ + ▸ Install package from tarball** and pick the file.

Unity records a `file:` path to the archive, relative to the project's `Packages` folder:

```json
{
  "dependencies": {
    "com.reromanlee.transvoxel": "file:com.reromanlee.transvoxel-2.0.0.tgz"
  }
}
```

**Keep the `.tgz` where it is.** Unity reads it whenever it resolves packages, so moving or deleting it breaks the project, and an absolute path outside the project works only on your machine.

### Local clone

For working on the package, point a `file:` path at the clone's `UnityPackage/` folder, and your edits are live with no reinstall between changes:

```json
{
  "dependencies": {
    "com.reromanlee.transvoxel": "file:../../Transvoxel/UnityPackage"
  }
}
```

The path is relative to the project's `Packages` folder and must end at the folder holding `package.json`, not at the repository root. Unity writes `.meta` files into the clone, which is what you want when working on the package.

### After installing

1. **The demo is a sample.** Import it from **Window ▸ Package Manager ▸ Transvoxel ▸ Samples**; see [USAGE.md](USAGE.md#interactive-demo).
2. **Tests are opt-in.** Add the package to `testables` to run them; see [TESTING.md](TESTING.md).
