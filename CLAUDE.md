# BFLWMS — Working Conventions

## Branch/PR workflow

For each distinct task or feature requested (not every chat message), work on its own branch and open a PR:

1. Create a feature branch off `main` (name it like existing history: `feat/<short-desc>`, `fix/<short-desc>`).
2. Commit the change(s) for that task with a conventional commit message matching existing style, e.g. `feat(reports): add X`, `fix(fsmrr): correct Y`.
3. Bump the version in [src/Wms.Web/Wms.Web.csproj](src/Wms.Web/Wms.Web.csproj) (`<Version>1.0.x</Version>`, patch increment) as part of the commit — note CI overrides this at build time with `-p:Version=1.0.${{ github.run_number }}`, so the manual bump is for local/dev visibility only, not the source of truth.
4. Push the branch and open a PR with `gh pr create` — **always confirm with the user before pushing or opening the PR**, per standard practice for actions visible to others.

Do not push or open PRs automatically without asking each time, even though the branch/PR-per-task convention itself is standing.
