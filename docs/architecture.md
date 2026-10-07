# Arquitectura

`Dubbing.Core` solo referencia bibliotecas de `System`. Contiene los contratos
de motores y multimedia, entidades, capacidades y grafo de dependencias. No
referencia Avalonia, DI, transportes, procesos ni proveedores concretos.

`Dubbing.Application` orquesta importación, traducción, generación incremental y
render a través de contratos del Core. La selección TTS aplica segmento > hablante
> proyecto > default. El timeline asigna hablantes por máxima superposición,
desempata por confianza e ID y conserva pausas y segmentos simultáneos.

`Dubbing.Infrastructure` implementa registro de motores, persistencia JSON,
ejecución segura de procesos y localización de ejecutables. El contrato de plugin
con `IServiceCollection` vive aquí para no llevar Microsoft DI al Core.

`Dubbing.Media.FFmpeg` implementa `IMediaEngine`; sus comandos son listas de
argumentos y nunca pasan por un shell. Las salidas se escriben a temporales y
solo sustituyen el destino tras éxito. El proceso se termina con su árbol en caso
de cancelación o timeout. La memoria retenida para stdout/stderr está limitada.

`Dubbing.Engines.Mock` proporciona los seis tipos de engine y dos TTS sustituibles.
`Dubbing.Engines.AudioCpp` y `Dubbing.Engines.LlamaCpp` implementan los proveedores reales fijados, con respuestas normalizadas a los contratos del Core.

`Dubbing.App` compone servicios mediante DI. Los ViewModels editan snapshots y
llaman a servicios; los diálogos usan StorageProvider. La lista de segmentos se
virtualiza con ListBox. Las capacidades controlan la información de voz mostrada;
el editor avanzado permite referencias, emoción, seed y opciones JSON según capacidades.

## Invariantes verificadas

- IDs de engine únicos; se rechazan contratos incompatibles.
- JSON versionado, validado y guardado mediante flush + sustitución atómica.
- Una edición de traducción invalida TTS, ajuste de duración, mezcla y mux del
  segmento; conserva ASR, diarización y los demás segmentos.
- Las traducciones manuales permanecen bloqueadas en las ejecuciones globales.
- La clave de caché considera engine/modelo/versiones, texto, idioma, referencia,
  emoción, duración, velocidad, seed y opciones ordenadas canónicamente.
- Las voces en caché se verifican por SHA-256. Se guarda un checkpoint después
  de cada segmento para recuperar trabajo completado.
- Se mezclan los WAV por su posición temporal, en lotes de hasta 24 entradas;
  los submixes flotantes evitan limitar amplitud antes de la mezcla final.
- No se carga el vídeo completo en RAM; el fingerprint se calcula en streaming.

## Fuentes de las APIs utilizadas

- [Avalonia 11.3.14](https://www.nuget.org/packages/Avalonia/11.3.14).
- [Avalonia StorageProvider](https://docs.avaloniaui.net/docs/services/storage/storage-provider).
- [Filtros oficiales FFmpeg](https://ffmpeg.org/ffmpeg-filters.html): `adelay`, `amix`, `alimiter`.
- [ffprobe](https://ffmpeg.org/ffprobe.html): streams y formato JSON.
