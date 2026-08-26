# BFLWMS — Working Conventions

## Branch/PR workflow

For each distinct task or feature requested (not every chat message), work on its own branch and open a PR:

1. Create a feature branch off `main` (name it like existing history: `feat/<short-desc>`, `fix/<short-desc>`).
2. Commit the change(s) for that task with a conventional commit message matching existing style, e.g. `feat(reports): add X`, `fix(fsmrr): correct Y`.
3. Bump the version in [src/Wms.Web/Wms.Web.csproj](src/Wms.Web/Wms.Web.csproj) (`<Version>1.0.x</Version>`, patch increment) as part of the commit — note CI overrides this at build time with `-p:Version=1.0.${{ github.run_number }}`, so the manual bump is for local/dev visibility only, not the source of truth.
4. Build before committing, and build again **after** the version bump — a green build taken before editing the csproj proves nothing (a malformed `<Version>` fails the build, and that has killed a deploy).
5. Push the branch and open a PR with `gh pr create` — **always confirm with the user before pushing or opening the PR**, per standard practice for actions visible to others.
6. Once the PR is open, carry it through **without further confirmation**: wait for the `Build & verify` check, `gh pr merge --squash --delete-branch`, then verify the "Build and deploy" run succeeded.

Do not push or open PRs automatically without asking each time, even though the branch/PR-per-task convention itself is standing. The confirmation gate is on the **push/PR** — making the work visible. Everything after that is mechanical and pre-authorised.

## After a merge

- Report the deployed version as `v1.0.<run_number>` from the "Build and deploy" workflow run, so a deploy can be traced back to the change. CI stamps this over the csproj value.
- `main` moves fast and several PRs are often open at once. Expect the second one to merge to need a rebase; the conflict is almost always just the `<Version>` line. Rebase, resolve to the higher number, rebuild, force-push with `--force-with-lease`.
- Auto-merge is **not** enabled on this repo — `gh pr merge --auto` fails. Wait for checks with `gh pr checks <n> --watch`, then merge.
