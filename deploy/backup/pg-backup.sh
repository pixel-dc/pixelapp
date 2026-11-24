#!/bin/sh
# Copia de seguridad de PostgreSQL: volcado comprimido diario + rotación local, con gancho para copia externa.
# Programar con cron o systemd timer, p. ej. a las 03:15:  15 3 * * *  /opt/pixela/src/deploy/backup/pg-backup.sh
#
# La base es lo ÚNICO que guarda los datos (clientes, bots, conocimiento, conversaciones, secretos cifrados, usuarios). Una copia que vive en el
# mismo disco del servidor no protege de perder el servidor: configura PX_BACKUP_UPLOAD para sacarla de allí.
#
# Variables (en /etc/pixela/backup.env o en el entorno):
#   PX_BACKUP_DIR      dónde guardar (por defecto /var/backups/pixela)
#   PX_BACKUP_KEEP     cuántas copias conservar (por defecto 14)
#   PX_BACKUP_UPLOAD   comando que recibe la ruta del archivo, p. ej. 'rclone copy {} spaces:pixela-backups'
#   PGHOST PGPORT PGUSER PGDATABASE PGPASSFILE  conexión estándar de libpq
set -eu

CONFIG="${PX_BACKUP_CONFIG:-/etc/pixela/backup.env}"
# shellcheck disable=SC1090
[ -r "$CONFIG" ] && . "$CONFIG"

DIR="${PX_BACKUP_DIR:-/var/backups/pixela}"
KEEP="${PX_BACKUP_KEEP:-14}"
DB="${PGDATABASE:-pixela}"
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
FILE="$DIR/$DB-$STAMP.dump"

umask 077
mkdir -p "$DIR"

# Formato "custom": comprimido y restaurable por partes con pg_restore. Se escribe a un temporal y se renombra al final,
# así una copia a medias nunca se confunde con una buena.
pg_dump --format=custom --compress=6 --no-owner --file="$FILE.partial" "$DB"
# Comprobación mínima: pg_restore debe poder leer el índice del archivo.
pg_restore --list "$FILE.partial" >/dev/null
mv "$FILE.partial" "$FILE"

# Rotación: se conservan las KEEP más recientes.
ls -1t "$DIR"/"$DB"-*.dump 2>/dev/null | tail -n +"$((KEEP + 1))" | while read -r old; do rm -f -- "$old"; done

if [ -n "${PX_BACKUP_UPLOAD:-}" ]; then
  # shellcheck disable=SC2086
  eval "$(printf '%s' "$PX_BACKUP_UPLOAD" | sed "s|{}|'$FILE'|g")"
fi

echo "copia creada: $FILE ($(du -h "$FILE" | cut -f1))"
