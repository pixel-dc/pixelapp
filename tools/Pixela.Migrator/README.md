# Migración de FacilCorreo a Pixela

Herramienta de una sola vez que copia **todo** lo de Don Porko y Pixel DC desde la base de FacilCorreo a Pixela.
Solo **lee** FacilCorreo (usa una transacción de solo lectura); escribe únicamente en Pixela.

## Qué se migra

| De FacilCorreo | A Pixela |
|---|---|
| Cuenta (Don Porko, Pixel DC) | Cliente con el mismo id, nombre corto, marca del widget, precio por conversación y tope (vacío = ilimitado) |
| — | Un asset por cliente, con el nombre de la cuenta (después se puede renombrar o repartir los bots en más assets) |
| Bots | Mismo id y **misma clave pública**: los widgets ya instalados siguen funcionando cambiando solo el dominio del cargador |
| Llave de Claude de la cuenta (o de su agencia) | Llave de cada bot |
| Clave de la app de Meta de los canales | Clave de la app de Meta del bot |
| — | Token de verificación **nuevo** por bot |
| Conocimiento, conexiones de datos, canales, conversaciones, mensajes, adjuntos (y sus archivos), contactos, íconos | Igual, con sus ids |
| Consumo mensual por cuenta | Consumo mensual del bot (si la cuenta tenía varios bots, queda en el más antiguo y el informe lo avisa) |
| Usuarios | Dueños y administradores de Pixel DC → equipo de la agencia; dueños y administradores de un cliente → administradores del cliente; operadores y miembros → operadores. Conservan contraseña y 2FA |

Todos los secretos se descifran con la llave maestra de FacilCorreo y se vuelven a cifrar con la de Pixela; nunca se escriben en claro en disco.

**No se migra:** los códigos de recuperación del 2FA (dependían de una llave de FacilCorreo; cada persona genera nuevos), los otros clientes de la agencia y nada del correo.

## Cómo correrla (en el servidor de Pixela)

1. Pixela ya instalado, con su base creada y su `.env` (`ConnectionStrings__Default`, `Secrets__MasterKey`).
2. Acceso a la base de FacilCorreo con un usuario de **solo lectura** y su llave maestra (`Secrets__MasterKey` de `/etc/facilcorreo/api.env`).
3. Si se quieren los archivos de los adjuntos, copia la carpeta de adjuntos de FacilCorreo al servidor de Pixela (o móntala).

```bash
export ConnectionStrings__Default='Host=localhost;Database=pixela;Username=pixela;Password=…'   # de /etc/pixela/pixela.env
export Secrets__MasterKey='…'                                                                    # de /etc/pixela/pixela.env
export Meta__AttachmentsPath=/var/lib/pixela/attachments

export MIGRATE_SOURCE_DB='Host=…;Database=facilcorreo;Username=lectura;Password=…'
export MIGRATE_SOURCE_MASTER_KEY='…'                    # llave maestra de FacilCorreo
export MIGRATE_SOURCE_ATTACHMENTS=/ruta/a/attachments   # opcional: adjuntos de FacilCorreo
export MIGRATE_SOURCE_META_APP_SECRET='…'               # opcional: app de Meta global de FacilCorreo, para canales sin app propia

# Primero en simulación: hace todo y lo deshace, solo muestra el informe.
dotnet run --project tools/Pixela.Migrator -- --tenant "Don Porko" --agency "Pixel DC" --dry-run

# Después de verdad.
dotnet run --project tools/Pixela.Migrator -- --tenant "Don Porko" --agency "Pixel DC"
```

`--tenant` acepta el id, el nombre corto o el nombre exacto de la cuenta y se puede repetir. `--agency` es la cuenta de la propia agencia.
Una cuenta ya migrada no se vuelve a migrar (la herramienta se detiene antes de escribir).

## Después de migrar

El informe lista lo que hay que hacer a mano. En resumen:

- **Meta:** en la app de Meta de cada bot con canales, cambiar la URL del webhook y el token de verificación por los de Pixela (pestaña Integraciones del bot). Hasta hacerlo, Meta sigue enviando los mensajes a FacilCorreo.
- **Widget:** en cada sitio donde está instalado, cambiar el dominio del cargador (`<script src="https://<pixela>/widget.js" …>`); la clave del bot es la misma.
- **Claude:** crear una llave por bot en la consola de Anthropic y ponerla en Integraciones, para ver el consumo de cada bot por separado (al migrar, los bots quedan con la llave que tenía la cuenta).
- **2FA:** quien lo tenga activo genera nuevos códigos de recuperación en Mi cuenta.
- Apagar los bots migrados en FacilCorreo cuando el cambio de Meta y del widget esté hecho, para que no respondan dos plataformas.
