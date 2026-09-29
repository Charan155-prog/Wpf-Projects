#!/usr/bin/env bash
set -euo pipefail
source "$HOME/.sail-sam3.env"
exec "$HOME/sail/venv/bin/python" "$(dirname "$0")/run_annotation.py" "$@"