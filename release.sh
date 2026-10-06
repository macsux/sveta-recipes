#!/usr/bin/env bash
# Builds Sveta's Recipes for Windows with Velopack.
#
#   ./release.sh <version> [--upload] [--seed <SRD_data.mdb> <SRD_2018_u.mdb>]
#
#   (always)  tests, publishes win-x64 and packs the PUBLIC release into Releases/ — no data in it.
#   --upload  publishes that release on GitHub (macsux/sveta-recipes); installed apps update from there.
#   --seed    also builds the HANDOFF installer: the same version with her data imported from the Access files
#             as seed/recipes.db (used only on first start, never overwrites). Output: publish/handoff/.
#             This one contains customer data — hand it over privately; never upload or commit it.
#
# Needs: .NET 10 SDK, vpk (dotnet tool install -g vpk), gh (logged in) for --upload, mdbtools for --seed,
#        Bolt.Theme at ../aibolt/src/Bolt.Theme.
set -euo pipefail
cd "$(dirname "$0")"

version=${1:?usage: ./release.sh <version> [--upload] [--seed SRD_data.mdb SRD_2018_u.mdb]}
shift
upload=false; seed_data=""; seed_front=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --upload) upload=true; shift ;;
    --seed) seed_data=${2:?SRD_data.mdb}; seed_front=${3:?SRD_2018_u.mdb}; shift 3 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

repo=https://github.com/macsux/sveta-recipes
vpk=${VPK:-$HOME/.dotnet/tools/vpk}
pack=(--packId SvetaRecipes --packVersion "$version" --packTitle "Sveta's Recipes" --packAuthors macsux
      --mainExe SvetaRecipes.exe --channel win --delta None)
# --delta None: her installed copy started from the seeded package, so a delta against the public package would not
# apply; full packages always do.

dotnet test --project tests/SvetaRecipes.Core.Tests

rm -rf publish/win-x64
dotnet publish src/SvetaRecipes.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true \
  -p:Version="$version" -o publish/win-x64

"$vpk" "[win]" pack "${pack[@]}" --packDir publish/win-x64 --outputDir Releases
echo "public release: Releases/"

if $upload; then
  "$vpk" "[win]" upload github --outputDir Releases --channel win --repoUrl "$repo" --token "$(gh auth token)" \
    --publish --tag "v$version" --releaseName "Sveta's Recipes $version"
  echo "uploaded to $repo/releases/tag/v$version"
fi

if [[ -n "$seed_data" ]]; then
  rm -rf publish/win-x64-seeded publish/handoff-pack
  cp -R publish/win-x64 publish/win-x64-seeded
  mkdir -p publish/win-x64-seeded/seed
  dotnet run --project tools/SvetaRecipes.Import -- "$seed_data" "$seed_front" publish/win-x64-seeded/seed/recipes.db --force
  "$vpk" "[win]" pack "${pack[@]}" --packDir publish/win-x64-seeded --outputDir publish/handoff-pack
  mkdir -p publish/handoff
  cp publish/handoff-pack/SvetaRecipes-win-Setup.exe "publish/handoff/Sveta's Recipes Setup $version.exe"
  echo "handoff installer (contains her data — keep private): publish/handoff/Sveta's Recipes Setup $version.exe"
fi
