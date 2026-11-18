#!/usr/bin/env bash
# GitSync server deployment with podman (API container + nginx web container in one pod).
#
#   ./deploy/deploy.sh [options]
#
#   -n NAME     image/pod name prefix           (default: gitsync)  → images NAME-api, NAME-web, pod NAME-pod
#   -t TAG      image tag                       (default: latest)
#   -p PORT     host port for the web UI        (default: 8080)
#   -d DIR      host data directory             (default: ~/.local/share/NAME)
#   -k KEY      access key for the web UI / API (default: generated and saved in DIR/access-key)
#   -H HOST     deploy to a remote server over ssh (user@host). Source is copied and built THERE.
#   -r DIR      remote work directory           (default: ~/NAME-src)
#   -h          help
#
# Existing images with the same name (any tag) and their containers are removed before building.
set -euo pipefail

NAME=gitsync; TAG=latest; PORT=8080; DATA=""; KEY=""; REMOTE=""; REMOTE_DIR=""
usage() { sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while getopts "n:t:p:d:k:H:r:h" o; do
  case "$o" in
    n) NAME=$OPTARG ;; t) TAG=$OPTARG ;; p) PORT=$OPTARG ;; d) DATA=$OPTARG ;; k) KEY=$OPTARG ;;
    H) REMOTE=$OPTARG ;; r) REMOTE_DIR=$OPTARG ;; h) usage 0 ;; *) usage 1 ;;
  esac
done

[[ "$NAME" =~ ^[a-z0-9][a-z0-9_.-]*$ ]] || { echo "이름은 소문자/숫자/-/_/. 만 사용할 수 있습니다: $NAME" >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
log()  { printf '\033[1;35m▶\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m✔\033[0m %s\n' "$*"; }

# ───────────── remote mode: copy source to the server and run this script there ─────────────
if [[ -n "$REMOTE" ]]; then
  REMOTE_DIR=${REMOTE_DIR:-"~/${NAME}-src"}
  log "소스를 $REMOTE:$REMOTE_DIR 로 복사합니다"
  ssh "$REMOTE" "mkdir -p $REMOTE_DIR"
  tar -C "$ROOT" --exclude='./.git' --exclude='*/bin' --exclude='*/obj' --exclude='./publish' -czf - . \
    | ssh "$REMOTE" "tar -xzf - -C $REMOTE_DIR"
  args=(-n "$NAME" -t "$TAG" -p "$PORT")
  [[ -n "$DATA" ]] && args+=(-d "$DATA")
  [[ -n "$KEY"  ]] && args+=(-k "$KEY")
  log "원격 서버에서 빌드/실행합니다"
  ssh -t "$REMOTE" "cd $REMOTE_DIR && bash deploy/deploy.sh $(printf '%q ' "${args[@]}")"
  exit 0
fi

command -v podman >/dev/null || { echo "podman 이 설치되어 있지 않습니다." >&2; exit 1; }

API_IMG="localhost/${NAME}-api"
WEB_IMG="localhost/${NAME}-web"
POD="${NAME}-pod"
DATA=${DATA:-"$HOME/.local/share/$NAME"}
mkdir -p "$DATA"

# ───────────── 1. remove existing pod / containers / images with this name ─────────────
if podman pod exists "$POD" 2>/dev/null; then
  log "기존 pod 삭제: $POD"
  podman pod rm -f "$POD" >/dev/null
fi
for c in "${NAME}-api" "${NAME}-web"; do
  podman container exists "$c" 2>/dev/null && { log "기존 컨테이너 삭제: $c"; podman rm -f "$c" >/dev/null; }
done
for img in "$API_IMG" "$WEB_IMG"; do
  ids=$(podman images --filter "reference=${img}" --format '{{.ID}}' | sort -u)
  if [[ -n "$ids" ]]; then
    for id in $ids; do
      users=$(podman ps -a --filter "ancestor=$id" --format '{{.ID}}')
      [[ -n "$users" ]] && podman rm -f $users >/dev/null
    done
    log "기존 이미지 삭제: $img ($(echo $ids | wc -w)개)"
    podman rmi -f $ids >/dev/null
  fi
done
podman image prune -f >/dev/null 2>&1 || true

# ───────────── 2. build ─────────────
log "이미지 빌드: ${API_IMG}:${TAG}"
podman build -f "$ROOT/deploy/Containerfile.api" -t "${API_IMG}:${TAG}" "$ROOT"
log "이미지 빌드: ${WEB_IMG}:${TAG}"
podman build -f "$ROOT/deploy/Containerfile.web" -t "${WEB_IMG}:${TAG}" "$ROOT"

# ───────────── 3. access key ─────────────
if [[ -z "$KEY" ]]; then
  if [[ -f "$DATA/access-key" ]]; then KEY=$(cat "$DATA/access-key")
  else KEY=$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32); fi
fi
umask 077; printf '%s' "$KEY" > "$DATA/access-key"

# ───────────── 4. run ─────────────
log "pod 생성: $POD (포트 $PORT → 80)"
podman pod create --name "$POD" -p "${PORT}:80" >/dev/null
podman run -d --pod "$POD" --name "${NAME}-api" --restart unless-stopped \
  -v "$DATA:/data:Z" \
  -e GITSYNC_ACCESS_KEY="$KEY" \
  "${API_IMG}:${TAG}" >/dev/null
podman run -d --pod "$POD" --name "${NAME}-web" --restart unless-stopped \
  "${WEB_IMG}:${TAG}" >/dev/null

for i in $(seq 1 30); do
  curl -fs "http://127.0.0.1:${PORT}/api/health" >/dev/null 2>&1 && break
  sleep 1
done

HOSTIP=$(hostname -I 2>/dev/null | awk '{print $1}')
ok "배포 완료"
echo "   웹 UI    : http://${HOSTIP:-localhost}:${PORT}"
echo "   접근 키  : $KEY   (저장 위치: $DATA/access-key)"
echo "   데이터   : $DATA"
echo "   이미지   : ${API_IMG}:${TAG}, ${WEB_IMG}:${TAG}"
echo "   로그     : podman logs -f ${NAME}-api"
