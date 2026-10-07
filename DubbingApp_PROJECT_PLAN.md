# DubbingApp — Plan técnico de implementación

> **Documento para el agente de programación (GPT-6 Astra)**
>
> Fecha de referencia: 2026-10-06  
> Estado: especificación inicial / fuente de verdad para el MVP  
> Nombre de trabajo: `DubbingApp` (renombrable antes de publicar)

---

## 0. Instrucción principal para el agente de programación

Construir una aplicación de escritorio **local-first, offline y multiplataforma** para traducir y doblar vídeos mediante modelos de IA locales.

La aplicación debe funcionar, como objetivo principal, en:

- Windows x64.
- Linux x64.
- macOS Apple Silicon.
- macOS Intel como objetivo secundario si las dependencias lo permiten.

La regla arquitectónica más importante es:

> **NINGÚN modelo concreto de IA puede formar parte de la lógica de negocio del Core.**

El Core no debe saber qué es IndexTTS, Qwen, Nemotron, Demucs, audio.cpp, llama.cpp, CUDA o Metal.  
El Core solo conoce **capacidades y contratos abstractos** como:

- `ITranscriptionEngine`
- `IDiarizationEngine`
- `IAlignmentEngine`
- `ITranslationEngine`
- `ITtsEngine`
- `ISeparationEngine`
- `IMediaEngine`
- `IModelManager`

Los motores concretos deben vivir en adaptadores/providers independientes.

Se debe poder sustituir, por ejemplo:

```text
IndexTTS-2.5
     ↓
Qwen3-TTS
     ↓
un futuro TTS aún no existente
```

sin modificar el pipeline de doblaje, el modelo de proyecto ni la lógica principal de la UI.

Del mismo modo:

```text
Qwen3-ASR -> futuro ASR
Nemotron diarization -> futuro diarizador
llama.cpp -> futuro motor de traducción
BS-RoFormer -> futuro separador
```

deben ser cambios de configuración/provider, no reescrituras de la aplicación.

---

# 1. Objetivo del producto

Crear una aplicación similar conceptualmente a un pequeño editor de doblaje:

```text
VIDEO ORIGINAL
      │
      ▼
EXTRACCIÓN / ANÁLISIS DE AUDIO
      │
      ├── separación voz / música / efectos
      │
      ├── transcripción
      │
      ├── timestamps / alineación
      │
      └── diarización
      │
      ▼
TIMELINE ESTRUCTURADO
      │
      ├── Speaker 1
      ├── Speaker 2
      ├── Speaker 3
      └── ...
      │
      ▼
TRADUCCIÓN + ADAPTACIÓN DE DURACIÓN
      │
      ▼
TTS / CLONACIÓN / EMOCIÓN
      │
      ▼
AJUSTE TEMPORAL
      │
      ▼
MEZCLA CON MÚSICA / SFX
      │
      ▼
VIDEO DOBLADO
```

La aplicación debe permitir intervenir manualmente en cualquier etapa.

No debe ser únicamente:

```text
Seleccionar vídeo -> botón -> vídeo terminado
```

Debe permitir editar:

- texto transcrito;
- traducción;
- asignación de hablantes;
- voz de cada hablante;
- referencia de voz;
- emoción;
- intensidad;
- velocidad;
- duración;
- motor/modelo usado;
- regeneración individual de segmentos.

---

# 2. Principios no negociables

## 2.1. Local-first

Por defecto:

- ningún audio se envía a Internet;
- ningún vídeo se envía a Internet;
- ninguna transcripción se envía a Internet;
- los modelos se ejecutan localmente.

La descarga inicial de modelos sí puede requerir Internet.

En un futuro podrán existir providers cloud opcionales, pero nunca deben ser necesarios para el funcionamiento normal.

---

## 2.2. Sin dependencia obligatoria de Python

El producto final no debe requerir que el usuario:

- instale Python;
- cree Conda environments;
- instale PyTorch;
- configure pip;
- gestione versiones CUDA de paquetes Python.

El runtime de audio inicial será `audio.cpp`.

La traducción local se realizará inicialmente mediante `llama.cpp`.

FFmpeg se utilizará para operaciones multimedia.

---

## 2.3. Arquitectura modular en dos niveles

Hay que distinguir entre:

### Nivel A — Provider/runtime

Ejemplos:

```text
audio.cpp
llama.cpp
future-native-runtime
future-cloud-runtime
```

Un provider sabe comunicarse con un runtime.

### Nivel B — Modelo

Ejemplos dentro de `audio.cpp`:

```text
qwen3_asr
nemotron_3_diar
index_tts2 / IndexTTS-2.5
qwen3_tts
bs_roformer
htdemucs
```

Cambiar un modelo soportado por el mismo provider debería requerir idealmente **cero cambios de código**.

Solo cambiar:

- descriptor;
- manifest;
- configuración;
- modelo instalado.

---

# 3. Stack tecnológico recomendado

## Desktop

- .NET 10.
- C#.
- Avalonia UI.
- MVVM.
- `CommunityToolkit.Mvvm` o equivalente ligero.
- Dependency Injection de Microsoft.

Usar versiones estables y fijarlas en `Directory.Packages.props`.

No depender de APIs experimentales de Avalonia salvo necesidad real.

---

## IA / audio

### Runtime principal

`audio.cpp`

Usarlo para:

- ASR;
- diarización;
- forced alignment;
- source separation;
- TTS;
- voice cloning;
- emotion/style control cuando el modelo lo permita.

No acoplar la aplicación a un único modelo de `audio.cpp`.

### Traducción

Inicialmente:

`llama.cpp / llama-server`

La aplicación debe hablar con él a través de un adapter `ITranslationEngine`.

### Multimedia

`ffmpeg` + `ffprobe`

Encapsularlos detrás de:

```csharp
IMediaEngine
```

No ejecutar comandos FFmpeg directamente desde ViewModels.

---

# 4. Arquitectura general

```text
┌───────────────────────────────────────────────────────────────┐
│                         Dubbing.App                           │
│                  Avalonia / ViewModels                        │
└─────────────────────────────┬─────────────────────────────────┘
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                    Dubbing.Application                        │
│                                                               │
│  ImportProject                                                │
│  AnalyzeAudio                                                 │
│  Transcribe                                                   │
│  Diarize                                                      │
│  Translate                                                    │
│  GenerateSpeech                                               │
│  Mix                                                          │
│  Export                                                       │
└─────────────────────────────┬─────────────────────────────────┘
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                       Dubbing.Core                            │
│                                                               │
│  Domain models                                                │
│  Engine contracts                                             │
│  Pipeline contracts                                           │
│  Capability model                                             │
│  Project format                                               │
│                                                               │
│  *** NO audio.cpp / llama.cpp / FFmpeg dependencies ***       │
└──────────────┬────────────────┬─────────────────┬─────────────┘
               │                │                 │
               ▼                ▼                 ▼
┌────────────────────┐ ┌───────────────────┐ ┌─────────────────┐
│ Engines.AudioCpp   │ │ Engines.LlamaCpp │ │ Media.FFmpeg    │
│                    │ │                   │ │                 │
│ ASR                │ │ Translation       │ │ Probe           │
│ Diarization        │ │ Adaptation        │ │ Decode          │
│ Alignment          │ │                   │ │ Mix             │
│ TTS                │ │                   │ │ Encode          │
│ Separation         │ │                   │ │ Mux             │
└──────────┬─────────┘ └─────────┬─────────┘ └────────┬────────┘
           │                     │                    │
           ▼                     ▼                    ▼
   audiocpp_server          llama-server          ffmpeg
   audiocpp_cli
   model manager
```

---

# 5. Estructura del repositorio

Propuesta:

```text
/
├── src/
│   ├── Dubbing.App/
│   ├── Dubbing.Core/
│   ├── Dubbing.Application/
│   ├── Dubbing.Infrastructure/
│   ├── Dubbing.Media.FFmpeg/
│   ├── Dubbing.Engines.AudioCpp/
│   ├── Dubbing.Engines.LlamaCpp/
│   └── Dubbing.Engines.Mock/
│
├── tests/
│   ├── Dubbing.Core.Tests/
│   ├── Dubbing.Application.Tests/
│   ├── Dubbing.AudioCpp.Tests/
│   ├── Dubbing.LlamaCpp.Tests/
│   └── Dubbing.Integration.Tests/
│
├── runtimes/
│   ├── win-x64/
│   ├── linux-x64/
│   ├── osx-arm64/
│   └── osx-x64/
│
├── engine-catalog/
│   ├── audio-cpp/
│   └── llama-cpp/
│
├── docs/
│   ├── architecture.md
│   ├── project-format.md
│   ├── engines.md
│   └── packaging.md
│
├── samples/
│
├── Directory.Build.props
├── Directory.Packages.props
├── DubbingApp.sln
└── README.md
```

---

# 6. Responsabilidad de cada proyecto

## Dubbing.Core

Debe contener únicamente:

- entidades;
- value objects;
- contratos;
- enums;
- descriptores de capacidades;
- eventos del dominio;
- tipos de resultados.

NO debe referenciar:

- Avalonia;
- audio.cpp;
- llama.cpp;
- FFmpeg;
- HTTP implementation details;
- CUDA;
- Vulkan;
- Metal.

---

## Dubbing.Application

Orquestación de casos de uso.

Ejemplos:

```text
CreateProjectUseCase
ImportVideoUseCase
AnalyzeProjectUseCase
TranscribeProjectUseCase
DiarizeProjectUseCase
TranslateProjectUseCase
GenerateDubUseCase
RenderProjectUseCase
ExportVideoUseCase
```

Aquí se compone el pipeline utilizando interfaces del Core.

---

## Dubbing.Engines.AudioCpp

Todo lo específico de `audio.cpp`.

Debe saber:

- iniciar/parar `audiocpp_server`;
- ejecutar `audiocpp_cli`;
- hablar con sus endpoints;
- inspeccionar modelos;
- convertir resultados del runtime a contratos del Core;
- usar el model manager;
- resolver backends;
- mapear opciones específicas.

El resto de la aplicación NO debe conocer esas opciones específicas.

---

## Dubbing.Engines.LlamaCpp

Todo lo relacionado con:

- `llama-server`;
- prompts de traducción;
- salida estructurada;
- detección de errores;
- context window;
- adaptación de duración.

---

## Dubbing.Media.FFmpeg

Debe encapsular:

- ffprobe;
- extracción de audio;
- resampling;
- cortar clips;
- mezcla;
- normalización;
- time stretch;
- mux final.

---

## Dubbing.Engines.Mock

Motores falsos para poder probar la aplicación sin modelos gigantes.

Debe incluir:

- `MockTranscriptionEngine`
- `MockDiarizationEngine`
- `MockTranslationEngine`
- `MockTtsEngine`

Esto es importante para CI.

---

# 7. Contratos de motores

Los nombres exactos pueden ajustarse, pero mantener esta separación conceptual.

---

## 7.1. Descriptor común

```csharp
public sealed record EngineDescriptor(
    string ProviderId,
    string EngineId,
    string DisplayName,
    EngineTask Task,
    IReadOnlySet<string> Languages,
    EngineCapabilities Capabilities,
    HardwareRequirements Hardware,
    string? License,
    string? Version
);
```

---

## 7.2. Transcripción

```csharp
public interface ITranscriptionEngine
{
    EngineDescriptor Descriptor { get; }

    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}
```

`TranscriptionResult` debe poder contener:

```text
text
language
segments
words
confidence
timing
```

Nunca asumir que todos los motores tienen word timestamps.

---

## 7.3. Diarización

```csharp
public interface IDiarizationEngine
{
    EngineDescriptor Descriptor { get; }

    Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}
```

Salida normalizada:

```csharp
public sealed record SpeakerTurn(
    TimeSpan Start,
    TimeSpan End,
    string SpeakerId,
    float? Confidence);
```

---

## 7.4. Forced alignment

```csharp
public interface IAlignmentEngine
{
    EngineDescriptor Descriptor { get; }

    Task<AlignmentResult> AlignAsync(
        AlignmentRequest request,
        CancellationToken cancellationToken);
}
```

---

## 7.5. Source separation

```csharp
public interface ISeparationEngine
{
    EngineDescriptor Descriptor { get; }

    Task<SeparationResult> SeparateAsync(
        SeparationRequest request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}
```

No asumir que todos generan los mismos stems.

La salida será algo como:

```text
vocals
instrumental
drums
bass
other
...
```

mediante nombres de stem genéricos.

---

## 7.6. Traducción

```csharp
public interface ITranslationEngine
{
    EngineDescriptor Descriptor { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
```

Debe soportar:

- idioma origen;
- idioma destino;
- contexto anterior;
- contexto posterior;
- speaker;
- duración objetivo;
- glossary;
- style instructions.

---

## 7.7. TTS

```csharp
public interface ITtsEngine
{
    EngineDescriptor Descriptor { get; }

    Task<TtsResult> SynthesizeAsync(
        TtsRequest request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}
```

`TtsRequest` NO debe contener propiedades llamadas `IndexTtsEmotionAlpha`, etc.

Debe utilizar conceptos genéricos:

```csharp
public sealed record TtsRequest
{
    public required string Text { get; init; }
    public required string Language { get; init; }

    public string? SpeakerProfileId { get; init; }

    public AudioReference? VoiceReference { get; init; }

    public EmotionDescriptor? Emotion { get; init; }

    public TimeSpan? TargetDuration { get; init; }

    public double? SpeakingRate { get; init; }

    public ulong? Seed { get; init; }

    public IReadOnlyDictionary<string, object?> EngineOptions { get; init; }
        = new Dictionary<string, object?>();
}
```

`EngineOptions` queda como escape hatch para opciones avanzadas de providers sin contaminar el Core.

---

# 8. Sistema de capacidades

Es esencial.

Cada modelo debe declarar lo que sabe hacer.

Ejemplo:

```csharp
public sealed record EngineCapabilities
{
    public bool SupportsStreaming { get; init; }

    public bool SupportsWordTimestamps { get; init; }

    public bool SupportsSpeakerLabels { get; init; }

    public bool SupportsVoiceCloning { get; init; }

    public bool SupportsEmotionReferenceAudio { get; init; }

    public bool SupportsEmotionLabels { get; init; }

    public bool SupportsEmotionVector { get; init; }

    public bool SupportsNonVerbalTags { get; init; }

    public bool SupportsSpeakingRate { get; init; }

    public bool SupportsTargetDuration { get; init; }

    public bool SupportsSeed { get; init; }

    public int? MaxSpeakers { get; init; }
}
```

La UI se construirá de acuerdo con capacidades.

Ejemplo:

Si un TTS soporta emotion reference:

```text
[Emotion source]
(*) Original segment
( ) Reference audio
( ) None
```

Si otro TTS solo permite etiquetas:

```text
Emotion:
Happy
Sad
Angry
Neutral
```

No mostrar controles que el modelo no soporte.

---

# 9. Engine Registry

Crear:

```csharp
public interface IEngineRegistry
{
    IReadOnlyList<EngineDescriptor> GetAvailableEngines(EngineTask task);

    T GetEngine<T>(string engineId)
        where T : class;
}
```

Los engines se registran mediante DI.

Ejemplo:

```text
audio.cpp/qwen3-asr
audio.cpp/nemotron-diar
audio.cpp/index-tts-2.5
audio.cpp/bs-roformer
llama.cpp/qwen-translation
```

La configuración de proyecto guarda IDs, nunca referencias a clases.

---

# 10. Plugins: diseño preparado desde el principio

No es obligatorio hacer carga dinámica de DLL en la primera semana, pero la arquitectura debe permitirlo.

Definir:

```csharp
public interface IEnginePlugin
{
    string PluginId { get; }

    void Register(IServiceCollection services);
}
```

Futuro:

```text
/plugins/
    AudioCpp/
        Dubbing.Plugin.AudioCpp.dll
        plugin.json

    FutureTts/
        Dubbing.Plugin.FutureTts.dll
        plugin.json
```

Usar `AssemblyLoadContext` solo cuando sea necesario.

IMPORTANTE:

La primera versión puede compilar providers directamente en la app.

La modularidad principal debe venir de los contratos.  
No sacrificar estabilidad del MVP por construir prematuramente un marketplace de plugins.

---

# 11. Model Catalog / manifests

Separar código de catálogo de modelos.

Ejemplo:

```json
{
  "id": "index-tts-2.5-q8",
  "provider": "audio.cpp",
  "family": "index_tts2",
  "task": "tts",
  "displayName": "IndexTTS 2.5 Q8",
  "languages": ["zh", "en", "ja", "es", "ar"],
  "capabilities": {
    "voiceCloning": true,
    "emotionControl": true
  },
  "license": "CHECK_UPSTREAM",
  "distribution": {
    "bundled": false
  }
}
```

No duplicar información que el runtime pueda descubrir automáticamente.

Preferir:

1. descubrimiento runtime;
2. metadata del model manager;
3. manifests locales como overlay/fallback.

---

# 12. audio.cpp provider

## 12.1. Regla

`Dubbing.Engines.AudioCpp` debe esconder completamente los detalles de `audio.cpp`.

El Core jamás debe ejecutar:

```text
audiocpp_cli
```

ni conocer URLs:

```text
/v1/audio/speech
```

---

## 12.2. Integración inicial recomendada

Usar una estrategia híbrida:

### HTTP server

Preferir `audiocpp_server` para:

- TTS repetido;
- ASR repetido;
- operaciones que reutilicen sesión/modelo;
- endpoints estables.

### CLI

Usar `audiocpp_cli` para:

- tareas todavía no cubiertas convenientemente por el server;
- separación;
- inspección;
- operaciones one-shot.

Ambos quedan detrás del mismo provider.

En el futuro se puede añadir:

```text
AudioCppCAbiTransport
```

sin cambiar contratos superiores.

---

## 12.3. Transport abstraction

Crear internamente:

```csharp
internal interface IAudioCppTransport
{
    Task<AudioCppResult> ExecuteAsync(
        AudioCppCommand command,
        CancellationToken cancellationToken);
}
```

Implementaciones:

```text
AudioCppHttpTransport
AudioCppCliTransport
```

Más adelante:

```text
AudioCppNativeTransport
```

---

# 13. Gestión del proceso audiocpp_server

Crear:

```text
AudioCppServerHost
```

Responsabilidades:

- localizar ejecutable correcto para OS/arquitectura;
- generar configuración temporal;
- elegir puerto localhost libre;
- ejecutar en `127.0.0.1`;
- esperar `/health`;
- reiniciar si muere;
- recoger stdout/stderr;
- detener limpiamente;
- kill process tree si hay cancelación grave.

Nunca exponer el server en:

```text
0.0.0.0
```

por defecto.

---

# 14. Gestión de VRAM

Muy importante, especialmente para GPUs de 8 GB.

No mantener simultáneamente en VRAM:

```text
ASR + diarización + TTS + separación + LLM
```

si no es necesario.

Crear:

```csharp
IGpuWorkScheduler
```

Inicialmente:

- una tarea GPU pesada simultánea;
- stages secuenciales;
- liberar/reiniciar runtime entre familias cuando sea necesario;
- permitir configurar concurrencia en equipos potentes.

Ejemplo:

```text
1. BS-RoFormer
   unload

2. Qwen ASR
   unload

3. Nemotron diarization
   unload

4. llama.cpp translation
   unload si hace falta

5. IndexTTS
```

No optimizar para una RTX 5090 sacrificando usuarios de 6-8 GB.

---

# 15. Backend abstraction

No hardcodear CUDA.

Enum:

```csharp
public enum ComputeBackend
{
    Auto,
    Cpu,
    Cuda,
    Vulkan,
    Hip,
    Metal
}
```

Un provider puede devolver:

```text
supportedBackends
recommendedBackend
```

La UI permitirá:

```text
Backend: Auto
```

como valor recomendado.

---

# 16. Gestión de modelos

Crear:

```csharp
public interface IModelManager
{
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(...);

    Task InstallAsync(...);

    Task RemoveAsync(...);

    Task VerifyAsync(...);
}
```

Para `audio.cpp`, aprovechar su model manager nativo cuando sea posible.

No reimplementar el downloader si upstream ya ofrece:

- instalación;
- validación;
- metadata;
- publicación atómica;
- list/info JSON.

---

## 16.1. No incluir todos los modelos en el instalador

El instalador debe ser relativamente pequeño.

Primera ejecución:

```text
AI Models

Transcription
[ Download ]

Diarization
[ Download ]

Translation
[ Download ]

Voice / TTS
[ Download ]

Source separation
[ Optional download ]
```

---

## 16.2. Licencias

Cada modelo debe tener metadata:

```text
license
commercialUse
redistributionAllowed
sourceUrl
version
```

No asumir que la licencia de `audio.cpp` cubre los pesos.

NO redistribuir automáticamente modelos si su licencia no lo permite.

---

# 17. Modelos iniciales recomendados

Estos son **defaults**, no dependencias arquitectónicas.

## ASR

Primera opción:

```text
Qwen3-ASR 0.6B
```

Motivos:

- multilingüe;
- relativamente pequeño;
- soportado por audio.cpp;
- forced aligner compatible disponible.

---

## Forced alignment

```text
Qwen3 Forced Aligner 0.6B
```

---

## Diarización

```text
Nemotron 3 Diarization
```

Debe tratarse simplemente como implementación de:

```text
IDiarizationEngine
```

---

## TTS

Default inicial:

```text
IndexTTS-2.5
```

porque interesa:

- voice cloning;
- control expresivo/emoción;
- español;
- referencia emocional.

Pero el proyecto NO debe depender de él.

Añadir posteriormente:

```text
Qwen3-TTS
Maya
OmniVoice
Breeze
otros
```

sin cambiar Core.

---

## Source separation

Inicial:

```text
BS-RoFormer
```

Alternativa:

```text
HTDemucs
```

---

## Traducción

`llama.cpp`

El modelo LLM concreto debe ser configurable.

No llamar a la clase:

```text
QwenTranslator
```

Llamarla:

```text
LlamaCppTranslationEngine
```

y seleccionar el modelo por configuración.

---

# 18. Modelo de proyecto

No guardar únicamente el resultado final.

Guardar un proyecto editable.

Formato recomendado:

```text
MyMovie.dubproj/
│
├── project.json
├── speakers.json
├── segments.json
├── settings.json
│
├── source/
│   └── source-reference.json
│
├── assets/
│   ├── voices/
│   ├── extracted/
│   └── stems/
│
├── generated/
│   ├── tts/
│   └── previews/
│
├── cache/
│
└── renders/
```

El vídeo original puede permanecer fuera del proyecto y referenciarse por path + fingerprint.

No copiar automáticamente vídeos enormes salvo que el usuario lo pida.

---

# 19. Versionado del formato de proyecto

Todo JSON raíz debe contener:

```json
{
  "schemaVersion": 1
}
```

Crear migradores:

```text
ProjectMigratorV1ToV2
ProjectMigratorV2ToV3
```

Nunca romper proyectos antiguos silenciosamente.

---

# 20. Entidad Segment

Ejemplo conceptual:

```json
{
  "id": "seg_000124",
  "startMs": 64210,
  "endMs": 67100,

  "speakerId": "speaker_02",

  "source": {
    "language": "en",
    "text": "Where have you been?"
  },

  "translation": {
    "language": "es",
    "text": "¿Dónde has estado?",
    "locked": false
  },

  "emotion": {
    "mode": "source_audio",
    "label": null,
    "intensity": 0.75
  },

  "tts": {
    "engineId": "audio.cpp/index-tts-2.5",
    "voiceProfileId": "voice_speaker_02",
    "generatedFile": "generated/tts/seg_000124.wav",
    "generatedDurationMs": 2801
  },

  "state": {
    "translationDirty": false,
    "ttsDirty": false
  }
}
```

---

# 21. Speaker Profile

```json
{
  "id": "speaker_02",
  "displayName": "Speaker 2",
  "color": "#...",
  "voiceReference": "assets/voices/speaker_02.wav",
  "preferredTtsEngine": null,
  "notes": ""
}
```

Permitir:

```text
Speaker 2 -> Actor / personaje X
```

pero no exigir nombres reales.

---

# 22. Timeline

El timeline es el corazón de la app.

NO generar una pista concatenando frases una detrás de otra.

Cada frase debe conservar:

```text
start
end
speaker
```

Renderizar cada WAV en su posición temporal.

Así se soportan:

- silencios;
- pausas;
- solapamientos;
- dos hablantes a la vez;
- interrupciones.

Conceptualmente:

```text
Speaker A:        [==========]
Speaker B:              [======]
Music/SFX:    ===================================
```

---

# 23. Pipeline completo

Definir stages.

```text
Import
  ↓
Probe
  ↓
ExtractAudio
  ↓
OptionalSeparation
  ↓
ASR
  ↓
Diarization
  ↓
AlignmentAndMerge
  ↓
SpeakerReferenceSelection
  ↓
Translation
  ↓
TranslationDurationAdaptation
  ↓
TTS
  ↓
DurationFit
  ↓
Mix
  ↓
Mux
```

Cada stage debe ser:

- cancelable;
- reiniciable;
- observable;
- cacheable;
- idempotente cuando sea posible.

---

# 24. Stage 1 — Import / Probe

`ffprobe` obtiene:

```text
duration
video codec
audio codec
sample rate
channel layout
frame rate
time base
audio streams
subtitle streams
```

Guardar metadata.

---

# 25. Stage 2 — Audio normalization

Crear una copia de trabajo WAV.

Para ASR/diarización:

```text
mono
16 kHz
PCM
```

cuando el engine lo requiera.

Para separación:

```text
44.1 kHz stereo
```

si lo requiere el modelo.

No convertir el master original innecesariamente.

---

# 26. Stage 3 — Separación

Si se activa:

```text
mix.wav
   │
   ├── vocals.wav
   └── instrumental.wav
```

Usar `vocals.wav` preferentemente para:

- ASR;
- diarización;
- extracción de referencias de voz.

Usar `instrumental.wav` como bed inicial del doblaje.

Si separation está desactivado:

- continuar;
- advertir que las voces originales permanecerán parcialmente en la mezcla final.

---

# 27. Stage 4 — ASR

Resultado normalizado:

```text
TranscriptionResult
  Text
  Segments[]
  Words[]
```

Si el ASR no produce word timestamps:

- ejecutar forced alignment si está disponible.

No hacer que ASR y aligner sean una sola interfaz.

---

# 28. Stage 5 — Diarización

Generar:

```text
SpeakerTurn[]
```

Ejemplo:

```text
00:12.120 - 00:15.910 speaker_00
00:16.050 - 00:19.080 speaker_01
```

---

# 29. Stage 6 — Merge ASR + diarization

Crear algoritmo independiente:

```text
TimelineBuilder
```

Input:

```text
words / ASR segments
speaker turns
```

Output:

```text
DubSegment[]
```

Asignar speaker por máxima superposición temporal.

Para words cercanas a boundary:

- usar overlap;
- confidence;
- continuidad del hablante;
- heurísticas de puntuación.

No meter esta lógica dentro del adapter de Nemotron.

---

# 30. Segmentación textual

No usar segmentos ASR gigantes.

Crear un segmenter configurable basado en:

- pausas;
- puntuación;
- speaker changes;
- duración máxima;
- word timestamps.

Objetivo inicial:

```text
1-8 segundos por segmento
```

pero configurable.

Nunca cortar una palabra.

---

# 31. Selección automática de referencia de voz

Crear:

```text
IVoiceReferenceSelector
```

No depende de IndexTTS.

Por hablante:

1. localizar clips con un único speaker;
2. evitar solapamientos;
3. evitar clips demasiado cortos;
4. preferir alta confianza;
5. preferir bajo ruido/música;
6. evitar clipping;
7. crear ranking.

Generar inicialmente:

```text
assets/voices/speaker_01.wav
```

El usuario debe poder:

- escuchar;
- sustituir;
- elegir otro fragmento;
- importar WAV externo.

---

# 32. Emociones

Crear un formato genérico.

```csharp
public sealed record EmotionDescriptor
{
    public EmotionMode Mode { get; init; }

    public string? Label { get; init; }

    public double? Intensity { get; init; }

    public AudioReference? ReferenceAudio { get; init; }

    public IReadOnlyList<double>? Vector { get; init; }
}
```

Modos:

```text
None
SourceAudio
ReferenceAudio
Label
Vector
Instruction
```

No todos los engines tienen que soportarlos.

---

# 33. Emoción automática inicial

Para IndexTTS-2.5, intentar como comportamiento inicial:

```text
VOICE IDENTITY
    = referencia limpia del speaker

EMOTION
    = audio original del segmento
```

si el adapter confirma que la versión de audio.cpp utilizada soporta ese tipo de control.

IMPORTANTE PARA EL AGENTE:

**No inventar nombres de parámetros de audio.cpp.**

Antes de implementar el mapping de IndexTTS:

- revisar documentación de la versión fijada;
- ejecutar `--help` / `--inspect`;
- comprobar opciones de request/session;
- escribir integration test.

La abstracción genérica debe existir aunque una versión concreta no exponga todos los controles.

---

# 34. Eventos no verbales

Diseñar TTS para que en el futuro soporte:

```text
[laugh]
[sigh]
[cough]
[breath]
[cry]
```

mediante:

```text
SupportsNonVerbalTags
```

No asumir que IndexTTS los soporta.

Un motor futuro podrá utilizarlos sin cambiar `DubSegment`.

---

# 35. Traducción

No traducir cada frase completamente aislada.

Enviar contexto limitado:

```text
previous segments
current segment
next segments
speaker
scene glossary
```

Pero devolver únicamente la traducción del segmento solicitado.

Usar IDs.

Ejemplo de output estructurado:

```json
{
  "segmentId": "seg_00124",
  "translation": "¿Dónde estabas?",
  "notes": null
}
```

Validar JSON.

Si falla:

- retry limitado;
- fallback parser;
- nunca escribir basura en el proyecto.

---

# 36. Prompt de traducción

Objetivos:

1. conservar significado;
2. conservar tono;
3. conservar registro;
4. mantener nombres;
5. mantener coherencia entre segmentos;
6. adaptar longitud;
7. sonar natural al hablar;
8. no añadir información.

El prompt debe diferenciar:

```text
literal translation
dubbing adaptation
```

La app usará por defecto:

```text
dubbing adaptation
```

---

# 37. Adaptación a duración

Cada segmento tiene:

```text
TargetDuration = End - Start
```

Primera traducción:

```text
source -> translation
```

Calcular una estimación de duración.

Si se desvía demasiado:

```text
LLM rewrite
```

Prompt conceptual:

```text
Reformula esta traducción manteniendo significado,
tono y naturalidad.

Debe poder pronunciarse en aproximadamente 2.8 segundos.

No añadas información.
```

---

# 38. Loop de duración

Algoritmo recomendado:

```text
target = 2.80 sec

1. Generate translation
2. Estimate spoken duration
3. Adapt translation if obviously too long/short
4. Generate TTS
5. Measure actual WAV
6. Compare

if error <= 5%
    accept

else if engine supports rate/duration
    regenerate/adjust within safe range

else if text is clearly responsible
    request one shorter/longer translation

else
    use subtle time stretch
```

Evitar loops infinitos.

Máximo inicial:

```text
2 intentos de traducción adaptativa
2 intentos TTS
```

configurable.

---

# 39. Time stretch

FFmpeg solo debe corregir pequeñas desviaciones.

Default recomendado:

```text
<= ±5%
```

Permitir ampliar en ajustes avanzados, pero avisar de degradación.

No convertir automáticamente una frase de 5 s en 2 s mediante `atempo`.

Eso debe resolverse con adaptación lingüística.

---

# 40. Cache incremental

Crítico para vídeos largos.

No regenerar 40 minutos porque el usuario cambió una frase.

Crear hashes.

Ejemplo TTS cache key:

```text
hash(
    engine_id,
    engine_version,
    model_id,
    text,
    language,
    voice_reference_hash,
    emotion_hash,
    speed,
    seed,
    advanced_options
)
```

Si no cambia:

```text
reuse WAV
```

---

# 41. Dependency invalidation

Ejemplo:

Usuario cambia:

```text
translation.text
```

Invalidar:

```text
TTS
duration-fit
mix
final render
```

NO invalidar:

```text
ASR
diarization
speaker reference
```

Crear explicitamente un dependency graph.

---

# 42. Estados del segmento

Ejemplo:

```text
Analyzed
Translated
TranslationEdited
TtsPending
TtsGenerated
TtsDirty
Ready
Error
```

No usar únicamente booleanos ambiguos.

---

# 43. Job system

Crear:

```csharp
IJobManager
```

Debe soportar:

- cola;
- progreso;
- cancelación;
- retries;
- mensajes;
- stage actual;
- segment actual.

Ejemplo UI:

```text
Generating voices
124 / 820
Speaker 3
00:13:21
```

---

# 44. Recuperación tras fallo

Si la app se cierra durante TTS segmento 400:

al volver a abrir:

```text
1-399 => cache válido
400 => incomplete
401+ => pending
```

No empezar desde cero.

Escribir archivos generados primero a:

```text
.tmp
```

y renombrar atómicamente cuando estén completos.

---

# 45. Mezcla final

No concatenar TTS.

Crear una pista temporal.

Para cada segmento:

```text
WAV
↓
place at segment.Start
↓
mix
```

Mantener overlaps.

---

# 46. Background bed

Si hay separación:

```text
instrumental.wav
+
dub track
=
final audio
```

Si hay varios stems:

```text
music
effects
ambience
dub
```

mezclarlos.

---

# 47. Loudness

Implementar inicialmente:

- peak protection;
- configurable gain de voces;
- loudness analysis;
- evitar clipping.

No imponer un LUFS único para todos los usos.

Ofrecer presets posteriormente:

```text
Web / YouTube
Film
Podcast
Custom
```

---

# 48. Preview

No exigir render completo para probar una frase.

Botones por segmento:

```text
Play original
Play translated TTS
Play in context
Regenerate
```

`Play in context` debería poder renderizar aproximadamente:

```text
2 s antes
segmento
2 s después
```

desde cache.

---

# 49. UI principal

Propuesta:

```text
┌──────────────────────────────────────────────────────────────┐
│ File  Project  Models  Settings                             │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│                     VIDEO PREVIEW                            │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ Timeline                                                     │
│ ───────────────────────────────────────────────────────────  │
│ Speaker 1  [=======]             [======]                    │
│ Speaker 2       [====]      [=========]                      │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ 00:12:41.200 - 00:12:44.100        Speaker 1                │
│                                                              │
│ Original                                                     │
│ Where the hell have you been?                                │
│                                                              │
│ Translation                                                  │
│ ¿Dónde demonios estabas?                                     │
│                                                              │
│ Voice: Speaker 1 reference                                   │
│ Emotion: Original segment                                    │
│ TTS: IndexTTS 2.5                                            │
│ Duration: 2.84 / 2.90 s                                      │
│                                                              │
│ [Original] [Dub] [Regenerate]                                │
└──────────────────────────────────────────────────────────────┘
```

---

# 50. Pantalla Models

Categorías:

```text
Transcription
Diarization
Alignment
Translation
Voice
Separation
```

Por modelo:

```text
Installed / not installed
size
version
license
languages
backend support
capabilities
```

Acciones:

```text
Install
Remove
Verify
Set default
```

---

# 51. Settings

## General

```text
Project directory
Cache directory
FFmpeg path
Model directory
```

## Hardware

```text
Backend: Auto / CUDA / Vulkan / HIP / Metal / CPU
Device
Max GPU jobs
CPU threads
```

## Pipeline

```text
Use source separation
Use forced alignment
Automatic speaker reference
Automatic emotion transfer
Duration tolerance
Time stretch maximum
```

---

# 52. Per-project engine configuration

Guardar algo similar:

```json
{
  "engines": {
    "transcription": "audio.cpp/qwen3-asr",
    "alignment": "audio.cpp/qwen3-forced-aligner",
    "diarization": "audio.cpp/nemotron-3-diar",
    "separation": "audio.cpp/bs-roformer",
    "translation": "llama.cpp/local-translator",
    "tts": "audio.cpp/index-tts-2.5"
  }
}
```

Los IDs no deben ser enums compilados.

---

# 53. Per-speaker y per-segment override

Permitir:

```text
Global TTS:
IndexTTS

Speaker 4:
Qwen3-TTS

Segment 401:
IndexTTS + custom settings
```

Orden:

```text
segment override
    >
speaker override
    >
project default
    >
application default
```

---

# 54. Engine options dinámicas

Para controles avanzados específicos de un modelo, el provider podrá exponer descriptores.

Ejemplo conceptual:

```csharp
public sealed record EngineOptionDescriptor(
    string Key,
    string DisplayName,
    EngineOptionType Type,
    object? DefaultValue,
    double? Min,
    double? Max,
    IReadOnlyList<string>? Values);
```

Esto permite que la UI muestre sliders/selects sin conocer IndexTTS.

---

# 55. Hardware detection

Crear:

```text
IHardwareDetector
```

Detectar:

- OS;
- architecture;
- RAM;
- GPUs;
- VRAM cuando sea posible;
- CUDA availability;
- Vulkan availability;
- Metal availability.

Resultado:

```text
HardwareProfile
```

El sistema puede recomendar:

```text
Q8 en 8 GB
F16 en GPU grande
CPU fallback
```

pero no bloquear arbitrariamente.

---

# 56. Process runner común

Crear:

```csharp
IProcessRunner
```

Usarlo para:

- ffmpeg;
- ffprobe;
- audiocpp_cli;
- audiocpp_model_manager;
- audiocpp_server;
- llama-server.

Características:

- args separados, no concatenar shell strings;
- stdout;
- stderr;
- exit code;
- cancellation;
- timeout opcional;
- process-tree kill;
- logs sanitizados.

Evitar `cmd.exe /c` y `bash -c` salvo necesidad justificada.

---

# 57. Seguridad

Los servidores locales:

```text
audiocpp_server
llama-server
```

deben escuchar únicamente:

```text
127.0.0.1
```

por defecto.

Usar puerto dinámico.

No aceptar conexiones de red externa.

No loguear texto sensible si el usuario activa:

```text
Privacy mode
```

---

# 58. Logging

Usar logging estructurado.

Niveles:

```text
Trace
Debug
Information
Warning
Error
```

Cada operación debe tener:

```text
ProjectId
JobId
Stage
SegmentId
EngineId
```

Nunca llenar los logs con matrices/model tensors.

---

# 59. Error model

No propagar errores de proceso como strings aleatorios.

Crear:

```csharp
EngineException
ModelNotInstalledException
UnsupportedCapabilityException
InsufficientMemoryException
RuntimeCrashedException
MediaProcessingException
ProjectFormatException
```

La UI debe dar errores entendibles.

---

# 60. audio.cpp: uso de sus capacidades actuales

En la fecha de este documento, `audio.cpp` ofrece actualmente, entre otras cosas:

- Qwen3-ASR;
- Qwen3 Forced Aligner;
- Nemotron 3 Diarization;
- IndexTTS-2 / 2.5;
- Qwen3-TTS;
- BS-RoFormer;
- HTDemucs;
- Mel-Band RoFormer;
- servidor HTTP;
- model manager nativo;
- builds CPU/CUDA/Vulkan/HIP/Metal según plataforma/modelo;
- C ABI opcional.

NO asumir que las APIs internas de `audio.cpp` serán estables.

Por eso:

1. fijar commit/tag de audio.cpp para cada release;
2. encapsularlo detrás de provider;
3. escribir tests de integración;
4. actualizar el adapter de forma aislada cuando upstream cambie.

---

# 61. Version pinning

Crear archivo:

```text
runtimes/versions.json
```

Ejemplo:

```json
{
  "audioCpp": {
    "version": "PINNED_VERSION_OR_COMMIT"
  },
  "llamaCpp": {
    "version": "PINNED_VERSION_OR_COMMIT"
  },
  "ffmpeg": {
    "version": "PINNED_VERSION"
  }
}
```

Nunca descargar `main/latest` silenciosamente en producción.

---

# 62. C ABI de audio.cpp

Arquitectura futura:

```text
DubbingApp
   │
   └── AudioCppNativeTransport
            │
            └── audiocpp shared library
```

Puede reducir overhead y simplificar lifecycle.

Pero NO debe ser requisito del primer MVP.

El transport debe ser intercambiable:

```text
HTTP
CLI
C ABI
```

sin cambiar `ITtsEngine`, `ITranscriptionEngine`, etc.

---

# 63. Cross-platform runtimes

RIDs principales:

```text
win-x64
linux-x64
osx-arm64
```

Secundario:

```text
osx-x64
```

No asumir paths con `\`.

Usar:

```csharp
Path.Combine(...)
```

No asumir extensión `.exe`.

Crear:

```text
IRuntimeLocator
```

---

# 64. Packaging

## Windows

Inicialmente:

- self-contained .NET publish;
- runtime binaries al lado de la app;
- Inno Setup para instalador.

## Linux

Primero:

- portable tar.gz.

Después:

- AppImage;
- deb opcional.

## macOS

Primero:

- `.app` bundle.

Después:

- DMG;
- signing/notarization.

No bloquear el MVP por code signing.

---

# 65. FFmpeg

Inicialmente permitir:

```text
bundled runtime
```

o:

```text
custom path
```

Comprobar la licencia del build concreto que se distribuya.

La aplicación debe poder arrancar aunque FFmpeg esté ausente y mostrar:

```text
FFmpeg runtime not installed
```

con opción de configuración.

---

# 66. Testing strategy

## Unit tests

Sin modelos.

Testear:

- timeline merge;
- duration logic;
- cache hashes;
- project migrations;
- invalidation graph;
- translation parsing;
- speaker assignment;
- engine selection;
- capability resolution.

---

## Integration tests

Con archivos de audio muy pequeños.

Separar por provider.

Marcar tests que requieren:

```text
AUDIOCPP_TEST_MODELS
CUDA
FFMPEG
```

para que CI normal no descargue gigabytes.

---

## Golden samples

Mantener pequeños fixtures:

```text
2 speakers
speaker overlap
silence
background music
short utterance
long utterance
```

No usar contenido con copyright dudoso.

---

# 67. Mock pipeline obligatorio

Antes de integrar modelos reales, debe funcionar:

```text
Import video
↓
Mock transcription
↓
Mock diarization
↓
Mock translation
↓
Mock TTS WAV
↓
Timeline
↓
Render
```

Esto valida arquitectura y UI independientemente de IA.

---

# 68. Métricas útiles

Guardar por job:

```text
audio duration
wall time
RTF
peak memory si está disponible
generated audio duration
target duration error
number of retries
```

Esto permitirá comparar motores futuros.

---

# 69. Benchmark abstraction

En el futuro:

```text
Engine Benchmark
```

Ejemplo:

```text
IndexTTS 2.5 Q8
RTF: 0.42
VRAM: 6.8 GB

FutureTTS Q6
RTF: 0.20
VRAM: 5.2 GB
```

No hacer benchmarks obligatorios para el MVP.

---

# 70. Traducción por lotes

Para rendimiento, permitir batching lógico.

Pero preservar IDs:

```json
[
  {
    "segmentId": "seg_100",
    "translation": "..."
  },
  {
    "segmentId": "seg_101",
    "translation": "..."
  }
]
```

Si una respuesta pierde IDs o devuelve número incorrecto de elementos:

```text
reject
retry smaller batch
```

---

# 71. Contexto de traducción

Usar un `TranslationContextBuilder`.

Debe suministrar:

- máximo N segmentos anteriores;
- máximo N posteriores;
- glossary;
- speaker;
- scene context opcional.

No mandar todo un largometraje en cada prompt.

---

# 72. Glossary

Proyecto debe permitir:

```text
John -> John
Starfleet -> Flota Estelar
ACME -> ACME
```

y expresiones:

```text
do not translate
preferred translation
```

Persistir en proyecto.

---

# 73. Traducciones bloqueadas

Si usuario edita manualmente:

```text
locked = true
```

Un re-run de traducción global no debe sobrescribirlo.

Botón explícito:

```text
Unlock / Re-translate
```

---

# 74. Speaker mapping manual

Debe poder:

```text
Merge speakers
Split incorrect assignment
Reassign segment
Rename speaker
```

Por ejemplo:

```text
speaker_02 + speaker_07 -> Character A
```

La referencia de voz debe actualizarse con control del usuario.

---

# 75. Overlapping speech

No ignorarlo.

Modelo de timeline debe soportar múltiples segmentos simultáneos.

No es necesario resolver separación perfecta de voces solapadas en el MVP.

Pero el formato no puede impedirlo.

---

# 76. Multi-channel audio

MVP:

- manejar correctamente mono/stereo;
- conservar canales al exportar cuando sea posible.

Futuro:

- explotar pistas 5.1;
- usar canal central para diálogo cuando corresponda;
- preservar surround.

No hardcodear 2 canales en el modelo de proyecto.

---

# 77. Subtítulos

Diseñar el proyecto para exportar después:

```text
SRT
VTT
ASS
```

Aun si el MVP solo implementa SRT.

Los `DubSegment` ya contienen tiempo + texto, por lo que debe ser sencillo.

---

# 78. Auto-save

Auto-save del proyecto con debounce.

Escritura segura:

```text
project.json.tmp
↓
fsync/close
↓
atomic rename
```

Mantener copia de backup reciente.

---

# 79. Undo/Redo

No es imprescindible para primer prototipo.

Pero las operaciones UI de edición deberían pasar por commands/services para añadirlo posteriormente.

No hacer modificaciones directas de archivos desde controles.

---

# 80. Primera versión funcional — alcance MVP

El MVP debe hacer esto de extremo a extremo:

1. Abrir un MP4/MKV.
2. Analizarlo con ffprobe.
3. Extraer audio.
4. Opcionalmente separar vocals/instrumental.
5. Transcribir.
6. Diarizar.
7. Fusionar transcript + speakers.
8. Mostrar segmentos editables.
9. Traducir al idioma destino.
10. Elegir referencia de voz por speaker.
11. Generar TTS.
12. Intentar transferir emoción cuando el TTS lo soporte.
13. Ajustar duración.
14. Crear pista doblada.
15. Mezclar con instrumental/background.
16. Mux al vídeo.
17. Guardar proyecto editable.
18. Reabrirlo sin recalcular todo.

---

# 81. NO incluir en MVP

No bloquear el MVP intentando hacer:

- lip-sync facial;
- generación de vídeo;
- cloud collaboration;
- marketplace de plugins;
- entrenamiento/fine-tuning;
- mobile;
- real-time dubbing;
- voice conversion compleja;
- editor DAW completo;
- subtítulos karaoke;
- traducción cloud obligatoria.

Preparar arquitectura, no implementar todavía.

---

# 82. Fase 0 — scaffold

Objetivo:

repositorio compilable.

Tareas:

- solución .NET;
- proyectos;
- DI;
- logging;
- tests;
- Avalonia shell;
- navegación básica;
- CI compile/test Windows/Linux/macOS.

Definition of done:

```text
dotnet build
dotnet test
```

funcionan sin modelos.

---

# 83. Fase 1 — Core y proyecto

Implementar:

- domain entities;
- engine contracts;
- capability system;
- engine registry;
- project format;
- JSON serialization;
- migrations base;
- atomic save;
- dirty tracking.

Testear ampliamente.

---

# 84. Fase 2 — FFmpeg

Implementar:

- runtime locator;
- process runner;
- ffprobe;
- extract WAV;
- cut segment;
- measure duration;
- mix;
- mux.

Crear integration tests pequeños.

---

# 85. Fase 3 — Mock pipeline

Implementar todos los engines mock.

Conseguir:

```text
video -> fake timeline -> fake translated speech -> output video
```

antes de integrar audio.cpp.

Si esto no funciona, no pasar a IA real.

---

# 86. Fase 4 — audio.cpp provider base

Implementar:

- runtime discovery;
- version check;
- process host;
- server health;
- HTTP transport;
- CLI transport;
- model listing;
- backend selection.

Todavía sin UI avanzada.

---

# 87. Fase 5 — ASR

Integrar primer:

```text
ITranscriptionEngine
```

con Qwen3-ASR.

Normalizar resultado.

Guardar cache.

Mostrar transcript.

---

# 88. Fase 6 — alignment

Integrar Qwen3 Forced Aligner.

Word timestamps deben entrar al formato común.

---

# 89. Fase 7 — diarización

Integrar Nemotron 3.

Crear:

```text
SpeakerTurn[]
```

Fusionar con ASR.

Mostrar speaker labels.

---

# 90. Fase 8 — traducción

Integrar llama.cpp.

Crear:

- server host;
- JSON structured responses;
- translation context;
- glossary;
- duration instructions;
- retries.

---

# 91. Fase 9 — TTS

Integrar `ITtsEngine` mediante audio.cpp.

Default:

```text
IndexTTS-2.5
```

Implementar:

- reference voice;
- language;
- seed si soportado;
- speed si soportado;
- emoción según capabilities reales;
- output WAV;
- duration measurement.

---

# 92. Fase 10 — speaker reference automation

Crear selector automático.

Añadir UI para:

```text
Preview reference
Choose another
Import external
```

---

# 93. Fase 11 — source separation

Integrar BS-RoFormer.

Debe ser opcional.

No cambiar pipeline superior.

---

# 94. Fase 12 — timing

Implementar:

- target duration;
- translation shortening/expansion;
- TTS retries;
- speaking rate;
- subtle atempo fallback.

Mostrar indicador:

```text
2.91 / 2.80
+3.9%
```

---

# 95. Fase 13 — editor UI

Añadir:

- video preview;
- segment list;
- waveform/timeline simplificado;
- speaker selector;
- translation editor;
- TTS options;
- regenerate;
- previews.

No intentar construir Adobe Premiere.

---

# 96. Fase 14 — rendering

Crear:

```text
DubRenderService
```

Input:

```text
project
segments
background
```

Output:

```text
dub_track.wav
```

Después:

```text
FinalMuxService
```

Output:

```text
dubbed.mp4
```

---

# 97. Fase 15 — Model UI

Instalar/eliminar/verificar modelos.

No asumir que todos provienen de Hugging Face.

Provider controla fuente y descarga.

---

# 98. Fase 16 — packaging

Generar:

```text
Windows x64 portable
Linux x64 portable
macOS arm64 .app
```

Después crear instaladores.

---

# 99. Definition of Done del MVP

El MVP está terminado cuando:

### Arquitectura

- Core no referencia motores concretos.
- Se puede registrar un segundo mock TTS sin cambiar pipeline.
- Se puede cambiar ASR por configuración.
- Capabilities controlan la UI.

### Proyecto

- guardar/reabrir funciona;
- cache funciona;
- editar una traducción solo invalida stages dependientes.

### IA

- ASR real funciona;
- diarización real funciona;
- traducción local funciona;
- TTS real funciona;
- al menos una estrategia de emoción funciona cuando el engine lo soporta.

### Media

- render de voz conserva timings;
- mezcla funciona;
- export produce vídeo reproducible.

### Plataformas

Como mínimo pruebas manuales:

```text
Windows x64
Linux x64
macOS arm64
```

si se dispone de hardware/CI.

---

# 100. Reglas para GPT-6 Astra al implementar

1. **No poner lógica de un modelo específico en `Dubbing.Core`.**
2. No hardcodear `IndexTTS-2.5` en ViewModels.
3. No hardcodear `Qwen3-ASR` en pipeline.
4. No hardcodear CUDA.
5. No ejecutar procesos externos desde UI.
6. No usar shell command strings si se pueden usar argumentos seguros.
7. Todo job largo debe aceptar `CancellationToken`.
8. Todo job largo debe reportar progreso.
9. Todo output caro debe poder cachearse.
10. Todo archivo de proyecto debe tener schema version.
11. Todo provider debe mapear a tipos comunes.
12. Todo model-specific option debe permanecer dentro del provider o `EngineOptions`.
13. Crear tests antes de depender de modelos gigantes.
14. No descargar modelos durante `dotnet test`.
15. No asumir que una feature upstream existe: verificarla contra la versión fijada.
16. No usar `latest/main` como dependencia reproducible de release.
17. No mezclar lógica de traducción con TTS.
18. No mezclar diarización con `TimelineBuilder`.
19. No mezclar FFmpeg con ViewModels.
20. Preferir código claro y sustituible a optimizaciones prematuras.

---

# 101. Primera tarea concreta que debe ejecutar el agente

Si el repositorio está vacío:

## Step 1

Crear solución y proyectos.

## Step 2

Crear Core con:

```text
EngineTask
EngineDescriptor
EngineCapabilities
ITranscriptionEngine
IDiarizationEngine
IAlignmentEngine
ITranslationEngine
ITtsEngine
ISeparationEngine
IEngineRegistry
```

## Step 3

Crear domain:

```text
DubbingProject
DubSegment
SpeakerProfile
EmotionDescriptor
EngineSelection
ProjectSettings
```

## Step 4

Crear Mock engines.

## Step 5

Crear tests de:

```text
engine registry
capability filtering
project serialization
project round trip
cache invalidation
```

## Step 6

Crear Avalonia shell que pueda:

```text
New project
Open project
Save project
Show configured engines
```

## Step 7

Solo después comenzar integración FFmpeg/audio.cpp.

---

# 102. Criterio arquitectónico para aceptar un nuevo TTS futuro

Supongamos que en 2027 aparece:

```text
AmazingTTS 4
```

La arquitectura es correcta si integrar ese motor requiere aproximadamente:

```text
1. Crear provider/adapter o descriptor.
2. Mapear TtsRequest -> AmazingTTS request.
3. Mapear output -> TtsResult.
4. Declarar capabilities.
5. Registrar engine.
```

Y NO requiere modificar:

```text
DubSegment
Translation pipeline
TimelineBuilder
RenderService
Project UI general
```

Puede añadirse una UI avanzada dinámica a partir de `EngineOptionDescriptor`.

---

# 103. Criterio arquitectónico para un nuevo ASR

Un nuevo ASR debe requerir:

```text
INewAsrAdapter : ITranscriptionEngine
```

y nada más.

Si tiene:

```text
word timestamps
speaker labels
streaming
```

declararlo en capabilities.

El pipeline utiliza lo disponible.

---

# 104. Criterio arquitectónico para un nuevo runtime completo

Ejemplo futuro:

```text
superaudio.cpp
```

Crear:

```text
Dubbing.Engines.SuperAudio
```

Puede implementar simultáneamente:

```text
ITranscriptionEngine
IDiarizationEngine
ITtsEngine
ISeparationEngine
```

Core permanece intacto.

---

# 105. Calidad de código

Preferir:

- immutable records cuando corresponda;
- nullable reference types;
- async/await real;
- small services;
- composition over inheritance;
- interfaces solo cuando representan boundary real;
- tests sobre behavior;
- DI;
- structured logging.

Evitar:

- `GodService`;
- static global state;
- Singleton con estado mutable salvo host/runtime bien controlado;
- ViewModels de 2000 líneas;
- strings mágicos repartidos por código;
- catch(Exception) silencioso;
- paths hardcodeados.

---

# 106. Performance

No cargar todo un vídeo en RAM.

Trabajar con:

- files;
- streams;
- chunks cuando el runtime lo permita.

Para timelines largos:

- virtualizar listas Avalonia;
- no crear waveform completo en memoria;
- precalcular peaks reducidos.

---

# 107. Reproducibilidad

Guardar en metadata del render:

```text
application version
engine IDs
model IDs
model versions
backend
important settings
```

No es necesario incrustarlo en vídeo inicialmente.

Sí guardarlo en el proyecto/log de render.

---

# 108. Privacidad y eliminación de temporales

El usuario debe poder:

```text
Clear generated cache
Clear model cache
Delete temporary audio
```

No borrar assets del proyecto sin confirmación.

---

# 109. Futuro: lip-sync

Debe implementarse como otro stage:

```text
IVideoLipSyncEngine
```

Input:

```text
source video
dub track / speech segments
```

Output:

```text
video track
```

No meter lip-sync dentro de TTS.

---

# 110. Futuro: speech-to-speech

Si un runtime futuro permite:

```text
speech -> translated speech
```

crear:

```text
ISpeechTranslationEngine
```

o un composite provider.

No romper pipeline existente.

---

# 111. Futuro: cloud providers

Podrán añadirse:

```text
OpenAI
Google
ElevenLabs
etc.
```

pero mediante adapters.

La UI debe indicar claramente:

```text
LOCAL
CLOUD
```

No mezclar credenciales con Core.

---

# 112. Referencias técnicas verificadas al crear este documento

Revisar siempre la documentación upstream antes de implementar porque cambia rápidamente.

## audio.cpp

Repositorio:

https://github.com/0xShug0/audio.cpp

Documentación de server:

https://github.com/0xShug0/audio.cpp/blob/main/app/server/README.md

Model manager:

https://github.com/0xShug0/audio.cpp/blob/main/docs/model_manager.md

Qwen3 ASR/TTS/aligner:

https://github.com/0xShug0/audio.cpp/blob/main/docs/models/qwen3.md

Audio tools / separation:

https://github.com/0xShug0/audio.cpp/blob/main/docs/audio_tools.md

GGUF status:

https://github.com/0xShug0/audio.cpp/blob/main/docs/gguf.md

En la fecha de esta especificación, upstream documenta:

- `audiocpp_server`;
- `/health`;
- `/v1/models`;
- `/v1/audio/speech`;
- `/v1/audio/transcriptions`;
- `/v1/audio/transcriptions/details`;
- model manager nativo;
- Qwen3-ASR;
- Qwen3 Forced Aligner;
- Nemotron 3 Diarization;
- IndexTTS-2/2.5;
- Qwen3-TTS;
- BS-RoFormer;
- HTDemucs;
- C ABI opcional;
- backends CPU/CUDA/Vulkan/HIP/Metal con cobertura dependiente del modelo.

---

# 113. Resumen ejecutivo para el agente

Construir primero una buena **plataforma de doblaje**, no una app de IndexTTS.

La arquitectura debe ser:

```text
                   ┌─────────────────────┐
                   │      DubbingApp     │
                   └──────────┬──────────┘
                              │
                         abstract APIs
                              │
          ┌───────────────────┼────────────────────┐
          │                   │                    │
          ▼                   ▼                    ▼
        ASR              Translation              TTS
          │                   │                    │
     provider/model       provider/model      provider/model
          │                   │                    │
     replaceable          replaceable         replaceable
```

El objetivo es que, cuando aparezca un modelo mejor dentro de seis meses, el usuario pueda seleccionarlo en:

```text
Settings -> Models
```

sin que haya que rediseñar el producto.

El MVP inicial utilizará principalmente:

```text
audio.cpp
    Qwen3-ASR
    Qwen3 Forced Aligner
    Nemotron 3 Diarization
    BS-RoFormer
    IndexTTS-2.5

llama.cpp
    local translation/adaptation

FFmpeg
    media processing
```

pero estos son **defaults**, no dependencias conceptuales.

**La modularidad y la capacidad de reemplazar motores tienen prioridad sobre cualquier optimización específica de un único modelo.**
