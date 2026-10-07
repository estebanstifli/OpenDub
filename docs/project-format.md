# Formato de proyecto v1

Una carpeta `Nombre-id.dubproj` contiene `project.json`, `project.json.bak`, assets,
generated, cache y renders. El vídeo original se referencia por ruta absoluta y
SHA-256, sin copiarlo. Mover ese vídeo requiere volver a importarlo en esta versión.

El documento raíz incluye `schemaVersion: 1`, ID, nombre, fecha, fuente, selección
de motores, ajustes/glosario, hablantes y segmentos. Se eligió un único documento
para guardar estos datos como una transacción; evita que varios JSON queden en
versiones distintas tras un fallo. Los tiempos son `TimeSpan` serializados por
System.Text.Json; nombres de propiedades camelCase y enums como strings.

Los assets generados usan rutas relativas al proyecto. Las referencias externas
de voz pueden ser absolutas. Las opciones específicas de motor son valores JSON
que se recuperan como JsonElement: los providers deben convertirlas explícitamente.

Los segmentos contienen texto original, traducción y bloqueo manual, hablante,
palabras opcionales, emoción, opciones TTS, archivo generado, hash, duración,
estado y etapas pendientes. Se admiten segmentos simultáneos.

El guardado escribe un temporal único, realiza flush a disco, cierra y sustituye
atómicamente `project.json`, conservando la versión previa en `.bak`. Nunca se
recupera una copia antigua silenciosamente: un JSON corrupto muestra error. Para
recuperación manual, conserva el corrupto y copia el `.bak` a `project.json`.

Se rechazan versiones futuras, versiones ausentes, IDs duplicados, tiempos
inválidos y rutas generadas que escapen del proyecto. `IProjectMigration` permite
añadir migraciones consecutivas al introducir v2; todavía no hay formatos previos
reales que migrar.

`ProjectSession` controla cambios pendientes. Un guardado tardío no marca limpias
ediciones posteriores. La UI pide guardar/descartar/cancelar al cambiar o cerrar.
El auto-save guarda ediciones aplicadas con debounce de 800 ms; generar voces guarda checkpoints por segmento. Los campos fittedSpeech, stems y metrics son aditivos y mantienen compatibilidad v1.
