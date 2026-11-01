# Publishing Piston

## Tagged publishing

The release workflow validates the package and distribution on Linux, macOS and
Windows, attaches the `.nupkg` to GitHub Releases, and publishes `v*` tags to
NuGet.org using [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
Manual runs still require `publish_to_nuget: true` to publish; otherwise they only
validate and produce an artifact.

Before publishing, establish ownership of the `Piston` package ID on NuGet.org.
If an initial upload is needed to claim the ID, do it through the maintainer's
account; an empty search result is not proof of ownership. Create a NuGet
Trusted Publisher policy with repository owner `damianh`, repository `piston`,
workflow filename `release.yml` (not its full path), and environment `nuget`.
Configure that GitHub environment with appropriate protection rules and set the
repository/environment variable `NUGET_USER` to the owning NuGet profile name,
not an email address. No long-lived `NUGET_API_KEY` repository secret is used.
`NuGet/login` exchanges the job's GitHub OIDC token for a temporary push key.

Publishing errors, including duplicate versions, fail the workflow. After a
push, `smoke-public` waits for NuGet indexing with bounded retries, checks the
downloaded metadata, and exercises public-feed `dnx` over real stdio MCP.
If this verification fails after the package was pushed, rerun the failed
`verify-public` job rather than the `publish` job, which would push the same
version again.
The first public tagged release remains an acceptance gate until it actually
succeeds; local-feed validation alone is not proof of publication.
