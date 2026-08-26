# Optimum Privacy Policy

Last updated: 2026-08-26

This policy explains what information Optimum handles when you build, install, or run it. Optimum is an independent, client-side performance project for Vintage Story. The source code is available at [github.com/StratumServer/Optimum](https://github.com/StratumServer/Optimum).

This policy covers the Optimum source, build scripts, installers, launcher, and patcher. It does not cover Vintage Story, Anego Studios, GitHub, NuGet, Microsoft, or any other service used by a build tool or by the official game. Those services have their own policies and control the data they receive.

## Information handled locally

Optimum's build and installer scripts use local paths and settings to find a Vintage Story installation, choose a data directory, prepare a build workspace, and package the result. On Windows, the installer may inspect local Vintage Story settings to locate a data path with an existing game session. These values stay on the device and are not uploaded to the Optimum maintainers.

When you run the launcher, Optimum reads the local game files needed for patching and writes patched assembly copies, donor caches, compatibility reports, and other runtime state in the selected game or data directory. It also reads and writes the local `ModConfig/optimum.json` settings file. Optimum does not send worlds, mods, client settings, game binaries, account credentials, or other local file contents to the Optimum maintainers.

Optimum writes a launcher log at the selected data path under `Logs/optimum-launcher.log`. Installer and preflight steps may write additional build logs in their local temporary or build directories. These logs can contain timestamps, versions, patch counts, file names, paths, and diagnostic error text. Optimum does not transmit these logs automatically. Review them before attaching them to a public issue or sending them to another person.

## Requests to external services

Optimum's build and packaging workflows can make network requests when you run them:

- The official Vintage Story CDN may receive requests for the game archive or client package required for a local build or package.
- The public Anego Studios repositories listed in [`forks.json`](forks.json) may receive Git requests when the bootstrap process clones their pinned source revisions.
- The NuGet service at [api.nuget.org](https://api.nuget.org/v3/index.json) may receive package restore requests. The build also may install .NET tools from the configured package source.
- GitHub may receive requests for the Optimum repository, upstream source repositories, and optional packaging tools or prerequisite downloads selected by the user.
- The installer may open download pages for .NET, Git, PowerShell, innoextract, or other prerequisites when the user selects or follows those options.

These services can receive normal connection metadata such as an IP address, request time, and protocol or user-agent information. Optimum does not control their logging or retention. The official Vintage Story client is a separate process and may contact Anego Studios, authentication services, game servers, or other services using the account and installation configured by the user. That activity is governed by the [Vintage Story Privacy Policy](https://www.vintagestory.at/privacy/) and the relevant service policies.

## What Optimum does not collect

Optimum's own launcher, patcher, installers, and build scripts do not operate a project account service, advertising system, telemetry endpoint, analytics service, or crash-reporting service. Optimum does not sell personal information and does not upload local game files or diagnostic logs to a project server. Running the official Vintage Story client remains outside Optimum's control and outside this policy's scope.

## Retention and deletion

Local data remains on your device until you remove it. To remove Optimum data, close the game and launcher, then review and remove the Optimum installation directory, the `.optimum` cache and donor directories, `Logs/optimum-launcher.log`, the Optimum `ModConfig/optimum.json` file, and any build or temporary directories created by the scripts. Keep or remove the original Vintage Story installation and its account settings separately according to your own needs. Deleting Optimum does not delete the official game, worlds, mods, or other data outside the Optimum directories.

Optimum has no project server that stores account records or uploaded launcher data. External services retain data they receive according to their own policies.

## Security

The URLs configured by the project for source, package, and client downloads use HTTPS. Optimum does not manage Vintage Story passwords or session credentials. Protect the device, the local game installation, the build workspace, and any logs before sharing them.

## Changes to this policy

The maintainers may update this policy when Optimum's data handling changes. The date at the top of this page records the latest revision. The public repository and download documentation will link to the current version.

## Contact

For a general privacy question, open an issue in the [Optimum repository](https://github.com/StratumServer/Optimum/issues). Do not include passwords, session credentials, account identifiers, or private logs in a public post. Requests about data held by Vintage Story, GitHub, NuGet, or another external service must be directed to that service.

## Implementation references

The relevant data flows are visible in the public source: [build input configuration](forks.json), [NuGet source configuration](NuGet.config), [runtime launcher](Optimum.Launcher/Program.cs), [local launcher logging](Optimum.Launcher/Logger.cs), and [Windows installer](scripts/install-windows.ps1).
