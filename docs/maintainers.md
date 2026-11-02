# Publishing Piston

## Calendar versions

Published versions use `YYYY.MDD.N`. The first component is the UTC year, the
second is the numeric month followed by the two-digit day, and the final
component is a daily counter starting at 1. For example, `2026.101.1-dev` is a
January 1 dev package, and `2026.1101.2` is a November 1 stable package. NuGet
normalizes leading zeroes, so January 1 is `101`, not `0101`. These versions
identify releases, not SemVer compatibility.

Dev and stable packages share the daily counter. Each version receives an
annotated `v`-prefixed Git tag recording its source commit and workflow run.
The tag also records the last verified reservation, including any explicitly
retired reservation, allowing later releases to validate the history without
rechecking every older disposition.
The date is fixed when the tag is allocated, even if publishing or retrying
crosses midnight UTC. An untagged local build uses `0.0.0-local`.

## Publishing

A successful `main` push CI run starts a dev publication for that exact tested
commit, including when `main` has moved on. It creates a `-dev` tag and publishes
a NuGet prerelease, but does not create a GitHub Release. Consumers must request
prereleases explicitly. CI runs that fail or are cancelled do not publish.

To ship a stable release, dispatch **Release tool** on `main` with
`publish_to_nuget` enabled. The workflow tests the selected commit, creates its
calendar tag, publishes the package, verifies it on NuGet.org, and attaches it
to a GitHub Release. Pushing a tag by hand does not publish. A manual run with
`publish_to_nuget` disabled only validates a candidate package and uploads an
artifact; it does not reserve a version, tag, or publish.

Both paths use [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

Before publishing, establish ownership of the `DamianH.Piston` package ID on NuGet.org.
If an initial upload is needed to claim the ID, do it through the maintainer's
account; an empty search result is not proof of ownership. Create a NuGet
Trusted Publisher policy with repository owner `damianh`, repository `piston`,
workflow filename `release.yml` (not its full path), and environment `nuget`.
Configure that GitHub environment so dev publications can run automatically,
and set the repository/environment variable `NUGET_USER` to the owning NuGet
profile name, not an email address. No long-lived `NUGET_API_KEY` repository
secret is used.
`NuGet/login` exchanges the job's GitHub OIDC token for a temporary push key.
The failed first publication reported a policy expecting `release.yaml`;
ensure the NuGet policy instead names the actual workflow, `release.yml`.

## Recovery

Publications are serialized. If a release fails after reserving its tag, later
publications fail with the original workflow run ID until it succeeds. Fix the
cause and rerun that run's failed jobs; the workflow reuses the same tag,
version, and source commit. If the package was already pushed, it compares the
public package with the workflow artifact before resuming verification rather
than blindly pushing a duplicate. Once it succeeds, rerun any later workflow
runs that were blocked. Do not delete, move, or reuse the reserved tag.

`smoke-public` waits for NuGet indexing with bounded retries, checks downloaded
metadata, and exercises public-feed `dnx` over real stdio MCP. If an old workflow
run has expired, the next publication verifies its public package and, for a
stable version, the GitHub Release asset against the tag, commit, and NuGet
package; if that evidence is unavailable, publishing remains blocked for
maintainer investigation.
GitHub Actions permits at most 100 pending runs in the release concurrency
queue; overflow is cancelled and must be rerun after capacity is available.

The sole exception is the unpublished `Piston` reservation
`v2026.1101.1-dev` from run `41339003060`. Release tooling retires it only
after verifying its exact tag, failed run and absent package/release evidence;
the tag and version remain reserved. **Do not rerun this retired workflow**:
its original source and tooling still target the old package ID.

The first public release remains an acceptance gate until it actually succeeds;
local-feed validation alone is not proof of publication.
