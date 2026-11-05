#!/usr/bin/env bash
# Vercel build step: download OFAC's current SDN list, export it for the browser, then publish the site.
set -euo pipefail
cli=(/tmp/dotnet/dotnet run --project src/SanctionScreen.Cli -c Release -f net10.0 --)
"${cli[@]}" update --lists lists
mkdir -p site/SanctionScreen.Web/wwwroot/data
"${cli[@]}" export --lists lists --out site/SanctionScreen.Web/wwwroot/data/list.json
/tmp/dotnet/dotnet publish site/SanctionScreen.Web -c Release -o out
