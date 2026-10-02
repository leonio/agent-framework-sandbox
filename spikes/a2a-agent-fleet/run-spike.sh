#!/usr/bin/env bash
# Starts both team services, runs the platform-side client against them, then stops the services.
# Needs the .NET 10 SDK. No Docker, no model keys, no network beyond NuGet restore.
set -euo pipefail
cd "$(dirname "$0")"

export FLEET_TOKEN="${FLEET_TOKEN:-dev-token}"
mkdir -p .run

dotnet build FleetSpike.slnx -nologo -v:quiet

dotnet run --no-build --project src/TeamA.SecurityReviewer --urls http://localhost:5101 > .run/team-a.log 2>&1 &
A=$!
dotnet run --no-build --project src/TeamB.SecretScanner --urls http://localhost:5102 > .run/team-b.log 2>&1 &
B=$!
trap 'kill "$A" "$B" 2>/dev/null || true' EXIT

for port in 5101 5102; do
  for _ in $(seq 1 60); do
    curl -fsS "http://localhost:$port/healthz" > /dev/null 2>&1 && break
    sleep 0.5
  done
done

(cd src/Fleet.Client && dotnet run --no-build)

echo
echo "== 5. What the two services saw (trace ids should match the client's trace= values above) =="
grep -h "trace=" .run/team-a.log .run/team-b.log | sed -E 's/^.*(a2a [A-Z]+ [^ ]+ trace=[0-9a-f]+).*$/  \1/' | sort -u
