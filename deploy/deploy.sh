#!/usr/bin/env bash
# Despliega Pixela en el servidor desde Git. Se corre EN el servidor, con un usuario que pueda usar sudo:
#
#   sudo /opt/pixela/src/deploy/deploy.sh            # último main
#   sudo /opt/pixela/src/deploy/deploy.sh v1.2.0     # una etiqueta o commit concreto
#   sudo /opt/pixela/src/deploy/deploy.sh --rollback # vuelve a la versión anterior
#
# Cada versión queda en /opt/pixela/releases/<commit>/{api,workers}; /opt/pixela/api y /opt/pixela/workers son enlaces a la
# versión activa. Si la nueva API no responde /health, se vuelve sola a la anterior. Las migraciones de la base se aplican
# al arrancar la API. Se conservan las últimas 3 versiones.
set -euo pipefail

BASE=/opt/pixela
SRC="$BASE/src"
RELEASES="$BASE/releases"
BRANCH="${PIXELA_BRANCH:-main}"
HEALTH=http://127.0.0.1:5180/health
KEEP=3

log() { printf '\n==> %s\n' "$*"; }

switch_to() {
  local release="$1"
  ln -sfn "$release/api" "$BASE/api.new" && mv -Tf "$BASE/api.new" "$BASE/api"
  ln -sfn "$release/workers" "$BASE/workers.new" && mv -Tf "$BASE/workers.new" "$BASE/workers"
  systemctl restart pixela-api pixela-workers
}

healthy() {
  for _ in $(seq 1 30); do
    if curl -fsS "$HEALTH" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

current_release() { readlink -f "$BASE/api" 2>/dev/null | xargs -r dirname; }

if [[ "${1:-}" == "--rollback" ]]; then
  current="$(current_release)"
  previous="$(ls -1dt "$RELEASES"/*/ 2>/dev/null | sed 's:/$::' | grep -vx "$current" | head -n 1 || true)"
  [[ -n "$previous" ]] || { echo "No hay una versión anterior a la que volver." >&2; exit 1; }
  log "Volviendo a $(basename "$previous")"
  switch_to "$previous"
  if healthy; then
    echo "Listo: $(basename "$previous") activa."
  else
    echo "La versión anterior tampoco responde: revisa 'journalctl -u pixela-api'." >&2
    exit 1
  fi
  exit 0
fi

ref="${1:-origin/$BRANCH}"
log "Trayendo el código ($ref)"
sudo -u pixela git -C "$SRC" fetch --tags --prune origin
sudo -u pixela git -C "$SRC" checkout --quiet --detach "$ref"
commit="$(sudo -u pixela git -C "$SRC" rev-parse --short=12 HEAD)"
release="$RELEASES/$commit"
previous="$(current_release || true)"

if [[ "$release" == "$previous" ]]; then
  echo "La versión $commit ya está activa. Nada que hacer."
  exit 0
fi

build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT
chown pixela:pixela "$build"

log "Compilando el panel web"
sudo -u pixela bash -c "cd '$SRC/web' && npm ci --no-audit --no-fund && npm run build"

# De uno en uno y sin servidor de compilación: cabe en un droplet de 2 GB.
log "Compilando la API"
sudo -u pixela dotnet publish "$SRC/src/Pixela.Api" -c Release -o "$build/api" -m:1 -p:UseSharedCompilation=false --nologo
log "Compilando los procesos de fondo"
sudo -u pixela dotnet publish "$SRC/src/Pixela.Workers" -c Release -o "$build/workers" -m:1 -p:UseSharedCompilation=false --nologo
cp -r "$SRC/web/dist" "$build/api/wwwroot"

mkdir -p "$RELEASES"
rm -rf "$release"
mv "$build" "$release"
chown -R root:pixela "$release"
chmod -R g+rX,o-rwx "$release"
trap - EXIT

log "Activando $commit"
switch_to "$release"
if ! healthy; then
  echo "La API nueva no responde. Volviendo a la anterior." >&2
  journalctl -u pixela-api -n 30 --no-pager >&2 || true
  if [[ -n "$previous" && -d "$previous" ]]; then switch_to "$previous"; fi
  exit 1
fi

if ! systemctl is-active --quiet pixela-workers; then
  echo "Aviso: los procesos de fondo no arrancaron. Revisa 'journalctl -u pixela-workers'." >&2
fi

log "Limpiando versiones viejas (se conservan $KEEP)"
ls -1dt "$RELEASES"/*/ | sed 's:/$::' | tail -n +"$((KEEP + 1))" | while read -r old; do rm -rf -- "$old"; done

echo "Listo: $commit activa."
