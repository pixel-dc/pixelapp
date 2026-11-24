# Despliegue de Pixela

Pixela corre sin Docker en un servidor Linux (pensado para el droplet de DigitalOcean con **Ubuntu 26.04**):

```
Internet ──https──► Caddy (443, certificado automático) ──► API :5180 (también sirve el panel)
                                                             Workers (Meta, indexado, retención)
                                                             PostgreSQL (localhost)
```

La preparación del servidor la hace el equipo de Pixel DC; aquí está lo que la aplicación necesita y las piezas listas para usar.

## Qué necesita el servidor

| Pieza | Detalle |
|---|---|
| Sistema | Ubuntu 26.04, 2 GB de RAM (con 2 GB de swap compila sin problema) |
| .NET | SDK 10 (`dotnet-sdk-10.0`): se compila en el propio servidor |
| Node | 22 o superior (solo para compilar el panel) |
| PostgreSQL | 17 o superior, en el mismo servidor; una base y un usuario `pixela` |
| Caddy | Cualquier versión 2.x (`apt install caddy`) |
| DNS | Registro A del subdominio (por ejemplo `bots.pixeldc.com`) a la IP del servidor |
| Firewall | Abiertos solo 22, 80 y 443. La API (5180) y PostgreSQL (5432) quedan en localhost |
| Git | Acceso de lectura al repositorio (llave de despliegue de GitHub si es privado) |

## Puesta en marcha (una vez)

```bash
# Usuario y carpetas
sudo adduser --system --group --home /var/lib/pixela pixela
sudo mkdir -p /opt/pixela /var/lib/pixela/attachments /etc/pixela
sudo chown pixela:pixela /opt/pixela /var/lib/pixela /var/lib/pixela/attachments

# Código
sudo -u pixela git clone https://github.com/pixel-dc/pixelapp.git /opt/pixela/src

# Base de datos
sudo -u postgres createuser pixela --pwprompt
sudo -u postgres createdb pixela --owner pixela

# Configuración: SOLO la llave maestra y la conexión (ver pixela.env.example)
sudo cp /opt/pixela/src/deploy/pixela.env.example /etc/pixela/pixela.env
sudo chown root:pixela /etc/pixela/pixela.env && sudo chmod 640 /etc/pixela/pixela.env
openssl rand -base64 32          # pegar en Secrets__MasterKey, y GUARDARLA también fuera del servidor
sudo nano /etc/pixela/pixela.env

# Servicios
sudo cp /opt/pixela/src/deploy/systemd/pixela-*.service /opt/pixela/src/deploy/systemd/pixela-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable pixela-api pixela-workers pixela-backup.timer

# Caddy: cambiar el subdominio en el archivo y copiarlo
sudo cp /opt/pixela/src/deploy/Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy

# Primera versión
sudo /opt/pixela/src/deploy/deploy.sh
```

Después abrir `https://<subdominio>`: como la base está vacía, el panel pide crear al **dueño de la agencia**. Luego, en
**Configuración**, poner la dirección pública (`https://<subdominio>`): con ella se arman las URL de los webhooks de Meta.

## Desplegar una versión nueva

```bash
sudo /opt/pixela/src/deploy/deploy.sh            # lo último de main
sudo /opt/pixela/src/deploy/deploy.sh v1.2.0     # una etiqueta o commit
sudo /opt/pixela/src/deploy/deploy.sh --rollback # volver a la versión anterior
```

El script trae el código, compila el panel y los dos servicios, deja la versión en `/opt/pixela/releases/<commit>`, cambia los
enlaces `/opt/pixela/api` y `/opt/pixela/workers` y reinicia. Si la API nueva no responde `/health` en un minuto, vuelve sola a la
anterior. Las migraciones de la base se aplican al arrancar la API. Se conservan las 3 últimas versiones.

## Copias de seguridad

`pixela-backup.timer` corre `backup/pg-backup.sh` todos los días a las 03:15 y guarda 14 copias en `/var/backups/pixela`.
**Una copia en el mismo servidor no protege de perder el servidor**: configura `PX_BACKUP_UPLOAD` en `/etc/pixela/backup.env`
(por ejemplo `rclone copy {} spaces:pixela-backups`) y guarda la llave maestra aparte; sin ella, los secretos de la copia no se
pueden descifrar. Restaurar: `pg_restore --clean --no-owner -d pixela <archivo.dump>` con los servicios detenidos.

## Revisar

```bash
systemctl status pixela-api pixela-workers
journalctl -u pixela-api -f
curl -s http://127.0.0.1:5180/health
```

## Notas

- Las plantillas (Caddyfile, servicios, script) se validaron con `shellcheck` pero **no se han probado todavía en el droplet real**: la
  primera instalación es la prueba. Si algo no coincide con el servidor, ajustarlo aquí y versionarlo.
- La migración de Don Porko y Pixel DC desde FacilCorreo se corre una vez después de la primera versión: ver
  `tools/Pixela.Migrator/README.md`.
- Ajustes opcionales (no secretos) se pueden poner en el mismo `.env` con el formato `Seccion__Clave`, por ejemplo
  `Chat__MessagesPerMinutePerIp=30`. Todo lo demás (modelo, precios, retención, marca) se cambia desde el panel.
