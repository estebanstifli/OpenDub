using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dubbing.App.Services;
using Dubbing.App.ViewModels;
using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Core.Media;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;
using Dubbing.Infrastructure.Projects;
using Dubbing.Infrastructure.Processes;
using Dubbing.Media.FFmpeg;
using Dubbing.App.Localization;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Models;
using Dubbing.Engines.AudioCpp;
using Dubbing.Engines.LlamaCpp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dubbing.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? services;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["Localizer"] = new TextCatalog();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var collection = new ServiceCollection();
            collection.AddLogging(builder => builder.AddConsole().AddProvider(new ActivityLoggerProvider()));
            var settings = new SettingsStore();
            var text = (TextCatalog)Resources["Localizer"]!; text.Load(settings.Current.UiLanguage);
            collection.AddSingleton(settings); collection.AddSingleton(text);
            collection.AddSingleton<IExecutionMetadata, ExecutionMetadata>();
            var catalog = new ModelCatalog(Path.Combine(AppContext.BaseDirectory, "engine-catalog"));
            collection.AddSingleton(catalog);
            collection.AddSingleton<RuntimeCoordinator>();
            collection.AddSingleton<IAudioCppTransport, AudioCppTransport>();
            foreach (var engine in MockEngineCatalog.Create()) collection.AddSingleton<IEngine>(engine);
            foreach (var model in catalog.Models.Where(m => m.Provider == "audio.cpp"))
                collection.AddSingleton<IEngine>(sp => model.Task switch
                {
                    EngineTask.Transcription => new AudioCppTranscriptionEngine(model, sp.GetRequiredService<IAudioCppTransport>(), sp.GetRequiredService<IMediaEngine>()),
                    EngineTask.Diarization => new AudioCppDiarizationEngine(model, sp.GetRequiredService<IAudioCppTransport>()),
                    EngineTask.Alignment => new AudioCppAlignmentEngine(model, sp.GetRequiredService<IAudioCppTransport>()),
                    EngineTask.Separation => new AudioCppSeparationEngine(model, sp.GetRequiredService<IAudioCppTransport>()),
                    EngineTask.Tts => new AudioCppTtsEngine(model, sp.GetRequiredService<IAudioCppTransport>(), sp.GetRequiredService<IMediaEngine>()),
                    _ => throw new InvalidDataException("Unsupported catalog engine task.")
                });
            collection.AddSingleton<IEngine, LlamaCppTranslationEngine>();
            collection.AddSingleton<IEngine>(sp => new OllamaTranslationEngine(sp.GetRequiredService<RuntimeCoordinator>()));
            collection.AddSingleton<IEngineRegistry, EngineRegistry>();
            collection.AddSingleton<ProjectJson>();
            collection.AddSingleton<IProjectStore, JsonProjectStore>();
            collection.AddSingleton<ProjectSession>();
            collection.AddSingleton<ProjectFactory>();
            collection.AddSingleton<EngineSelectionService>();
            collection.AddSingleton<TimelineBuilder>();
            collection.AddSingleton<TranslationService>();
            collection.AddSingleton<IProcessRunner, ProcessRunner>();
            collection.AddSingleton<IRuntimeLocator>(new RuntimeLocator());
            collection.AddSingleton(new FFmpegOptions(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));
            collection.AddSingleton<IMediaEngine, FFmpegMediaEngine>();
            collection.AddSingleton<ImportVideoService>();
            collection.AddSingleton<GenerateSpeechService>();
            collection.AddSingleton<DurationFitService>();
            collection.AddSingleton<DubRenderService>();
            collection.AddSingleton<ExportVideoService>();
            collection.AddSingleton<IModelManager, VerifiedModelManager>();
            collection.AddSingleton<WorkspaceServices>();
            collection.AddSingleton<IProjectDialogs>(new ProjectDialogs(window, text, settings));
            collection.AddSingleton<MainViewModel>();
            services = collection.BuildServiceProvider();
            window.DataContext = services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
