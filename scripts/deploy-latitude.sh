#!/usr/bin/env bash
# Publishes MyBibleApp.Desktop for linux-x64, syncs it to the touchscreen test device (the
# Ubuntu Latitude) over scp, killing any already-running instance first (so the binary isn't
# busy while it's overwritten) and launching the fresh copy afterward.
#
# Usage:
#   scripts/deploy-latitude.sh
#
# Config (override via env vars if your setup differs):
#   LATITUDE_HOST   default: jeremy@jeremy-latitude
#   LATITUDE_PATH   default: ~/apps/MyBibleApp
#   DISPLAY_TARGET  default: :0   (X11/XWayland display on the Latitude)

set -euo pipefail

LATITUDE_HOST="${LATITUDE_HOST:-jeremy@jeremy-latitude}"
LATITUDE_PATH="${LATITUDE_PATH:-~/apps/MyBibleApp}"
DISPLAY_TARGET="${DISPLAY_TARGET:-:0}"
APP_NAME="MyBibleApp.Desktop"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PUBLISH_DIR="$REPO_ROOT/publish/latitude-linux"

echo "==> Stopping any running $APP_NAME on $LATITUDE_HOST"
# pkill -f matches the FULL command line of every process, including the shell sshd spawns to
# run this very pkill call — since that shell's own argv literally contains the app name as
# plain text (it's right here in the command being sent), a plain pattern matches pkill's own
# invoking session and kills the SSH connection out from under itself. Bracketing the first
# character ([M]yBibleApp.Desktop) is the standard fix: it still matches the real process's
# command line as a substring, but never matches this invocation's own literal "[M]..." text.
PKILL_PATTERN="[${APP_NAME:0:1}]${APP_NAME:1}"
ssh "$LATITUDE_HOST" "pkill -f '$PKILL_PATTERN' 2>/dev/null; sleep 1; true"

echo "==> Publishing $APP_NAME (linux-x64, self-contained)"
dotnet publish "$REPO_ROOT/MyBibleApp.Desktop/MyBibleApp.Desktop.csproj" \
    -c Debug -r linux-x64 --self-contained true -o "$PUBLISH_DIR"

echo "==> Syncing to $LATITUDE_HOST:$LATITUDE_PATH"
ssh "$LATITUDE_HOST" "mkdir -p $LATITUDE_PATH"
scp -r "$PUBLISH_DIR"/. "$LATITUDE_HOST:$LATITUDE_PATH/"
ssh "$LATITUDE_HOST" "chmod +x $LATITUDE_PATH/$APP_NAME"

echo "==> Launching on the Latitude's display ($DISPLAY_TARGET)"
# nohup + backgrounding + redirected output so this ssh call returns as soon as the process is
# launched, instead of blocking here for as long as the GUI app stays open.
ssh "$LATITUDE_HOST" \
    "DISPLAY=$DISPLAY_TARGET nohup $LATITUDE_PATH/$APP_NAME > $LATITUDE_PATH/app.log 2>&1 & true"

echo "==> Done. Remote log: $LATITUDE_HOST:$LATITUDE_PATH/app.log"
