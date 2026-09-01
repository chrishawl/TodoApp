#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
codex_version=${CODEX_VERSION:-0.147.0}

docker build --target agent --build-arg "CODEX_VERSION=$codex_version" -t todoapp-eval-agent:local "$root"
docker build --target evaluator -t todoapp-eval-evaluator:local "$root"

docker image inspect --format 'agent={{.Id}}' todoapp-eval-agent:local
docker image inspect --format 'evaluator={{.Id}}' todoapp-eval-evaluator:local
